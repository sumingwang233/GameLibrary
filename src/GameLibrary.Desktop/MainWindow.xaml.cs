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
using WinForms = System.Windows.Forms;

namespace GameLibrary.Desktop;

/// <summary>
/// Desktop 纵切（T12/T15）：Steam 库风格视图——左侧视图切换/搜索/游戏列表、右侧详情与操作。
/// v1 审查修复：数据目录与游戏文件夹控件分离；开始游戏/启动方式配置；库根管理；
/// 扫描进度与取消；设置页（主题/缩放/托盘/开机启动/周期）；事件驱动刷新；
/// 按游戏 ID 选中（不再按标题猜）；"待审核候选"视图过滤分支补齐。
/// </summary>
public partial class MainWindow : Window
{
    private HostConnection? _connection;

    /// <summary>侧边栏条目（游戏或候选），供选中联动（Tag 直取，按 ID 不按标题）。</summary>
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

    /// <summary>事件轮询游标（审查意见：CLI/MCP 修改后 Desktop 自动刷新）。</summary>
    private long _eventCursor;
    private readonly DispatcherTimer _eventTimer;

    /// <summary>宿主设置快照（连接后读取；设置页读写）。ValueKind.Undefined = 未加载。</summary>
    private JsonElement _settings;

    private bool TryGetSetting(string name, out JsonElement value)
    {
        if (_settings.ValueKind == JsonValueKind.Object && _settings.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private string? _currentScanJobId;
    private bool _scanCancellationRequested;
    private readonly List<CollectionItem> _userCollections = [];
    private bool _updatingViewSelector;
    private int _loadedGamesCount;
    private int _loadedGamesTotal;
    private int _refreshVersion;
    private bool _loadingMoreGames;
    private string? _preferredGameId;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => App.NotifyMainWindowLoaded(this);
        // 窗口图标从 EXE 提取（ApplicationIcon 已把 App.ico 嵌入 Win32 资源；
        // 修复 UIA 测试抓到的启动崩溃：XAML pack URI 方式要求 Resource 嵌入，路径脆弱）。
        var exePath = Environment.ProcessPath;
        if (exePath is not null && System.IO.File.Exists(exePath))
        {
            try
            {
                var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon is not null)
                {
                    using var stream = new System.IO.MemoryStream();
                    sysIcon.Save(stream);
                    stream.Position = 0;
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.StreamSource = stream;
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    Icon = image;
                }
            }
            catch (Exception)
            {
                // 图标提取失败不阻塞启动。
            }
        }

        DataDirHint.Text = $"应用数据位置：{App.ResolvedDataDirectory}";
        ViewSelector.Items.Add(new ComboBoxItem { Content = "全部游戏", Tag = "all" });
        ViewSelector.Items.Add(new ComboBoxItem { Content = "收藏", Tag = "favorites" });
        ViewSelector.Items.Add(new ComboBoxItem { Content = "待确认游戏", Tag = "pending" });
        ViewSelector.SelectedIndex = 0;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            var newSearch = SearchBox.Text.Trim();
            if (newSearch != _searchText)
            {
                _searchText = newSearch;
                await RefreshAsync();
            }
        };
        SearchBox.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };

        _eventTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _eventTimer.Tick += async (_, _) => await PollEventsAsync();

        Loaded += async (_, _) => await ConnectAsync();
        Closing += SaveSplitterOnClose;
    }

}
