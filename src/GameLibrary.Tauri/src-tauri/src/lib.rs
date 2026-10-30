use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Manager, RunEvent, State, WindowEvent};
use tauri_plugin_opener::OpenerExt;

#[cfg(target_os = "windows")]
mod taskbar_icon;

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

/// bridge 响应等待上限。响应通过 transportId 与请求配对；超时/读写失败/协议损坏按
/// 「bridge 失效」处理：同一进程的所有 pending 失败并杀掉进程，下次请求重新拉起。
const REQUEST_TIMEOUT: Duration = Duration::from_secs(120);

struct BridgeProcess {
    child: Mutex<Child>,
    stdin: Mutex<Option<ChildStdin>>,
    pending: PendingRequests,
    generation: u64,
}

/// 同一 bridge 进程的等待表。transportId 独立于 Host 的 requestId，只用于私有 stdio 配对。
#[derive(Default)]
struct PendingRequests {
    pending: Mutex<HashMap<String, mpsc::Sender<Result<Value, String>>>>,
    failed: AtomicBool,
}

#[derive(Default)]
struct BridgeState {
    process: Mutex<Option<Arc<BridgeProcess>>>,
    requests_lane: Mutex<()>,
    events_lane: Mutex<()>,
}

/// 前端从 settings.get 读到 closeToTray 后经 set_close_to_tray 下发。
/// Rust 不查库——业务状态一律留在 Host，这里只保存窗口行为开关。
#[derive(Default)]
struct UiPrefsInner {
    close_to_tray: bool,
}

#[derive(Default)]
struct UiPrefs(Arc<Mutex<UiPrefsInner>>);

enum RequestFailure {
    /// Host 已明确返回 transportError：只拒绝本次 invoke，进程仍可继续服务。
    Transport(String),
    /// 超时、读写失败或协议损坏：同一进程的所有等待请求都必须失败并清理。
    Broken(String),
}

impl PendingRequests {
    fn register(
        &self,
        transport_id: &str,
        sender: mpsc::Sender<Result<Value, String>>,
    ) -> Result<(), String> {
        let mut pending = self
            .pending
            .lock()
            .map_err(|_| "TauriBridge 等待表锁异常".to_string())?;
        if self.failed.load(Ordering::SeqCst) {
            return Err("TauriBridge 已失效".to_string());
        }
        pending.insert(transport_id.to_string(), sender);
        Ok(())
    }

    fn unregister(&self, transport_id: &str) {
        if let Ok(mut pending) = self.pending.lock() {
            pending.remove(transport_id);
        }
    }

    fn complete(&self, transport_id: &str, reply: Result<Value, String>) -> bool {
        let sender = self
            .pending
            .lock()
            .ok()
            .and_then(|mut pending| pending.remove(transport_id));
        match sender {
            Some(sender) => {
                let _ = sender.send(reply);
                true
            }
            None => false,
        }
    }

    fn fail_all(&self, reason: &str) {
        let senders = {
            let Ok(mut pending) = self.pending.lock() else {
                return;
            };
            self.failed.store(true, Ordering::SeqCst);
            std::mem::take(&mut *pending)
        };
        for (_, sender) in senders {
            let _ = sender.send(Err(reason.to_string()));
        }
    }

    fn is_failed(&self) -> bool {
        self.failed.load(Ordering::SeqCst)
    }
}

impl BridgeProcess {
    fn request(&self, transport_id: &str, frame: &Value) -> Result<Value, RequestFailure> {
        if self.pending.is_failed() {
            return Err(RequestFailure::Broken("TauriBridge 已失效".to_string()));
        }

        let line = serde_json::to_string(frame)
            .map_err(|error| RequestFailure::Transport(error.to_string()))?;
        let (sender, receiver) = mpsc::channel();
        self.pending
            .register(transport_id, sender)
            .map_err(RequestFailure::Broken)?;

        let write_result = self
            .stdin
            .lock()
            .map_err(|_| "TauriBridge stdin 锁异常".to_string())
            .and_then(|mut stdin| {
                let stdin = stdin
                    .as_mut()
                    .ok_or_else(|| "TauriBridge stdin 已关闭".to_string())?;
                writeln!(stdin, "{line}")
                    .and_then(|_| stdin.flush())
                    .map_err(|error| format!("TauriBridge 写入失败：{error}"))
            });
        if let Err(reason) = write_result {
            self.pending.unregister(transport_id);
            return Err(RequestFailure::Broken(reason));
        }

        match receiver.recv_timeout(REQUEST_TIMEOUT) {
            Ok(Ok(value)) if !self.pending.is_failed() => Ok(value),
            Ok(Ok(_)) => Err(RequestFailure::Broken("TauriBridge 已失效".to_string())),
            Ok(Err(message)) if !self.pending.is_failed() => {
                Err(RequestFailure::Transport(message))
            }
            Ok(Err(message)) => Err(RequestFailure::Broken(message)),
            Err(RecvTimeoutError::Timeout) => {
                self.pending.unregister(transport_id);
                Err(RequestFailure::Broken(format!(
                    "TauriBridge 在 {} 秒内未响应，可能后台服务无响应",
                    REQUEST_TIMEOUT.as_secs()
                )))
            }
            Err(RecvTimeoutError::Disconnected) => {
                self.pending.unregister(transport_id);
                Err(RequestFailure::Broken("TauriBridge 已退出".to_string()))
            }
        }
    }

    fn terminate(&self) {
        self.pending.fail_all("TauriBridge 已退出");
        if let Ok(mut child) = self.child.lock() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }

    fn close_input(&self) {
        if let Ok(mut stdin) = self.stdin.try_lock() {
            stdin.take();
        }
    }
}

/// 解析私有 stdio 响应。损坏、缺少 transportId 或同时/都不带 result 与 transportError
/// 都视为协议失效，由调用方终止该进程而不猜测归属。
fn parse_response_line(line: &str) -> Result<(String, Result<Value, String>), String> {
    let value: Value =
        serde_json::from_str(line).map_err(|_| "TauriBridge 返回了无法解析的响应".to_string())?;
    let object = value
        .as_object()
        .ok_or_else(|| "TauriBridge 返回了非对象响应".to_string())?;
    let transport_id = object
        .get("transportId")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| "TauriBridge 响应缺少 transportId".to_string())?
        .to_string();

    match (
        object.get("result").filter(|value| !value.is_null()),
        object
            .get("transportError")
            .filter(|value| !value.is_null()),
    ) {
        (Some(result), None) => Ok((transport_id, Ok(result.clone()))),
        (None, Some(error)) => {
            let code = error
                .get("code")
                .and_then(Value::as_str)
                .ok_or_else(|| "TauriBridge transportError 缺少 code".to_string())?;
            let message = error
                .get("message")
                .and_then(Value::as_str)
                .ok_or_else(|| "TauriBridge transportError 缺少 message".to_string())?;
            let message = if message.is_empty() {
                code.to_string()
            } else {
                message.to_string()
            };
            Ok((transport_id, Err(message)))
        }
        _ => Err("TauriBridge 响应必须且只能包含 result 或 transportError".to_string()),
    }
}

/// 独立线程读取 bridge stdout 并按 transportId 投递，主调用方因此可以并发等待且不受
/// 写入顺序影响。未知/损坏响应会失败该进程的全部 pending 并终止进程。
fn spawn_response_reader(stdout: ChildStdout, process: Arc<BridgeProcess>) {
    std::thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        loop {
            let mut line = String::new();
            match reader.read_line(&mut line) {
                Ok(0) => break,
                Ok(_) => {
                    let trimmed = line.trim();
                    if trimmed.is_empty() {
                        process.pending.fail_all("TauriBridge 返回空响应");
                        process.terminate();
                        return;
                    }
                    match parse_response_line(trimmed) {
                        Ok((transport_id, reply)) => {
                            if !process.pending.complete(&transport_id, reply) {
                                process
                                    .pending
                                    .fail_all("TauriBridge 返回了未知 transportId 的响应");
                                process.terminate();
                                return;
                            }
                        }
                        Err(reason) => {
                            process.pending.fail_all(&reason);
                            process.terminate();
                            return;
                        }
                    }
                }
                Err(_) => {
                    process.pending.fail_all("TauriBridge 读取失败");
                    process.terminate();
                    return;
                }
            }
        }
        process.pending.fail_all("TauriBridge 已退出");
    });
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

fn spawn_bridge(app: &AppHandle) -> Result<Arc<BridgeProcess>, String> {
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
        .args(["--parent-pid", &std::process::id().to_string()])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::inherit())
        .current_dir(path.parent().unwrap_or_else(|| Path::new(".")))
        .spawn()
        .map_err(|error| format!("启动 TauriBridge 失败：{error}"))?;
    let stdin = child.stdin.take().ok_or("TauriBridge stdin 不可用")?;
    let stdout = child.stdout.take().ok_or("TauriBridge stdout 不可用")?;
    static NEXT_GENERATION: AtomicU64 = AtomicU64::new(1);
    let process = Arc::new(BridgeProcess {
        child: Mutex::new(child),
        stdin: Mutex::new(Some(stdin)),
        pending: PendingRequests::default(),
        generation: NEXT_GENERATION.fetch_add(1, Ordering::Relaxed),
    });
    spawn_response_reader(stdout, Arc::clone(&process));
    Ok(process)
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

fn next_transport_id(generation: u64) -> String {
    static NEXT_TRANSPORT_ID: AtomicU64 = AtomicU64::new(1);
    format!(
        "{generation}-{}",
        NEXT_TRANSPORT_ID.fetch_add(1, Ordering::Relaxed)
    )
}

fn ensure_bridge(state: &BridgeState, app: &AppHandle) -> Result<Arc<BridgeProcess>, String> {
    let mut slot = state
        .process
        .lock()
        .map_err(|_| "TauriBridge 状态锁异常".to_string())?;
    if let Some(current) = slot.as_ref() {
        let exited = current
            .child
            .lock()
            .map(|mut child| child.try_wait().ok().flatten().is_some())
            .unwrap_or(true);
        if !exited && !current.pending.is_failed() {
            return Ok(Arc::clone(current));
        }
    }

    if let Some(stale) = slot.take() {
        stale.pending.fail_all("TauriBridge 已退出");
        stale.terminate();
    }
    let process = spawn_bridge(app)?;
    *slot = Some(Arc::clone(&process));
    Ok(process)
}

/// 只清理传进来的那个进程。若它已被新进程替换，仍会失败它自己的 pending，但绝不会
/// 清空或杀掉当前槽位里的新进程。
fn discard_process(state: &BridgeState, process: &Arc<BridgeProcess>, reason: &str) {
    if let Ok(mut slot) = state.process.lock() {
        if slot
            .as_ref()
            .is_some_and(|current| Arc::ptr_eq(current, process))
        {
            *slot = None;
        }
    }
    process.pending.fail_all(reason);
    process.terminate();
}

/// `#[tauri::command(async)]` 使该命令在独立线程执行而非主线程——否则阻塞式等待 bridge
/// 响应会卡住整个 Tauri 事件循环（窗口拖动、其它命令全部排队）。
/// 普通请求与 events.wait 各持一把 lane 锁，允许长轮询与普通操作同时在途，总上限两个。
#[tauri::command(async)]
fn bridge_request(
    state: State<'_, BridgeState>,
    app: AppHandle,
    request: Value,
) -> Result<Value, String> {
    let is_events = request.get("operationId").and_then(Value::as_str) == Some("events.wait");
    let lane = if is_events {
        &state.events_lane
    } else {
        &state.requests_lane
    };
    let _lane_guard = lane.lock().map_err(|_| "TauriBridge 状态锁异常")?;

    let process = ensure_bridge(&state, &app)?;
    let transport_id = next_transport_id(process.generation);
    let frame = json!({ "transportId": &transport_id, "request": request });

    match process.request(&transport_id, &frame) {
        Ok(result) => Ok(result),
        Err(RequestFailure::Transport(message)) => Err(message),
        Err(RequestFailure::Broken(message)) => {
            // 超时/断连/协议损坏后该进程的配对已不可信：失败所有 pending 并清理。
            discard_process(&state, &process, &message);
            Err(message)
        }
    }
}

#[tauri::command(async)]
fn bridge_status(state: State<'_, BridgeState>) -> Result<Value, String> {
    let mut slot = state
        .process
        .lock()
        .map_err(|_| "TauriBridge 状态锁异常".to_string())?;
    let running = slot.as_mut().is_some_and(|process| {
        !process.pending.is_failed()
            && process
                .child
                .lock()
                .map(|mut child| child.try_wait().ok().flatten().is_none())
                .unwrap_or(false)
    });
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

#[tauri::command]
fn set_ui_language(app: AppHandle, language: &str) -> Result<(), String> {
    let (show_text, quit_text) = match language {
        "zh-CN" => ("显示 GameLibrary", "退出"),
        "zh-TW" => ("顯示 GameLibrary", "結束"),
        "en" => ("Show GameLibrary", "Quit"),
        "ja" => ("GameLibrary を表示", "終了"),
        _ => return Err("Unsupported UI language".into()),
    };
    let show = MenuItem::with_id(&app, "show", show_text, true, None::<&str>)
        .map_err(|e| e.to_string())?;
    let quit = MenuItem::with_id(&app, "quit", quit_text, true, None::<&str>)
        .map_err(|e| e.to_string())?;
    let menu = Menu::with_items(&app, &[&show, &quit]).map_err(|e| e.to_string())?;
    if let Some(tray) = app.tray_by_id("main-tray") {
        tray.set_menu(Some(menu)).map_err(|e| e.to_string())?;
    }
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
            #[cfg(target_os = "windows")]
            if let Some(window) = app.get_webview_window("main") {
                taskbar_icon::set_taskbar_icon(window.hwnd()?.0)
                    .map_err(|error| format!("taskbar icon setup: {error}"))?;
            }
            build_tray(app.handle()).map_err(|error| format!("tray setup: {error}"))?;
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
            set_ui_language,
            show_main_window
        ])
        .build(tauri::generate_context!())
        .expect("error while building GameLibrary Tauri app")
        .run(|app, event| {
            if matches!(event, RunEvent::Exit) {
                if let Some(state) = app.try_state::<BridgeState>() {
                    // Parent monitoring stops Host; avoid waiting for busy long-poll lanes.
                    // The reader owns another Arc; explicitly close stdin for graceful shutdown.
                    if let Ok(mut process) = state.process.try_lock() {
                        if let Some(process) = process.take() {
                            process.close_input();
                        }
                    }
                }
            }
        });
}

#[cfg(test)]
mod bridge_transport_tests {
    use super::{parse_response_line, PendingRequests};
    use serde_json::{json, Value};
    use std::sync::mpsc;
    use std::time::Duration;

    fn register(
        pending: &PendingRequests,
        transport_id: &str,
    ) -> mpsc::Receiver<Result<Value, String>> {
        let (sender, receiver) = mpsc::channel();
        pending.register(transport_id, sender).unwrap();
        receiver
    }

    fn result_frame(transport_id: &str, request_id: &str) -> String {
        json!({
            "transportId": transport_id,
            "result": { "requestId": request_id, "ok": true }
        })
        .to_string()
    }

    #[test]
    fn routes_out_of_order_responses_by_transport_id() {
        let pending = PendingRequests::default();
        let first = register(&pending, "transport-a");
        let second = register(&pending, "transport-b");

        let (id, reply) = parse_response_line(&result_frame("transport-b", "request-b")).unwrap();
        assert!(pending.complete(&id, reply));
        let (id, reply) = parse_response_line(&result_frame("transport-a", "request-a")).unwrap();
        assert!(pending.complete(&id, reply));

        assert_eq!(first.try_recv().unwrap().unwrap()["requestId"], "request-a");
        assert_eq!(
            second.try_recv().unwrap().unwrap()["requestId"],
            "request-b"
        );
    }

    #[test]
    fn late_response_for_removed_transport_fails_pending_and_blocks_new_requests() {
        let pending = PendingRequests::default();
        let current = register(&pending, "transport-current");
        let (id, reply) =
            parse_response_line(&result_frame("transport-expired", "request-old")).unwrap();
        assert!(!pending.complete(&id, reply));
        pending.fail_all("迟到的响应");

        assert!(current.try_recv().unwrap().is_err());
        let (sender, _receiver) = mpsc::channel();
        assert!(pending.register("transport-new", sender).is_err());
    }

    #[test]
    fn transport_error_is_delivered_without_failing_other_requests() {
        let pending = PendingRequests::default();
        let (sender, receiver) = mpsc::channel();
        pending.register("transport-error", sender).unwrap();

        let (id, reply) = parse_response_line(
            &json!({
                "transportId": "transport-error",
                "transportError": { "code": "HostUnavailable", "message": "宿主不可用" }
            })
            .to_string(),
        )
        .unwrap();
        assert!(pending.complete(&id, reply));
        assert_eq!(receiver.try_recv().unwrap().unwrap_err(), "宿主不可用");
        assert!(!pending.is_failed());
    }

    #[test]
    fn disconnect_fails_every_pending_request() {
        let pending = PendingRequests::default();
        let first = register(&pending, "transport-a");
        let second = register(&pending, "transport-b");
        pending.fail_all("TauriBridge 已退出");

        assert_eq!(
            first
                .recv_timeout(Duration::from_secs(1))
                .unwrap()
                .unwrap_err(),
            "TauriBridge 已退出"
        );
        assert_eq!(
            second
                .recv_timeout(Duration::from_secs(1))
                .unwrap()
                .unwrap_err(),
            "TauriBridge 已退出"
        );
    }

    #[test]
    fn rejects_corrupt_or_ambiguous_frames() {
        assert!(parse_response_line("not-json").is_err());
        assert!(parse_response_line(&json!({ "transportId": "x" }).to_string()).is_err());
        assert!(parse_response_line(
            &json!({
                "transportId": "x",
                "result": { "ok": true },
                "transportError": { "code": "c", "message": "m" }
            })
            .to_string()
        )
        .is_err());
        assert!(parse_response_line(
            &json!({
                "transportId": "x",
                "transportError": { "code": "HostUnavailable" }
            })
            .to_string()
        )
        .is_err());
    }
}
