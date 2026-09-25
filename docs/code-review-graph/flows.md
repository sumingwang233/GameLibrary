# GameLibrary 关键流程图

下面的流程图来自 `code-review-graph` 的执行流快照。完整路径可用 `get_flow_tool` 按入口名称展开。

## 1. Host 启动与库打开

证据入口：`GameLibrary.Host/Program.cs::Program.Main`、`HostRuntime.StartAsync`、`SingleInstanceGuard.TryAcquire`、`HostRuntime.OpenLibraryAsync`、`HostRuntime.WirePersistence`、`PipeServer.Start`。

```mermaid
flowchart TD
    Main[Program.Main\nHost/Program.cs:14-89]
    Start[HostRuntime.StartAsync\nHostRuntime.cs:37-107]
    Resolve[DataDirectory.Resolve\nContracts/Ipc/DataDirectory.cs:12-92]
    Guard[SingleInstanceGuard.TryAcquire\nHost/Hosting/SingleInstanceGuard.cs:26-55]
    Open[HostRuntime.OpenLibraryAsync\nHostRuntime.cs:296-327]
    Wire[HostRuntime.WirePersistence\nHostRuntime.cs:114-206]
    Pipe[PipeServer.Start\nHost/Ipc/PipeServer.cs:33-36]
    Scan[RunReconcileScan / ReconcileService.CheckGames]
    Ready[Host ready for IPC operations]

    Main --> Start --> Resolve --> Guard
    Guard -->|acquired| Open --> Wire --> Pipe --> Scan --> Ready
    Guard -->|already running| Exit[Return single-instance result]
```

`Main` 流图谱规模为 466 个节点、深度 15；上图只显示启动阶段的稳定边界。

## 2. 桌面连接与事件轮询

证据入口：`MainWindow.ConnectAsync`（`MainWindow.Connection.cs:23-58`）、`HostProcessLauncher.EnsureStartedAsync`、`HostConnection.ConnectAsync`、`HandshakeAsync`、`IpcFrame.WriteJsonAsync`、`MainWindow.PollEventsAsync`（`MainWindow.Connection.cs:188-226`）。

```mermaid
sequenceDiagram
    participant W as MainWindow
    participant L as HostProcessLauncher
    participant C as HostConnection
    participant H as Host
    participant F as IpcFrame

    W->>L: EnsureStartedAsync
    L->>H: Start or reuse local Host
    W->>C: ConnectAsync
    C->>H: named pipe connect
    C->>F: HandshakeAsync / WriteJsonAsync
    H-->>C: handshake + operation result
    W->>C: LoadSettingsAsync / RefreshAsync
    loop event polling
        W->>C: PollEventsAsync -> InvokeAsync
        C->>H: events.read / games.list
        H-->>W: refresh data and status
    end
```

`ConnectAsync` 的图谱流为 28 个节点、深度 5、criticality `0.8339`；`PollEventsAsync` 为 21 个节点、深度 6、criticality `0.8529`。

## 3. 扫描与候选审查

证据入口：桌面 `MainWindow.OnScanClick`（`MainWindow.Scanning.cs:319-487`）和 Host `ScanningHandler.ScanStart`（`Host/Hosting/ScanningHandler.cs:52-167`）。图谱直接解析到 `JobManager.Create`、`ScanIgnoreRuleSet.FromStore`、`ScanJobRunner.Run`、`ScanCandidatePersistence.Persist`、`ReconcileService.CheckGames` 和 `EventStream.Publish`。

```mermaid
flowchart LR
    Click[Desktop OnScanClick]
    Request[HostConnection.InvokeAsync\nscan.start]
    Validate[ScanningHandler.ScanStart\n参数、路径、注册根校验]
    Job[JobManager.Create]
    Rules[ScanIgnoreRuleSet.FromStore]
    Run[ScanJobRunner.Run]
    Persist[ScanCandidatePersistence.Persist]
    Reconcile[ReconcileService.CheckGames]
    Events[EventStream.Publish]
    Review[candidates.list / candidates.get\n用户接受、延后或忽略]

    Click --> Request --> Validate
    Validate -->|valid| Job --> Rules --> Run --> Persist --> Reconcile --> Events
    Events --> Review
    Validate -->|invalid/outside root| Reject[RejectPathOutsideRoots / error]
```

`OnScanClick` 流为 18 个节点、深度 5、criticality `0.7833`；`ScanStart` 流为 22 个节点、深度 4、criticality `0.7695`。

## 4. 启动游戏

证据入口：MCP `GameLibraryTools.LaunchExecute`（`GameLibraryTools.cs:645-657`）和 Host `LaunchingHandler.LaunchExecute`（`Host/Hosting/LaunchingHandler.cs:499-562`）。图谱解析到参数校验、`LaunchRegistry.GetPlanProfileId`、`GetProfile`、`GetPlanGameId`、`ResolveTranslationRoute`、`LaunchRegistry.Execute` 和错误映射。

```mermaid
flowchart TD
    Tool[CLI/MCP launch.execute]
    Invoke[HostClient.InvokeAsync]
    Handler[LaunchingHandler.LaunchExecute]
    Params[IpcRequests 参数校验]
    Plan[LaunchRegistry 读取 plan/profile/game]
    Route[ResolveTranslationRoute\nTranslationRouteBlock]
    Execute[LaunchRegistry.Execute]
    Result[launch result / LaunchError]

    Tool --> Invoke --> Handler --> Params
    Params -->|valid| Plan --> Route --> Execute --> Result
    Params -->|invalid| Result
```

`LaunchExecute` 流为 23 个节点、深度 5、criticality `0.7870`。启动相关并发、过期 plan、工具缺失和翻译策略行为已有图谱识别出的集成测试入口，可继续用 `query_graph_tool(pattern="tests_for", target="LaunchExecute")` 核对。
