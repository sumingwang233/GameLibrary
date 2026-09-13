using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;

namespace GameLibrary.Desktop;

/// <summary>
/// Desktop 纵切（T12/T15-A）：Steam 库风格视图——左侧游戏列表、右侧详情与操作。
/// 仅经 HostClient 与宿主通信，与 CLI/MCP 同库同契约；数据落库重启保留。
/// </summary>
public partial class MainWindow : Window
{
    private HostConnection? _connection;
    private string? _dataDirectory;

    /// <summary>侧边栏条目（游戏或候选），供选中联动。</summary>
    private sealed record Entry(
        string Kind, // "game" | "candidate"
        string Id,
        string Title,
        string Subtitle,
        string PhysicalPath,
        JsonElement Raw);

    private List<Entry> _entries = [];

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
            var candidates = await InvokeAsync("candidates.list");
            RenderSidebar(games, candidates);

            var gameCount = games.Data.GetProperty("total").GetInt32();
            var pendingCount = candidates.Data.GetProperty("items").EnumerateArray()
                .Count(c => c.GetProperty("reviewState").GetString() == "pendingReview");
            SetStatus($"已连接 · 库 {libraryState} · 游戏 {gameCount} · 待审核 {pendingCount}");
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError($"刷新失败：{ex.Message}");
        }
    }

    private void RenderSidebar(Envelope<JsonElement> games, Envelope<JsonElement> candidates)
    {
        var entries = new List<Entry>();

        foreach (var game in games.Data.GetProperty("items").EnumerateArray())
        {
            var title = game.GetProperty("title").GetString() ?? "(未命名)";
            var engine = game.GetProperty("engine").GetString() ?? "未识别";
            entries.Add(new Entry(
                "game",
                game.GetProperty("gameId").GetString() ?? "",
                title,
                engine,
                game.GetProperty("rootPath").GetString() ?? "",
                game));
        }

        var pending = candidates.Data.GetProperty("items").EnumerateArray()
            .Where(c => c.GetProperty("reviewState").GetString() == "pendingReview")
            .ToList();
        foreach (var candidate in pending)
        {
            var relativePath = candidate.GetProperty("relativePath").GetString();
            entries.Add(new Entry(
                "candidate",
                candidate.GetProperty("candidateId").GetString() ?? "",
                string.IsNullOrEmpty(relativePath) || relativePath == "" ? "(库根)" : relativePath!,
                "待审核",
                candidate.GetProperty("physicalPath").GetString() ?? "",
                candidate));
        }

        _entries = entries;

        LibraryList.Items.Clear();
        foreach (var entry in entries)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            panel.Children.Add(new TextBlock
            {
                Text = entry.Kind == "candidate" ? "待审核候选" : entry.Subtitle,
                FontSize = 11,
                Foreground = entry.Kind == "candidate"
                    ? TryFindResource<SolidColorBrush>("Green")
                    : TryFindResource<SolidColorBrush>("TextMuted"),
            });
            LibraryList.Items.Add(panel);
        }

        if (LibraryList.Items.Count == 0)
        {
            LibraryList.Items.Add(new TextBlock
            {
                Text = "库是空的。填写目录 → 注册库根 → 扫描，候选会出现在这里。",
                FontSize = 11,
                Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 6, 10, 6),
            });
            RenderDetailPlaceholder();
            return;
        }

        // 保持既有选中或选第一个。
        var previous = LibraryList.SelectedIndex;
        LibraryList.SelectedIndex = previous >= 0 && previous < _entries.Count ? previous : 0;
        if (LibraryList.SelectedIndex < 0)
        {
            RenderDetailPlaceholder();
        }
    }

    private void RenderDetailPlaceholder()
    {
        DetailPanel.Children.Clear();
        DetailPanel.Children.Add(new TextBlock
        {
            Text = "选择左侧条目查看详情",
            FontSize = 14,
            Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
        });
    }

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = LibraryList.SelectedIndex;
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        RenderDetail(_entries[index]);
    }

    private void RenderDetail(Entry entry)
    {
        DetailPanel.Children.Clear();

        if (entry.Kind == "game")
        {
            var raw = entry.Raw;
            DetailPanel.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = TryFindResource<SolidColorBrush>("TextPrimary"),
            });
            DetailPanel.Children.Add(new TextBlock
            {
                Text = $"引擎 {raw.GetProperty("engine").GetString() ?? "未识别"} · {raw.GetProperty("kind").GetString()} · rev {raw.GetProperty("revision").GetInt32()}",
                FontSize = 13,
                Foreground = TryFindResource<SolidColorBrush>("Accent"),
                Margin = new Thickness(0, 4, 0, 12),
            });
            DetailPanel.Children.Add(MetaLine("路径", raw.GetProperty("rootPath").GetString() ?? ""));
            DetailPanel.Children.Add(MetaLine("入库时间", raw.GetProperty("acceptedUtc").GetString() ?? ""));

            DetailPanel.Children.Add(ButtonRow(
                ("打开目录", () => OpenDirectoryAsync(entry.PhysicalPath), "SteamButton")));
            return;
        }

        // 候选详情
        var candidate = entry.Raw;
        DetailPanel.Children.Add(new TextBlock
        {
            Text = entry.Title.Length == 0 ? "(库根)" : entry.Title,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = TryFindResource<SolidColorBrush>("TextPrimary"),
        });
        DetailPanel.Children.Add(new TextBlock
        {
            Text = $"待审核 · {candidate.GetProperty("kind").GetString()} · rev {candidate.GetProperty("revision").GetInt32()}",
            FontSize = 13,
            Foreground = TryFindResource<SolidColorBrush>("Green"),
            Margin = new Thickness(0, 4, 0, 12),
        });
        DetailPanel.Children.Add(MetaLine("路径", entry.PhysicalPath));
        DetailPanel.Children.Add(MetaLine("候选 ID", entry.Id));
        DetailPanel.Children.Add(new TextBlock
        {
            Text = "接受后创建游戏卡片；忽略将登记 ExactPath 规则（撤销规则才恢复提示）。",
            FontSize = 12,
            Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
            Margin = new Thickness(0, 10, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        });

        var revision = candidate.GetProperty("revision").GetInt32();
        DetailPanel.Children.Add(ButtonRow(
            ("接受入库", () => ReviewAsync(entry.Id, revision, "candidates.accept"), "SteamGreenButton"),
            ("暂缓", () => ReviewAsync(entry.Id, revision, "candidates.defer"), "SteamButton"),
            ("忽略", () => ReviewAsync(entry.Id, revision, "candidates.ignore"), "SteamButton")));
    }

    private static TextBlock MetaLine(string label, string value) => new()
    {
        Text = $"{label}：{value}",
        FontSize = 12,
        Margin = new Thickness(0, 3, 0, 3),
        TextTrimming = TextTrimming.CharacterEllipsis,
        ToolTip = value,
    };

    private StackPanel ButtonRow(params (string Label, Func<Task> Handler, string Style)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (label, handler, style) in buttons)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)TryFindResource(style),
                Margin = new Thickness(0, 0, 10, 0),
                MinWidth = 96,
            };
            var captured = handler;
            button.Click += async (_, _) => await captured();
            row.Children.Add(button);
        }

        return row;
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

    private async Task OpenDirectoryAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                ShowError($"目录不存在：{path}");
            }
        }
        catch (Exception ex)
        {
            ShowError($"打开目录失败：{ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

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

    private static T? TryFindResource<T>(string key) where T : class =>
        System.Windows.Application.Current.TryFindResource(key) as T;

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
