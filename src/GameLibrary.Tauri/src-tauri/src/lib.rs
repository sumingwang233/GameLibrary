use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Manager, RunEvent, State, WindowEvent};
use tauri_plugin_opener::OpenerExt;

// 只允许已有目录；不向前端开放可执行文件或任意 URL 的本地打开权限。
fn validate_game_directory(path: &str) -> Result<PathBuf, String> {
    let directory = PathBuf::from(path);
    if !directory.is_absolute() || !directory.is_dir() {
        return Err(format!("游戏目录不存在或无法访问：{path}"));
    }
    Ok(directory)
}

#[tauri::command]
fn open_game_directory(app: AppHandle, path: String) -> Result<(), String> {
    let directory = validate_game_directory(&path)?;
    app.opener()
        .open_path(directory.to_string_lossy(), None::<&str>)
        .map_err(|error| format!("打开游戏目录失败：{error}"))
}

#[cfg(test)]
mod directory_tests {
    use super::validate_game_directory;

    #[test]
    fn accepts_existing_absolute_directory_but_rejects_files_and_relative_paths() {
        let directory = std::env::temp_dir().join(format!(
            "gamelibrary-目录-[LunaSoft]-{}",
            std::process::id()
        ));
        std::fs::create_dir_all(&directory).unwrap();
        let file = directory.join("Game.exe");
        std::fs::write(&file, b"test").unwrap();
        assert!(validate_game_directory(directory.to_str().unwrap()).is_ok());
        assert!(validate_game_directory(file.to_str().unwrap()).is_err());
        assert!(validate_game_directory(".").is_err());
        assert!(validate_game_directory(directory.join("missing").to_str().unwrap()).is_err());
        std::fs::remove_file(file).unwrap();
        std::fs::remove_dir(directory).unwrap();
    }
}

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// bridge 响应等待上限。请求-响应严格一对一，一旦超时就无法保证下一条响应仍对应本次请求，
/// 因此超时按「bridge 挂死」处理：杀掉进程，下次请求重新拉起。
const REQUEST_TIMEOUT: Duration = Duration::from_secs(120);

struct BridgeProcess {
    child: Child,
    stdin: ChildStdin,
    responses: Receiver<String>,
}

#[derive(Default)]
struct BridgeState(Mutex<Option<BridgeProcess>>);

/// 前端从 settings.get 读到 closeToTray 后经 set_close_to_tray 下发。
/// Rust 不查库——业务状态一律留在 Host，这里只保存窗口行为开关。
#[derive(Default)]
struct UiPrefsInner {
    close_to_tray: bool,
}

#[derive(Default)]
struct UiPrefs(Arc<Mutex<UiPrefsInner>>);

impl BridgeProcess {
    fn request(&mut self, request: &Value) -> Result<Value, String> {
        let line = serde_json::to_string(request).map_err(|error| error.to_string())?;
        writeln!(self.stdin, "{line}").map_err(|error| error.to_string())?;
        self.stdin.flush().map_err(|error| error.to_string())?;

        let response = match self.responses.recv_timeout(REQUEST_TIMEOUT) {
            Ok(response) => response,
            Err(RecvTimeoutError::Timeout) => {
                return Err(format!(
                    "TauriBridge 在 {} 秒内未响应，可能后台服务无响应",
                    REQUEST_TIMEOUT.as_secs()
                ));
            }
            Err(RecvTimeoutError::Disconnected) => return Err("TauriBridge 已退出".to_string()),
        };

        if response.trim().is_empty() {
            return Err("TauriBridge 返回空响应".to_string());
        }

        serde_json::from_str(response.trim()).map_err(|error| error.to_string())
    }
}

/// 独立线程读取 bridge stdout 并按行投递，使主调用方可以用 recv_timeout 施加超时。
/// 子进程退出时 read_line 返回 0，线程自然结束并让 Receiver 进入 Disconnected。
fn spawn_response_reader(stdout: ChildStdout) -> Receiver<String> {
    let (sender, receiver) = mpsc::channel();
    std::thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        loop {
            let mut line = String::new();
            match reader.read_line(&mut line) {
                Ok(0) => break,
                Ok(_) => {
                    if sender.send(line).is_err() {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    });
    receiver
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
    #[cfg(windows)]
    command.creation_flags(CREATE_NO_WINDOW);
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
        responses: spawn_response_reader(stdout),
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

fn discard_bridge(bridge: &mut Option<BridgeProcess>) {
    if let Some(mut stale) = bridge.take() {
        let _ = stale.child.kill();
        let _ = stale.child.wait();
    }
}

/// `#[tauri::command(async)]` 使该命令在独立线程执行而非主线程——否则阻塞式等待 bridge
/// 响应会卡住整个 Tauri 事件循环（窗口拖动、其它命令全部排队）。
#[tauri::command(async)]
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

    let result = bridge
        .as_mut()
        .expect("bridge initialized")
        .request(&request);

    if result.is_err() {
        // 超时或断连后请求-响应配对已不可信，丢弃进程避免读到错位的响应。
        discard_bridge(&mut bridge);
    }

    result
}

#[tauri::command(async)]
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

#[tauri::command]
fn set_close_to_tray(prefs: State<'_, UiPrefs>, enabled: bool) -> Result<(), String> {
    let mut guard = prefs.0.lock().map_err(|_| "UI 偏好状态锁异常")?;
    guard.close_to_tray = enabled;
    Ok(())
}

/// 置前主窗口：托盘"显示"、单实例唤醒与前端（v1.5 feat-2：launch.exited 事件到达时
/// 经 events.read 轮询发现游戏退出 → invoke show_main_window）共用同一实现。
#[tauri::command]
fn show_main_window(app: AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
    }
}

fn close_to_tray_enabled(prefs: &UiPrefs) -> bool {
    prefs
        .0
        .lock()
        .map(|guard| guard.close_to_tray)
        .unwrap_or(false)
}

fn build_tray(app: &AppHandle) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "显示 GameLibrary", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &quit])?;

    let mut builder = TrayIconBuilder::with_id("main-tray")
        .tooltip("GameLibrary")
        .menu(&menu)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => show_main_window(app.clone()),
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main_window(tray.app_handle().clone());
            }
        });

    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    } else if let Ok(icon) = tauri::image::Image::from_bytes(include_bytes!("../icons/icon.ico")) {
        builder = builder.icon(icon);
    }

    builder.build(app)?;
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let builder = tauri::Builder::default()
        // single-instance 必须最先注册，否则第二个实例会先完成其它初始化再退出。
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_main_window(app.clone());
        }))
        .manage(BridgeState::default())
        .manage(UiPrefs::default())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init());

    #[cfg(debug_assertions)]
    let builder = builder.plugin(tauri_plugin_mcp_bridge::init());

    builder
        .setup(|app| {
            build_tray(app.handle())?;
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                if window.label() != "main" {
                    return;
                }
                let want_tray = window
                    .app_handle()
                    .try_state::<UiPrefs>()
                    .is_some_and(|prefs| close_to_tray_enabled(&prefs));
                if want_tray {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            bridge_request,
            bridge_status,
            resolve_data_directory,
            open_game_directory,
            set_close_to_tray,
            show_main_window
        ])
        .build(tauri::generate_context!())
        .expect("error while building GameLibrary Tauri app")
        .run(|app, event| {
            if matches!(event, RunEvent::Exit) {
                if let Some(state) = app.try_state::<BridgeState>() {
                    if let Ok(mut bridge) = state.0.lock() {
                        discard_bridge(&mut bridge);
                    }
                }
            }
        });
}
