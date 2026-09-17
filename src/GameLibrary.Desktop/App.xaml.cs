using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;

namespace GameLibrary.Desktop;

/// <summary>
/// Desktop 宿主（v1 审查修复）：
/// 1) 首次双击不再要求 --data-dir——默认 %LOCALAPPDATA%\GameLibrary，记忆于引导配置；
/// 2) 按 Windows 用户会话建立桌面单实例——后启动进程只唤醒既有窗口后退出；
/// 3) 主题（dark/light）在启动与设置变更时切换（资源字典整体替换）。
/// </summary>
public partial class App : Application
{
    /// <summary>启动参数（兼容 CLI 风格 --data-dir 显式指定）。</summary>
    public static string[] Args { get; } = Environment.GetCommandLineArgs();

    /// <summary>解析后的数据目录（bootstrap 配置 &gt; 命令行 &gt; 默认 %LOCALAPPDATA%\GameLibrary）。</summary>
    public static string ResolvedDataDirectory { get; private set; } = "";

    private const string BootstrapFolderName = "GameLibrary";
    private const string BootstrapFileName = "desktop.json";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _wakeEvent;
    private static Thread? _wakeThread;
    private static volatile bool _exiting;
    private static bool _pendingWake;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!AcquireSingleInstance())
        {
            // 已有实例：唤醒其窗口后自行退出（审查意见 #10）。
            SignalWake();
            Shutdown(0);
            return;
        }

        ResolvedDataDirectory = ResolveDataDirectory();
        // 只有真正运行的实例才能更新普通双击启动记住的库位置。
        if (!HasExplicitDataDirectory())
        {
            RememberBootstrap(ResolvedDataDirectory);
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _wakeEvent?.Set();
        _wakeThread?.Join(500);
        _wakeEvent?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool HasExplicitDataDirectory() =>
        Args.Skip(1).Take(Math.Max(0, Args.Length - 2))
            .Any(arg => arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));

    /// <summary>数据目录解析：--data-dir 显式 &gt; 引导配置记忆 &gt; 默认 %LOCALAPPDATA%\GameLibrary。</summary>
    private static string ResolveDataDirectory()
    {
        var args = Args;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        var remembered = ReadBootstrap();
        if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered))
        {
            return remembered;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            BootstrapFolderName);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string BootstrapPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        BootstrapFolderName, BootstrapFileName);

    private static string? ReadBootstrap()
    {
        try
        {
            if (!File.Exists(BootstrapPath))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(BootstrapPath));
            return document.RootElement.TryGetProperty("dataDir", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RememberBootstrap(string dataDirectory)
    {
        try
        {
            var path = BootstrapPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(new { dataDir = dataDirectory }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 引导记忆失败不影响启动（下次仍走默认/命令行）。
        }
    }

    // Local 限定登录会话；SID 避免同一会话内不同账户互相阻断。
    // 桌面单实例不依赖数据目录：显式 --data-dir 也不能再启动第二个桌面窗口。
    private static readonly string InstanceName = ResolveInstanceName();

    private static string ResolveInstanceName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户");
    }

    private static string MutexName => $"Local\\GameLibrary.Desktop.{InstanceName}";

    private static string WakeEventName => $"Local\\GameLibrary.Desktop.Wake.{InstanceName}";

    private bool AcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            StartWakeListener();
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private static void SignalWake()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(WakeEventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // 唤醒尽力而为；主实例正常情况下已创建该事件。
        }
    }

    private void StartWakeListener()
    {
        _wakeEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, WakeEventName);
        _wakeThread = new Thread(() =>
        {
            while (!_exiting && _wakeEvent.WaitOne())
            {
                if (_exiting || Dispatcher.HasShutdownStarted)
                {
                    break;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is MainWindow window && window.IsLoaded)
                    {
                        window.ShowFromSecondaryLaunch();
                    }
                    else
                    {
                        _pendingWake = true;
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "GameLibrary.Desktop.WakeListener",
        };
        _wakeThread.Start();
    }

    internal static void NotifyMainWindowLoaded(MainWindow window)
    {
        if (!_pendingWake)
        {
            return;
        }

        _pendingWake = false;
        window.ShowFromSecondaryLaunch();
    }

    /// <summary>运行中切换主题（settings.theme）：整体替换合并字典（审查意见：StaticResource 固定深色无法切换）。</summary>
    public static void ApplyTheme(string theme)
    {
        var resolved = theme == "light" ? "Light" : "Dark";
        var uri = new Uri($"Theme/{resolved}.xaml", UriKind.Relative);
        Current.Resources.MergedDictionaries.Clear();
        Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    public static void ApplyFontFamily(string family)
    {
        FontFamily font;
        try
        {
            font = new FontFamily(family);
        }
        catch (ArgumentException)
        {
            font = new FontFamily("Segoe UI");
        }
        Current.Resources["UiFontFamily"] = font;
        foreach (Window window in Current.Windows)
        {
            window.FontFamily = font;
        }
    }
}
