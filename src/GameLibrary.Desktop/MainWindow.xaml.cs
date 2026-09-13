using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;

namespace GameLibrary.Desktop;

/// <summary>
/// Desktop 纵切（T12）：游戏卡片 + 候选审核，仅经 HostClient 与宿主通信，
/// 与 CLI/MCP 同库同契约；数据落库后重启保留。
/// </summary>
public partial class MainWindow : Window
{
    private HostConnection? _connection;
    private string? _dataDirectory;

    public MainWindow()
    {
        InitializeComponent();
        ParseArgs();
        Loaded += async (_, _) => await ConnectAsync();
    }

    private void ParseArgs()
    {
        var args = App.Args;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                _dataDirectory = args[i + 1];
            }
        }

        if (_dataDirectory is null)
        {
            ShowError("缺少 --data-dir 启动参数；示例：GameLibrary.Desktop.exe --data-dir D:\\gamelibrary-data");
        }
        else
        {
            RootBox.Text = _dataDirectory;
        }
    }

    private async Task ConnectAsync()
    {
        if (_dataDirectory is null)
        {
            return;
        }

        try
        {
            SetStatus("正在连接宿主…");
            await DisposeConnectionAsync();
            _connection = await HostProcessLauncher.EnsureStartedAsync(
                _dataDirectory, clientName: "desktop", timeout: TimeSpan.FromSeconds(10));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("未连接");
            ShowError($"连接宿主失败：{ex.Message}");
        }
    }

    private async Task RefreshAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            var hostStatus = await InvokeAsync("host.status");
            var libraryInitialized = hostStatus.Data.GetProperty("libraryInitialized").GetBoolean();
            InitLibraryButton.IsEnabled = !libraryInitialized;
            var libraryState = hostStatus.Data.TryGetProperty("libraryState", out var state)
                ? state.GetString()
                : null;

            var games = await InvokeAsync("games.list");
            RenderGames(games);

            var candidates = await InvokeAsync("candidates.list");
            RenderCandidates(candidates);

            var gameCount = games.Data.GetProperty("total").GetInt32();
            var candidateCount = candidates.Data.GetProperty("total").GetInt32();
            SetStatus($"已连接 · 库 {libraryState} · 游戏 {gameCount} · 候选 {candidateCount}");
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError($"刷新失败：{ex.Message}");
        }
    }

    private void RenderGames(Envelope<JsonElement> envelope)
    {
        GamesPanel.Children.Clear();
        foreach (var game in envelope.Data.GetProperty("items").EnumerateArray())
        {
            GamesPanel.Children.Add(BuildCard(
                title: game.GetProperty("title").GetString() ?? "(未命名)",
                lines:
                [
                    $"引擎：{game.GetProperty("engine").GetString() ?? "未识别"}",
                    $"路径：{game.GetProperty("rootPath").GetString()}",
                    $"状态：{game.GetProperty("membership").GetString()} · rev {game.GetProperty("revision").GetInt32()}",
                ],
                actions: []));
        }

        if (GamesPanel.Children.Count == 0)
        {
            GamesPanel.Children.Add(BuildCard("还没有游戏", ["扫描一个目录，然后在「候选审核」里接受候选入库。"], []));
        }
    }

    private void RenderCandidates(Envelope<JsonElement> envelope)
    {
        CandidatesPanel.Children.Clear();
        foreach (var candidate in envelope.Data.GetProperty("items").EnumerateArray())
        {
            var candidateId = candidate.GetProperty("candidateId").GetString()!;
            var relativePath = candidate.GetProperty("relativePath").GetString();
            var reviewState = candidate.GetProperty("reviewState").GetString();
            var revision = candidate.GetProperty("revision").GetInt32();

            var actions = new List<(string Label, Func<Task> Handler)>();
            if (reviewState == "pendingReview")
            {
                actions.Add(("接受入库", () => ReviewAsync(candidateId, revision, "candidates.accept")));
                actions.Add(("暂缓", () => ReviewAsync(candidateId, revision, "candidates.defer")));
                actions.Add(("忽略", () => ReviewAsync(candidateId, revision, "candidates.ignore")));
            }

            CandidatesPanel.Children.Add(BuildCard(
                title: relativePath ?? candidateId,
                lines:
                [
                    $"状态：{reviewState} · rev {revision}",
                    candidate.GetProperty("physicalPath").GetString() ?? "",
                ],
                actions));
        }

        if (CandidatesPanel.Children.Count == 0)
        {
            CandidatesPanel.Children.Add(BuildCard("没有待审核候选", ["扫描发现的候选会出现在这里。"], []));
        }
    }

    private static UIElement BuildCard(
        string title, IReadOnlyList<string> lines, IReadOnlyList<(string Label, Func<Task> Handler)> actions)
    {
        var border = new Border
        {
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(4),
            Width = 300,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontSize = 12,
            });
        }

        if (actions.Count > 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (label, handler) in actions)
            {
                var button = new Button { Content = label, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
                var captured = handler;
                button.Click += async (_, _) => await captured();
                panel.Children.Add(button);
            }

            stack.Children.Add(panel);
        }

        border.Child = stack;
        return border;
    }

    private async Task ReviewAsync(string candidateId, int revision, string operationId)
    {
        try
        {
            var envelope = await InvokeAsync(operationId, new
            {
                idempotencyKey = $"ui-{Guid.NewGuid():N}",
                candidateId,
                expectedRevision = revision,
            });
            if (!envelope.Ok)
            {
                ShowError($"{operationId} 失败：{envelope.Error?.Code} {envelope.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            ShowError($"{operationId} 失败：{ex.Message}");
        }

        await RefreshAsync();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void OnInitLibraryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var envelope = await InvokeAsync("library.init");
            if (!envelope.Ok)
            {
                ShowError($"建库失败：{envelope.Error?.Message}");
            }
            else
            {
                SetStatus("库已初始化");
            }
        }
        catch (Exception ex)
        {
            ShowError($"建库失败：{ex.Message}");
        }

        await RefreshAsync();
    }

    private async void OnAddRootClick(object sender, RoutedEventArgs e)
    {
        var root = RootBox.Text.Trim();
        if (root.Length == 0)
        {
            ShowError("请填写库根目录（绝对路径）。");
            return;
        }

        try
        {
            var envelope = await InvokeAsync("roots.add", new { root });
            if (!envelope.Ok)
            {
                ShowError($"注册库根失败：{envelope.Error?.Message}");
            }
            else
            {
                SetStatus("库根已注册（显式授权扫描边界）");
            }
        }
        catch (Exception ex)
        {
            ShowError($"注册库根失败：{ex.Message}");
        }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        var root = RootBox.Text.Trim();
        if (root.Length == 0)
        {
            ShowError("请填写要扫描的根目录（绝对路径）。");
            return;
        }

        ScanButton.IsEnabled = false;
        try
        {
            var start = await InvokeAsync("scan.start", new { root });
            if (!start.Ok)
            {
                ShowError($"扫描启动失败：{start.Error?.Code} {start.Error?.Message}");
                return;
            }

            var jobId = start.JobId!;
            SetStatus("扫描中…");
            while (true)
            {
                await Task.Delay(500);
                var status = await InvokeAsync("jobs.get", new { jobId });
                var state = status.Data.GetProperty("state").GetString();
                if (state is "succeeded" or "failed" or "cancelled")
                {
                    if (state != "succeeded")
                    {
                        ShowError($"扫描结束：{state}");
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"扫描失败：{ex.Message}");
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }

        await RefreshAsync();
    }

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("宿主未连接");
        }

        var json = JsonSerializer.Serialize(parameters ?? new { });
        return await _connection.InvokeAsync(
            new IpcRequest
            {
                RequestId = $"desktop-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = JsonDocument.Parse(json).RootElement.Clone(),
            },
            CancellationToken.None);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowError(string? message) => ErrorText.Text = message ?? "";

    private async Task DisposeConnectionAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        await DisposeConnectionAsync();
        base.OnClosed(e);
    }
}
