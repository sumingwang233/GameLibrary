using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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



/// <summary>MainWindow 的 Lifetime 关注点（阶段三结构拆分，partial；MVVM 化前置步骤）。</summary>
public partial class MainWindow : Window
{
    internal void ShowFromSecondaryLaunch() => ShowFromTray();

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

    // ---------- 分栏记忆 ----------

    private void SaveSplitterOnClose(object? sender, System.ComponentModel.CancelEventArgs e) => SaveUiPrefs();

    private void SaveUiPrefs()
    {
        try
        {
            var path = UiPrefsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 合并进已有对象。整文件重写会抹掉 Tauri 记下的 sort / layout。
            var root = ReadUiPrefsObject(path);
            root["leftWidth"] = JsonValue.Create(LeftColumn.Width.Value);
            if (_persistSort && IsPersistedSort(_sortMode))
            {
                root["sort"] = JsonValue.Create(_sortMode);
            }

            var json = root.ToJsonString(new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // UI 记忆失败可忽略。
        }
    }

    private void LoadUiPrefs()
    {
        try
        {
            var path = UiPrefsPath;
            if (!File.Exists(path))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("leftWidth", out var width)
                && width.TryGetDouble(out var parsed)
                && parsed is >= 220 and <= 800)
            {
                LeftColumn.Width = new GridLength(parsed);
            }

            // 文件里没有 sort 时保持构造时的默认值，关窗也不补写，避免把 Tauri 的默认排序改掉。
            if (document.RootElement.TryGetProperty("sort", out var sortElement)
                && sortElement.ValueKind == JsonValueKind.String
                && sortElement.GetString() is string sort
                && IsPersistedSort(sort))
            {
                _sortMode = sort;
                _persistSort = true;
                for (var index = 0; index < SortSelector.Items.Count; index++)
                {
                    if (SortSelector.Items[index] is ComboBoxItem item && item.Tag is string tag && tag == sort)
                    {
                        SortSelector.SelectedIndex = index;
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 记忆缺失走默认 290 / 名称 A–Z。
        }
    }

    private static JsonObject ReadUiPrefsObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static bool IsPersistedSort(string? sort) =>
        sort is "title-asc" or "title-desc" or "updated-desc" or "accepted-desc";

    private static string UiPrefsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLibrary", "ui.json");

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭按钮按设置缩到托盘；托盘菜单中的显式退出仍会结束界面。
        var closeToTray = !TryGetSetting("closeToTray", out var ctt) || ctt.GetBoolean();
        if (!_exitRequested && closeToTray)
        {
            e.Cancel = true;
            EnsureTrayIconVisible();
            Hide();
            return;
        }

        _coverLoadCts?.Cancel();
        _eventTimer.Stop();
        await DisposeConnectionAsync();
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnClosing(e);
    }

    /// <summary>true=用户显式退出（托盘菜单）；false=点关闭按钮（按设置缩托盘）。</summary>
    private bool _exitRequested;

    private WinForms.NotifyIcon? _trayIcon;

    private void EnsureTrayIconVisible()
    {
        _trayIcon ??= CreateTrayIcon();
        _trayIcon.Visible = true;
    }

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        // 审查意见 #1：托盘使用应用图标（EXE 内嵌 ApplicationIcon）。
        System.Drawing.Icon? icon = null;
        try
        {
            icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        var notifyIcon = new WinForms.NotifyIcon
        {
            Text = "GameLibrary",
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Visible = false,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L10n.T("显示主窗口"), null, (_, _) => ShowFromTray());
        menu.Items.Add(L10n.T("退出界面（后台继续运行）"), null, (_, _) => ExitInterfaceOnly());
        menu.Items.Add(L10n.T("停止后台并退出"), null, (_, _) => StopHostAndExit());
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        return notifyIcon;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>退出界面：只结束 Desktop；Host 继续扫描并供 CLI/MCP 使用（AI-09 退出语义）。</summary>
    private void ExitInterfaceOnly()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>停止后台并退出：host.stop（Host 排空后自行退出，不杀游戏/翻译器），随后关闭界面。</summary>
    private async void StopHostAndExit()
    {
        try
        {
            if (_connection is not null)
            {
                await InvokeAsync("host.stop", new { idempotencyKey = $"ui-stop-{Guid.NewGuid():N}" });
            }
        }
        catch (Exception)
        {
            // 宿主可能已退出；界面退出不受影响。
        }

        _exitRequested = true;
        Close();
    }
}
