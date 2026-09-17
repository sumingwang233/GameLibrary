using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;
using Xunit;

namespace GameLibrary.IntegrationTests.Ui;

/// <summary>
/// UIA 冒烟测试（阶段三：UI 自动化首步）：启动真实 Desktop 进程（独立数据目录），
/// 通过 UI Automation 断言主窗口、搜索框与关键按钮可达（屏幕阅读器/自动化的可发现性）。
/// 覆盖真实窗口操作和关键业务路径；进程以整树终止清理。
/// </summary>
public sealed class UiaSmokeTests
{
    private static string DesktopExe => Path.Combine(
        AppContext.BaseDirectory, "GameLibrary.Desktop.exe");

    [Fact]
    public async Task DesktopWindow_ManualFile_AddPlayRemove_PreservesOriginalFiles()
    {
        Assert.True(File.Exists(DesktopExe), $"未找到 Desktop 可执行文件：{DesktopExe}");

        var runDir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(runDir, "data");
        var gameDir = Path.Combine(dataDir, "fixture-game");
        Directory.CreateDirectory(gameDir);
        foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "GameLibrary.TestProcessStub" + extension),
                Path.Combine(gameDir, "GameLibrary.TestProcessStub" + extension));
        }

        var gameExe = Path.Combine(gameDir, "GameLibrary.TestProcessStub.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = DesktopExe,
                Arguments = $"--data-dir \"{dataDir}\"",
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Desktop 进程启动失败");

            try
            {
                var window = await WaitForWindowAsync(process, TimeSpan.FromSeconds(40));
                Assert.NotNull(window);
                var guide = await WaitForNamedWindowAsync(process.Id, "GameLibrary 使用指南", TimeSpan.FromSeconds(15));
                Assert.NotNull(guide);
                var guideDone = await WaitForElementAsync(guide!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "我知道了"), TimeSpan.FromSeconds(5));
                Assert.NotNull(guideDone);
                ((InvokePattern)guideDone!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

                var searchBox = await WaitForElementAsync(window!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchBox"),
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(searchBox);
                ((ValuePattern)searchBox!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("不匹配的旧搜索词");
                await Task.Delay(700);

                var addButton = await WaitForElementAsync(window!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ManualAddButton"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(addButton);
                ((InvokePattern)addButton!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var choice = await WaitForNamedWindowAsync(process.Id, "手动添加游戏", TimeSpan.FromSeconds(5));
                Assert.NotNull(choice);
                var fileChoice = await WaitForElementAsync(choice!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "选择游戏主程序或快捷方式（EXE / LNK）"),
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(fileChoice);
                ((InvokePattern)fileChoice!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

                var fileDialog = await WaitForNamedWindowAsync(process.Id, "选择游戏主程序", TimeSpan.FromSeconds(10));
                Assert.True(fileDialog is not null, $"未找到文件选择器；窗口：{VisibleWindowNames(process.Id)}");
                var fileNameInput = fileDialog!.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                Assert.True(fileNameInput is not null, $"未找到文件名输入框；控件：{VisibleControlNames(fileDialog)}");
                ((ValuePattern)fileNameInput!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(gameExe);
                Assert.Equal(gameExe, ((ValuePattern)fileNameInput.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                var openButton = fileDialog.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "1"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                Assert.True(openButton is not null, $"未找到打开按钮；控件：{VisibleControlNames(fileDialog)}");
                ((InvokePattern)openButton!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                // Windows 文件对话框可能先导航到绝对路径所在目录，再把文件名留在输入框。
                var navigatedDialog = await WaitForNamedWindowAsync(
                    process.Id, "选择游戏主程序", TimeSpan.FromSeconds(2));
                if (navigatedDialog is not null)
                {
                    var remainingName = ((ValuePattern)fileNameInput.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                    Assert.Equal(Path.GetFileName(gameExe), remainingName);
                    var completeOpen = navigatedDialog.FindFirst(TreeScope.Descendants,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "1"),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                    Assert.NotNull(completeOpen);
                    ((InvokePattern)completeOpen!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }

                var confirmScope = await WaitForNamedWindowAsync(
                    process.Id, "确认游戏文件夹范围", TimeSpan.FromSeconds(5));
                if (confirmScope is not null)
                {
                    var yesButton = confirmScope.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "6"));
                    Assert.True(yesButton is not null, $"未找到确认按钮；控件：{VisibleControlNames(confirmScope)}");
                    ((InvokePattern)yesButton!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }

                await using var client = await HostConnection.ConnectAsync(dataDir, "uia-manual", CancellationToken.None);
                string? gameId = null;
                Assert.True(await WaitUntilTrueAsync(async () =>
                {
                    var list = await InvokeHostAsync(client, "games.list", new { });
                    if (!list.Ok || list.Data.GetProperty("total").GetInt32() != 1)
                    {
                        return false;
                    }

                    gameId = list.Data.GetProperty("items")[0].GetProperty("gameId").GetString();
                    return gameId is not null;
                }, TimeSpan.FromSeconds(15)),
                    $"从窗口选择 EXE 后，游戏未进入宿主库；确认框={confirmScope is not null}；" +
                    $"文件存在：{File.Exists(gameExe)}；窗口：{VisibleWindowNames(process.Id)}；控件：{VisibleControlNames(window)}");
                var profiles = await InvokeHostAsync(client, "profiles.list", new { gameId });
                Assert.True(profiles.Ok, profiles.Error?.Message);
                Assert.Single(profiles.Data.GetProperty("items").EnumerateArray());
                Assert.Equal("", ((ValuePattern)searchBox.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);

                var removeButton = await WaitForElementAsync(window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "从库中移除（不删除文件）"),
                    TimeSpan.FromSeconds(15));
                Assert.NotNull(removeButton);
                var playButton = await WaitForElementAsync(window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "▶ 开始游戏"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(playButton);
                Assert.True(await WaitUntilTrueAsync(
                    () => Task.FromResult(playButton!.Current.IsEnabled), TimeSpan.FromSeconds(10)),
                    "手动选择 EXE 后，开始游戏按钮仍不可用");
                ((InvokePattern)playButton!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Assert.True(await WaitUntilTrueAsync(async () =>
                {
                    var history = await InvokeHostAsync(client, "launch.history", new { gameId });
                    return history.Ok && history.Data.GetProperty("total").GetInt32() == 1;
                }, TimeSpan.FromSeconds(15)), "点击开始游戏后，宿主没有启动记录");

                ((InvokePattern)removeButton!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var removeConfirm = await WaitForNamedWindowAsync(
                    process.Id, "确认从库中移除", TimeSpan.FromSeconds(5));
                Assert.NotNull(removeConfirm);
                var confirmRemove = removeConfirm!.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "6"));
                Assert.True(confirmRemove is not null, $"未找到移除确认按钮；控件：{VisibleControlNames(removeConfirm)}");
                ((InvokePattern)confirmRemove!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Assert.True(await WaitUntilTrueAsync(async () =>
                {
                    var list = await InvokeHostAsync(client, "games.list", new { });
                    return list.Ok && list.Data.GetProperty("total").GetInt32() == 0;
                }, TimeSpan.FromSeconds(15)), "从库中移除后，宿主仍列出此游戏");
                Assert.True(File.Exists(gameExe), "从库中移除意外删除了原游戏文件");
            }
            finally
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        finally
        {
            TryCleanup(runDir);
        }
    }

    [Fact]
    public async Task DesktopWindow_PendingCandidates_CanBeSelectedAndBatchAccepted()
    {
        Assert.True(File.Exists(DesktopExe), $"未找到 Desktop 可执行文件：{DesktopExe}");

        var runDir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"uia-batch-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(runDir, "data");
        var scanRoot = Path.Combine(runDir, "games");
        Directory.CreateDirectory(dataDir);
        foreach (var name in new[] { "GameA", "GameB" })
        {
            var game = Path.Combine(scanRoot, name);
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, $"{name}.exe"), "fixture");
            File.WriteAllText(Path.Combine(game, "content.pak"), "fixture");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = DesktopExe,
                Arguments = $"--data-dir \"{dataDir}\"",
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Desktop 进程启动失败");

            try
            {
                var window = await WaitForWindowAsync(process, TimeSpan.FromSeconds(40));
                Assert.NotNull(window);
                var guide = await WaitForNamedWindowAsync(process.Id, "GameLibrary 使用指南", TimeSpan.FromSeconds(15));
                Assert.NotNull(guide);
                var guideDone = await WaitForElementAsync(guide!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "我知道了"), TimeSpan.FromSeconds(5));
                Assert.NotNull(guideDone);
                ((InvokePattern)guideDone!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

                var statusText = await WaitForElementAsync(window!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "StatusText"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(statusText);
                Assert.True(await WaitUntilTrueAsync(
                    () => Task.FromResult((statusText!.Current.Name ?? "").StartsWith("已就绪", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(40)));

                await using var client = await HostConnection.ConnectAsync(dataDir, "uia-batch", CancellationToken.None);
                var addRoot = await InvokeHostAsync(client, "roots.add", new
                {
                    idempotencyKey = $"uia-batch-root-{Guid.NewGuid():N}",
                    root = scanRoot,
                });
                Assert.True(addRoot.Ok, addRoot.Error?.Message);
                var scan = await InvokeHostAsync(client, "scan.start", new
                {
                    idempotencyKey = $"uia-batch-scan-{Guid.NewGuid():N}",
                    root = scanRoot,
                });
                Assert.True(scan.Ok, scan.Error?.Message);
                var jobId = scan.JobId!;
                Assert.True(await WaitUntilTrueAsync(async () =>
                {
                    var job = await InvokeHostAsync(client, "jobs.get", new { jobId });
                    return job.Ok && job.Data.GetProperty("state").GetString() == "succeeded";
                }, TimeSpan.FromSeconds(20)), "扫描作业未完成");
                Assert.True(await WaitUntilTrueAsync(async () =>
                {
                    var candidates = await InvokeHostAsync(client, "candidates.list", new { });
                    return candidates.Ok && candidates.Data.GetProperty("items").EnumerateArray()
                        .Count(item => item.GetProperty("reviewState").GetString() == "pendingReview") == 2;
                }, TimeSpan.FromSeconds(10)), "未生成两个待确认候选");

                var viewSelector = await WaitForElementAsync(window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ViewSelector"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(viewSelector);
                ((ExpandCollapsePattern)viewSelector!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                var pendingView = await WaitForElementAsync(viewSelector, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "待确认游戏"),
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(pendingView);
                ((SelectionItemPattern)pendingView!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

                var selectAll = await WaitForElementAsync(window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SelectAllCandidatesCheckBox"),
                    TimeSpan.FromSeconds(15));
                Assert.NotNull(selectAll);
                ((TogglePattern)selectAll!.GetCurrentPattern(TogglePattern.Pattern)).Toggle();

                var batchAccept = await WaitForElementAsync(window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "BatchAcceptButton"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(batchAccept);
                Assert.True(await WaitUntilTrueAsync(
                    () => Task.FromResult(batchAccept!.Current.IsEnabled), TimeSpan.FromSeconds(5)));
                ((InvokePattern)batchAccept!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

                Assert.True(await WaitUntilTrueAsync(async () =>
                {
                    var games = await InvokeHostAsync(client, "games.list", new { });
                    var candidates = await InvokeHostAsync(client, "candidates.list", new { });
                    return games.Ok
                        && games.Data.GetProperty("total").GetInt32() == 2
                        && candidates.Ok
                        && candidates.Data.GetProperty("items").EnumerateArray()
                            .All(item => item.GetProperty("reviewState").GetString() != "pendingReview");
                }, TimeSpan.FromSeconds(20)), "批量加入未处理全部选中候选");
                Assert.True(await WaitUntilTrueAsync(
                    () => Task.FromResult((statusText.Current.Name ?? "").Contains("成功 2 项", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(10)), $"未显示批量操作汇总：{statusText.Current.Name}");
            }
            finally
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        finally
        {
            TryCleanup(runDir);
        }
    }

    [Fact]
    public async Task DesktopWindow_ExposesSearchAndPrimaryActions_ToUiAutomation()
    {
        Assert.True(File.Exists(DesktopExe), $"未找到 Desktop 可执行文件：{DesktopExe}");

        var runDir = Path.Combine(@"D:\Official\GameLibrary\artifacts\test-runs", $"uia-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(runDir, "data");
        Directory.CreateDirectory(dataDir);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = DesktopExe,
                Arguments = $"--data-dir \"{dataDir}\"",
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Desktop 进程启动失败");

            try
            {
                // 1. 等主窗口出现（首次启动含库初始化 + 宿主连接）。
                var window = await WaitForWindowAsync(process, TimeSpan.FromSeconds(40));
                Assert.NotNull(window);
                Assert.Contains("GameLibrary", window!.Current.Name, StringComparison.OrdinalIgnoreCase);

                // 2. 关键控件对 UIA 可发现（AutomationId 来自 x:Name）。
                var search = await WaitForElementAsync(
                    window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchBox"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(search);

                // 3. 视图切换器与内容区可达（自动化树完整性抽查）。
                var viewSelector = await WaitForElementAsync(
                    window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ViewSelector"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(viewSelector);

                var createCollection = await WaitForElementAsync(
                    window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "新建收藏夹"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(createCollection);

                var manageCollections = await WaitForElementAsync(
                    window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "管理收藏夹"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(manageCollections);

                var manualAdd = await WaitForElementAsync(
                    window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ManualAddButton"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(manualAdd);

                // 4. 启动引导端到端：状态栏最终进入"已就绪"（后台服务自动拉起 + 游戏库准备完成）。
                var statusText = await WaitForElementAsync(
                    window, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "StatusText"),
                    TimeSpan.FromSeconds(10));
                Assert.NotNull(statusText);

                var connected = await WaitUntilTrueAsync(async () =>
                {
                    var text = statusText!.GetCurrentPropertyValue(AutomationElement.NameProperty) as string ?? "";
                    return text.StartsWith("已就绪", StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(40));
                Assert.True(connected, "状态栏 40 秒内未进入'已就绪'（后台服务启动失败？）");

                // 全新空库自动展示使用指南；先关闭模态窗口，再操作主窗口。
                var guide = await WaitForNamedWindowAsync(process.Id, "GameLibrary 使用指南", TimeSpan.FromSeconds(5));
                Assert.NotNull(guide);
                var done = await WaitForElementAsync(guide!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "我知道了"),
                    TimeSpan.FromSeconds(5));
                Assert.True(done is not null,
                    $"指南内未找到确认按钮；类型={guide.Current.ControlType.ProgrammaticName}；" +
                    $"进程已退出={process.HasExited}；顶层窗口={VisibleWindowNames(process.Id)}；子元素=" +
                    string.Join(" | ", guide.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                        .Cast<AutomationElement>().Select(element => element.Current.Name).Where(name => name.Length > 0)));
                ((InvokePattern)done!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Assert.True(await WaitUntilTrueAsync(
                    () => Task.FromResult(File.Exists(Path.Combine(dataDir, "desktop-guide-v1.json"))),
                    TimeSpan.FromSeconds(5)));
                var helpButton = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "HelpButton"));
                Assert.NotNull(helpButton);
                ((InvokePattern)helpButton!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var reopenedGuide = await WaitForNamedWindowAsync(
                    process.Id, "GameLibrary 使用指南", TimeSpan.FromSeconds(5));
                Assert.NotNull(reopenedGuide);
                var closeGuide = await WaitForElementAsync(reopenedGuide!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "我知道了"),
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(closeGuide);
                ((InvokePattern)closeGuide!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

                // 再次双击同一数据目录：新进程只唤醒现有窗口，不创建第二个后台实例。
                using var second = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("第二个 Desktop 进程启动失败");
                using var secondTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await second.WaitForExitAsync(secondTimeout.Token);
                Assert.Equal(0, second.ExitCode);
                Assert.False(process.HasExited);
                Assert.NotNull(await WaitForWindowAsync(process, TimeSpan.FromSeconds(5)));

                // 即使用另一个数据目录启动，也只能唤醒现有窗口。最小化状态用于验证唤醒。
                var windowPattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
                windowPattern.SetWindowVisualState(WindowVisualState.Minimized);
                var alternateStartInfo = new ProcessStartInfo
                {
                    FileName = DesktopExe,
                    Arguments = $"--data-dir \"{Path.Combine(runDir, "other-data")}\"",
                    UseShellExecute = false,
                };
                using var alternate = Process.Start(alternateStartInfo)
                    ?? throw new InvalidOperationException("另一数据目录的 Desktop 进程启动失败");
                using var alternateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await alternate.WaitForExitAsync(alternateTimeout.Token);
                Assert.Equal(0, alternate.ExitCode);
                Assert.False(process.HasExited);
                Assert.True(await WaitUntilTrueAsync(
                    () => Task.FromResult(windowPattern.Current.WindowVisualState == WindowVisualState.Normal),
                    TimeSpan.FromSeconds(5)), "现有窗口未被第二次启动唤醒");

                // UI 真实创建收藏夹，随后经独立 HostClient 读取：验证不是只有按钮外观。
                ((InvokePattern)createCollection!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var nameDialog = await WaitForNamedWindowAsync(
                    process.Id, "新建收藏夹", TimeSpan.FromSeconds(5));
                Assert.True(nameDialog is not null,
                    $"未找到新建收藏夹对话框；当前窗口：{VisibleWindowNames(process.Id)}");
                var nameInput = nameDialog!.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                Assert.NotNull(nameInput);
                ((ValuePattern)nameInput!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("测试收藏夹");
                var confirm = nameDialog.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "确定"));
                Assert.NotNull(confirm);
                ((InvokePattern)confirm!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

                await using var client = await HostConnection.ConnectAsync(dataDir, "uia-smoke", CancellationToken.None);
                var created = await WaitUntilTrueAsync(async () =>
                {
                    using var parameters = JsonDocument.Parse("{}");
                    var result = await client.InvokeAsync(new IpcRequest
                    {
                        RequestId = $"uia-tags-{Guid.NewGuid():N}",
                        OperationId = "tags.list",
                        Parameters = parameters.RootElement.Clone(),
                    }, CancellationToken.None);
                    return result.Ok && result.Data.GetProperty("items").EnumerateArray()
                        .Any(tag => tag.GetProperty("kind").GetString() == "user"
                            && tag.GetProperty("name").GetString() == "测试收藏夹");
                }, TimeSpan.FromSeconds(10));
                Assert.True(created, "Desktop 收藏夹创建未持久化到宿主库");

                // 先从独立入口设置自定义缓存位置，验证 Desktop 能读回、显示并恢复默认。
                var cacheParent = Path.Combine(runDir, "ui-cache-parent");
                Directory.CreateDirectory(cacheParent);
                var settingsBefore = await InvokeHostAsync(client, "settings.get", new { });
                var cacheUpdate = await InvokeHostAsync(client, "settings.update", new
                {
                    idempotencyKey = $"uia-cache-{Guid.NewGuid():N}",
                    expectedRevision = settingsBefore.Data.GetProperty("revision").GetInt32(),
                    cacheParentDirectory = cacheParent,
                });
                Assert.True(cacheUpdate.Ok, cacheUpdate.Error?.Message);

                // 设置页字体、文字大小与缓存目录对辅助技术可见，修改能从宿主读回。
                var settingsButton = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsButton"));
                Assert.NotNull(settingsButton);
                ((InvokePattern)settingsButton!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var settingsDialog = await WaitForNamedWindowAsync(process.Id, "设置", TimeSpan.FromSeconds(5));
                Assert.NotNull(settingsDialog);
                var fontChoice = await WaitForElementAsync(settingsDialog!, TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                        new PropertyCondition(AutomationElement.NameProperty, "界面字体")),
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(fontChoice);
                var scaleControl = await WaitForElementAsync(settingsDialog!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "文字与界面大小"),
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(scaleControl);
                var cachePath = await WaitForElementAsync(settingsDialog!, TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CacheDirectoryPath"),
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(cachePath);
                Assert.Equal(cacheParent,
                    ((ValuePattern)cachePath!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                var chooseCache = settingsDialog.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ChooseCacheDirectoryButton"));
                Assert.NotNull(chooseCache);
                var resetCache = settingsDialog.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ResetCacheDirectoryButton"));
                Assert.NotNull(resetCache);
                ((InvokePattern)resetCache!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                ((RangeValuePattern)scaleControl!.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(1.25);
                var saveSettings = settingsDialog.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "保存"));
                Assert.NotNull(saveSettings);
                ((InvokePattern)saveSettings!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var savedScale = await WaitUntilTrueAsync(async () =>
                {
                    using var parameters = JsonDocument.Parse("{}");
                    var result = await client.InvokeAsync(new IpcRequest
                    {
                        RequestId = $"uia-settings-{Guid.NewGuid():N}",
                        OperationId = "settings.get",
                        Parameters = parameters.RootElement.Clone(),
                    }, CancellationToken.None);
                    return result.Ok
                        && Math.Abs(result.Data.GetProperty("uiFontScale").GetDouble() - 1.25) < 0.01
                        && result.Data.GetProperty("cacheParentDirectory").ValueKind == JsonValueKind.Null;
                }, TimeSpan.FromSeconds(10));
                Assert.True(savedScale, "Desktop 文字大小或默认缓存位置未持久化");
            }
            finally
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        finally
        {
            TryCleanup(runDir);
        }
    }

    private static async Task<AutomationElement?> WaitForWindowAsync(
        Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var condition = new PropertyCondition(
                AutomationElement.ProcessIdProperty, process.Id);
            var window = AutomationElement.RootElement.FindFirst(
                TreeScope.Children, condition);
            if (window is not null)
            {
                return window;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static async Task<AutomationElement?> WaitForNamedWindowAsync(
        int processId, string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        while (DateTime.UtcNow < deadline)
        {
            var window = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition);
            if (window is not null)
            {
                return window;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static string VisibleWindowNames(int processId) => string.Join(", ",
        AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId))
            .Cast<AutomationElement>()
            .Select(window => window.Current.Name));

    private static string VisibleControlNames(AutomationElement root) => string.Join(" | ",
        root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Select(element => $"{element.Current.Name} ({element.Current.AutomationId})")
            .Where(name => name.Length > 3));

    private static async Task<Envelope<JsonElement>> InvokeHostAsync(
        HostConnection client, string operationId, object parameters)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        return await client.InvokeAsync(new IpcRequest
        {
            RequestId = $"uia-{Guid.NewGuid():N}",
            OperationId = operationId,
            Parameters = json.RootElement.Clone(),
        }, CancellationToken.None);
    }

    private static async Task<bool> WaitUntilTrueAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return await probe();
    }

    private static async Task<AutomationElement?> WaitForElementAsync(
        AutomationElement root, TreeScope scope, Condition condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var element = root.FindFirst(scope, condition);
            if (element is not null)
            {
                return element;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
