use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::Mutex;
use tauri::{AppHandle, Manager, State};

struct BridgeProcess {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
}

#[derive(Default)]
struct BridgeState(Mutex<Option<BridgeProcess>>);

impl BridgeProcess {
    fn request(&mut self, request: &Value) -> Result<Value, String> {
        let line = serde_json::to_string(request).map_err(|error| error.to_string())?;
        writeln!(self.stdin, "{line}").map_err(|error| error.to_string())?;
        self.stdin.flush().map_err(|error| error.to_string())?;

        let mut response = String::new();
        self.stdout
            .read_line(&mut response)
            .map_err(|error| error.to_string())?;
        if response.trim().is_empty() {
            return Err("TauriBridge 返回空响应".to_string());
        }

        serde_json::from_str(response.trim()).map_err(|error| error.to_string())
    }
}

fn bridge_candidates(app: &AppHandle) -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    if let Ok(value) = std::env::var("GAMELIBRARY_TAURI_BRIDGE_EXE") {
        candidates.push(PathBuf::from(value));
    }

    if let Ok(current) = std::env::current_exe() {
        if let Some(parent) = current.parent() {
            candidates.push(parent.join("GameLibrary.TauriBridge.exe"));
            candidates.push(parent.join("GameLibrary.TauriBridge-x86_64-pc-windows-msvc.exe"));
        }
    }

    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    for configuration in ["Release", "Debug"] {
        candidates.push(manifest_dir.join(format!(
            "../../GameLibrary.TauriBridge/bin/{configuration}/net10.0-windows/GameLibrary.TauriBridge.exe"
        )));
    }

    if let Ok(resource_dir) = app.path().resource_dir() {
        let binaries = resource_dir.join("binaries");
        candidates.push(binaries.join("GameLibrary.TauriBridge-x86_64-pc-windows-msvc.exe"));
        if let Ok(entries) = std::fs::read_dir(&binaries) {
            candidates.extend(entries.flatten().map(|entry| entry.path()).filter(|path| {
                path.file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| {
                        name.starts_with("GameLibrary.TauriBridge") && name.ends_with(".exe")
                    })
            }));
        }
    }

    candidates
}

fn spawn_bridge(app: &AppHandle) -> Result<BridgeProcess, String> {
    let path = bridge_candidates(app)
        .into_iter()
        .find(|candidate| candidate.is_file())
        .ok_or_else(|| "找不到 GameLibrary.TauriBridge.exe；请先构建 sidecar".to_string())?;
    let mut command = Command::new(&path);
    if let Some(host_path) = host_candidates()
        .into_iter()
        .find(|candidate| candidate.is_file())
    {
        command.env("GAMELIBRARY_HOST_EXE", host_path);
    }
    let mut child = command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::inherit())
        .current_dir(path.parent().unwrap_or_else(|| Path::new(".")))
        .spawn()
        .map_err(|error| format!("启动 TauriBridge 失败：{error}"))?;
    let stdin = child.stdin.take().ok_or("TauriBridge stdin 不可用")?;
    let stdout = child.stdout.take().ok_or("TauriBridge stdout 不可用")?;
    Ok(BridgeProcess {
        child,
        stdin,
        stdout: BufReader::new(stdout),
    })
}

fn host_candidates() -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    if let Ok(value) = std::env::var("GAMELIBRARY_HOST_EXE") {
        candidates.push(PathBuf::from(value));
    }
    if let Ok(current) = std::env::current_exe() {
        if let Some(parent) = current.parent() {
            candidates.push(parent.join("GameLibrary.Host.exe"));
            candidates.push(parent.join("GameLibrary.Host-x86_64-pc-windows-msvc.exe"));
        }
    }
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    for configuration in ["Release", "Debug"] {
        candidates.push(manifest_dir.join(format!(
            "../../GameLibrary.Host/bin/{configuration}/net10.0-windows/GameLibrary.Host.exe"
        )));
    }
    candidates
}

#[tauri::command]
fn bridge_request(
    state: State<'_, BridgeState>,
    app: AppHandle,
    request: Value,
) -> Result<Value, String> {
    let mut bridge = state.0.lock().map_err(|_| "TauriBridge 状态锁异常")?;
    if bridge
        .as_mut()
        .is_some_and(|process| process.child.try_wait().ok().flatten().is_some())
    {
        *bridge = None;
    }
    if bridge.is_none() {
        *bridge = Some(spawn_bridge(&app)?);
    }

    bridge
        .as_mut()
        .expect("bridge initialized")
        .request(&request)
}

#[tauri::command]
fn bridge_status(state: State<'_, BridgeState>) -> Result<Value, String> {
    let mut bridge = state.0.lock().map_err(|_| "TauriBridge 状态锁异常")?;
    let running = bridge
        .as_mut()
        .map(|process| {
            process
                .child
                .try_wait()
                .map(|state| state.is_none())
                .unwrap_or(false)
        })
        .unwrap_or(false);
    Ok(json!({ "running": running }))
}

#[tauri::command]
fn resolve_data_directory(requested: Option<String>) -> String {
    requested
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| {
            std::env::var("LOCALAPPDATA")
                .map(|value| format!(r#"{value}\GameLibrary"#))
                .unwrap_or_else(|_| "C:\\Users\\Public\\GameLibrary".to_string())
        })
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let builder = tauri::Builder::default()
        .manage(BridgeState::default())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init());

    #[cfg(debug_assertions)]
    let builder = builder.plugin(tauri_plugin_mcp_bridge::init());

    builder
        .invoke_handler(tauri::generate_handler![
            bridge_request,
            bridge_status,
            resolve_data_directory
        ])
        .build(tauri::generate_context!())
        .expect("error while building GameLibrary Tauri app")
        .run(|app, event| {
            if matches!(event, tauri::RunEvent::Exit) {
                if let Some(state) = app.try_state::<BridgeState>() {
                    if let Ok(mut bridge) = state.0.lock() {
                        if let Some(mut process) = bridge.take() {
                            let _ = process.child.kill();
                        }
                    }
                }
            }
        });
}
