using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.HostClient;

namespace GameLibrary.Desktop;

/// <summary>
/// Desktop 纵切（T12/T15）：Steam 库风格视图——左侧视图切换/搜索/游戏列表、右侧详情与操作。
/// 仅经 HostClient 与宿主通信，与 CLI/MCP 同库同契约；数据落库重启保留。
/// T15：搜索 300ms 防抖、收藏（games.update）、封面 LRU + 取消加载、列表虚拟化、PerMonitorV2 DPI、Ctrl+F。
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
        bool Favorite,
        JsonElement Raw);

    private List<Entry> _allEntries = [];
    private string _searchText = "";

    /// <summary>封面位图 LRU 缓存（T15：图像取消/LRU；容量 32）。</summary>
    private static readonly Dictionary<string, BitmapImage> CoverCache = new();
    private static readonly LinkedList<string> CoverLruOrder = new();
    private const int MaxCachedCovers = 32;
    private CancellationTokenSource? _coverLoadCts;

    private readonly DispatcherTimer _searchTimer;

    public MainWindow()
    {
        InitializeComponent();
        ParseArgs();
        ViewSelector.Items.Add(new ComboBoxItem { Content = "全部游戏", Tag = "all" });
        ViewSelector.Items.Add(new ComboBoxItem { Content = "收藏", Tag = "favorites" });
        ViewSelector.Items.Add(new ComboBoxItem { Content = "待审核候选", Tag = "pending" });
        ViewSelector.SelectedIndex = 0;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _searchText = SearchBox.Text.Trim();
            RenderSidebar();
        };
        SearchBox.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
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

            var favoriteFilter = ViewSelector.SelectedItem is ComboBoxItem item && (string)item.Tag == "favorites";
            var games = await InvokeAsync("games.list", favoriteFilter ? new { favorite = true } : null);
            var candidates = await InvokeAsync("candidates.list");
            RenderSidebar(games, candidates);

            var gameCount = games.Data.GetProperty("total").GetInt32();
            var candidateCount = candidates.Data.GetProperty("total").GetInt32();
            SetStatus($"已连接 · 库 {libraryState} · 游戏 {gameCount} · 待审核 {candidateCount}");
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
                game.GetProperty("favorite").GetBoolean(),
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
                false,
                candidate));
        }

        _allEntries = entries;
        RenderSidebar();
    }

    /// <summary>应用当前视图与搜索文本渲染侧边栏（T15：搜索防抖后调用）。</summary>
    private void RenderSidebar()
    {
        var activeView = ViewSelector.SelectedItem is ComboBoxItem item ? (string)item.Tag : "all";
        IEnumerable<Entry> visible = _allEntries;
        if (activeView == "favorites")
        {
            visible = visible.Where(e => e.Kind == "game" && e.Favorite);
        }

        if (_searchText.Length > 0)
        {
            visible = visible.Where(e => e.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }

        var list = visible.ToList();
        LibraryList.Items.Clear();
        foreach (var entry in list)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = (entry.Favorite ? "★ " : "") + entry.Title,
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
                Text = _searchText.Length > 0
                    ? $"没有匹配「{_searchText}」的条目。"
                    : "库是空的。填写目录 → 注册库根 → 扫描，候选会出现在这里。",
                FontSize = 11,
                Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 6, 10, 6),
            });
            DetailPanel.Children.Clear();
            return;
        }

        var previous = LibraryList.SelectedIndex;
        LibraryList.SelectedIndex = previous >= 0 && previous < list.Count ? previous : 0;
        if (LibraryList.SelectedIndex < 0)
        {
            DetailPanel.Children.Clear();
        }
    }

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = LibraryList.SelectedIndex;
        if (index < 0 || index >= LibraryList.Items.Count)
        {
            return;
        }

        // 视图过滤后侧边栏项与 _allEntries 不再一一对应——按选中项标题查找。
        if (LibraryList.Items[index] is StackPanel panel
            && panel.Children[0] is TextBlock titleBlock)
        {
            var title = titleBlock.Text.TrimStart('★', ' ');
            var entry = _allEntries.FirstOrDefault(en => en.Title == title);
            if (entry is not null)
            {
                RenderDetail(entry);
                return;
            }
        }

        RenderDetailPlaceholder();
    }

    private void RenderDetail(Entry entry)
    {
        DetailPanel.Children.Clear();
        _coverLoadCts?.Cancel();
        _coverLoadCts = new CancellationTokenSource();

        if (entry.Kind == "game")
        {
            RenderGameDetail(entry);
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

    /// <summary>游戏详情：封面头图 + 标题（可编辑）+ 收藏 + 摘要 + meta + 操作按钮排（T14/T15）。</summary>
    private void RenderGameDetail(Entry entry)
    {
        var raw = entry.Raw;
        var gameId = entry.Id;
        var revision = raw.GetProperty("revision").GetInt32();
        var title = raw.GetProperty("title").GetString() ?? "";
        var titleSource = raw.TryGetProperty("titleSource", out var ts) ? ts.GetString() : null;
        var summary = raw.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : "";
        var coverAssetId = raw.TryGetProperty("coverAssetId", out var cai) && cai.ValueKind == JsonValueKind.String ? cai.GetString() : null;

        // 封面头图（LRU 缓存 + 可取消加载）。
        if (coverAssetId is not null)
        {
            var cover = new Image
            {
                Height = 190,
                Width = 340,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12),
            };
            _ = LoadCoverAsync(coverAssetId, cover);
            DetailPanel.Children.Add(cover);
        }
        else
        {
            DetailPanel.Children.Add(new Border
            {
                Height = 190,
                Width = 340,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x30, 0x3c)),
                Child = new TextBlock
                {
                    Text = "暂无封面",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
                },
            });
        }

        // 标题行 + 收藏 + 编辑按钮。
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = TryFindResource<SolidColorBrush>("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var favoriteButton = new Button
        {
            Content = entry.Favorite ? "★ 已收藏" : "☆ 收藏",
            Style = (Style)TryFindResource("SteamButton"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        favoriteButton.Click += async (_, _) =>
        {
            await InvokeAsync("games.update", new
            {
                idempotencyKey = $"fav-{Guid.NewGuid():N}",
                gameId,
                favorite = !entry.Favorite,
                expectedRevision = revision,
            });
            await RefreshAsync();
        };
        titleRow.Children.Add(favoriteButton);
        var editButton = new Button
        {
            Content = "编辑标题",
            Style = (Style)TryFindResource("SteamButton"),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(editButton);
        DetailPanel.Children.Add(titleRow);

        var editRow = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var editBox = new TextBox { Width = 320, Text = title };
        var saveButton = new Button { Content = "保存", Style = (Style)TryFindResource("SteamGreenButton"), Margin = new Thickness(8, 0, 0, 0) };
        var cancelButton = new Button { Content = "取消", Style = (Style)TryFindResource("SteamButton"), Margin = new Thickness(6, 0, 0, 0) };
        editRow.Children.Add(editBox);
        editRow.Children.Add(saveButton);
        editRow.Children.Add(cancelButton);
        DetailPanel.Children.Add(editRow);

        editButton.Click += (_, _) =>
        {
            editRow.Visibility = Visibility.Visible;
            editButton.Visibility = Visibility.Collapsed;
            editBox.Focus();
        };
        cancelButton.Click += (_, _) =>
        {
            editRow.Visibility = Visibility.Collapsed;
            editButton.Visibility = Visibility.Visible;
        };
        saveButton.Click += async (_, _) =>
        {
            await SetTitleAsync(gameId, editBox.Text.Trim(), revision);
        };

        DetailPanel.Children.Add(new TextBlock
        {
            Text = $"引擎 {raw.GetProperty("engine").GetString() ?? "未识别"} · {raw.GetProperty("kind").GetString()} · rev {revision} · 标题来源 {titleSource ?? "auto"}",
            FontSize = 13,
            Foreground = TryFindResource<SolidColorBrush>("Accent"),
            Margin = new Thickness(0, 4, 0, 12),
        });

        if (!string.IsNullOrEmpty(summary))
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        DetailPanel.Children.Add(MetaLine("路径", raw.GetProperty("rootPath").GetString() ?? ""));
        DetailPanel.Children.Add(MetaLine("入库时间", raw.GetProperty("acceptedUtc").GetString() ?? ""));

        DetailPanel.Children.Add(ButtonRow(
            ("导入封面", () => ImportCoverAsync(gameId), "SteamButton"),
            ("打开目录", () => OpenDirectoryAsync(entry.PhysicalPath), "SteamButton")));
    }

    /// <summary>封面加载：LRU 缓存命中直接用；否则经 assets.get 读取（详情切换取消旧加载）。</summary>
    private async Task LoadCoverAsync(string assetId, Image coverImage)
    {
        if (CoverCache.TryGetValue(assetId, out var cached))
        {
            coverImage.Source = cached;
            return;
        }

        var cts = _coverLoadCts;
        try
        {
            var asset = await InvokeAsync("assets.get", new { assetId });
            if (!asset.Ok || cts is null || cts.IsCancellationRequested || !ReferenceEquals(_coverLoadCts, cts))
            {
                return;
            }

            var bytes = Convert.FromBase64String(asset.Data.GetProperty("dataBase64").GetString()!);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            using var stream = new MemoryStream(bytes);
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            if (cts.IsCancellationRequested || !ReferenceEquals(_coverLoadCts, cts))
            {
                return;
            }

            coverImage.Source = bitmap;
            CoverCache[assetId] = bitmap;
            CoverLruOrder.AddFirst(assetId);
            while (CoverLruOrder.Count > MaxCachedCovers && CoverLruOrder.Last is { } oldestNode)
            {
                CoverLruOrder.RemoveLast();
                CoverCache.Remove(oldestNode.Value);
            }
        }
        catch (Exception)
        {
            // 封面加载失败不影响详情页。
        }
    }

    private async Task ImportCoverAsync(string gameId)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var envelope = await InvokeAsync("assets.import", new
            {
                idempotencyKey = $"cover-{Guid.NewGuid():N}",
                gameId,
                sourcePath = dialog.FileName,
            });
            if (!envelope.Ok)
            {
                ShowError($"导入封面失败：{envelope.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            ShowError($"导入封面失败：{ex.Message}");
        }

        await RefreshAsync();
    }

    private async Task SetTitleAsync(string gameId, string newTitle, int expectedRevision)
    {
        if (newTitle.Length == 0)
        {
            ShowError("标题不能为空（清空语义随 fields.clear 提供）。");
            return;
        }

        try
        {
            var envelope = await InvokeAsync("fields.set", new
            {
                idempotencyKey = $"title-{Guid.NewGuid():N}",
                gameId,
                field = "title",
                value = newTitle,
                expectedRevision,
            });
            if (!envelope.Ok)
            {
                ShowError($"保存失败：{envelope.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            ShowError($"保存失败：{ex.Message}");
        }

        await RefreshAsync();
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

    private void OnViewSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewSelector.SelectedItem is ComboBoxItem item && _connection is not null)
        {
            _ = InvokeAsync("views.activate", new { viewId = (string)item.Tag });
        }

        RenderSidebar();
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl+F 聚焦搜索（T15 键盘支持）。
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _coverLoadCts?.Cancel();
        await DisposeConnectionAsync();
        base.OnClosed(e);
    }
}
