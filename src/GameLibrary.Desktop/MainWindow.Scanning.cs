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



/// <summary>MainWindow 的 Scanning 关注点（阶段三结构拆分，partial；MVVM 化前置步骤）。</summary>
public partial class MainWindow : Window
{
    // ---------- 顶栏操作 ----------

    private async void OnManualAddClick(object sender, RoutedEventArgs e)
    {
        var choice = new Window
        {
            Title = "手动添加游戏",
            Width = 470,
            Height = 230,
            Owner = this,
            FontFamily = FontFamily,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = TryFindResource<SolidColorBrush>("BgMain"),
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "扫描没有找到游戏？可以手动指定主程序，或先把游戏目录加入库。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            Margin = new Thickness(0, 0, 0, 15),
        });
        var fileButton = new Button
        {
            Content = "选择游戏主程序或快捷方式（EXE / SWF / LNK）",
            Style = (Style)TryFindResource("SteamBlueButton"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var folderButton = new Button
        {
            Content = "选择游戏目录（稍后配置启动方式）",
            Style = (Style)TryFindResource("SteamButton"),
        };
        string? selectionKind = null;
        fileButton.Click += (_, _) => { selectionKind = "file"; choice.DialogResult = true; };
        folderButton.Click += (_, _) => { selectionKind = "directory"; choice.DialogResult = true; };
        panel.Children.Add(fileButton);
        panel.Children.Add(folderButton);
        choice.Content = panel;
        if (choice.ShowDialog() != true)
        {
            return;
        }

        string? sourcePath;
        if (selectionKind == "file")
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择游戏主程序",
                Filter = "游戏主程序或快捷方式|*.exe;*.swf;*.lnk",
                CheckFileExists = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            sourcePath = picker.ShowDialog(this) == true ? picker.FileName : null;
        }
        else
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择游戏目录",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            sourcePath = picker.ShowDialog(this) == true ? picker.FolderName : null;
        }

        if (sourcePath is null)
        {
            return;
        }

        try
        {
            var roots = await InvokeAsync("roots.list");
            if (!roots.Ok)
            {
                ShowError($"读取游戏库失败：{roots.Error?.Message}");
                return;
            }

            var alreadyAuthorized = roots.Data.GetProperty("items").EnumerateArray()
                .Any(root => IsWithinDirectory(root.GetProperty("path").GetString() ?? "", sourcePath));
            if (!alreadyAuthorized)
            {
                var folderToAuthorize = selectionKind == "file"
                    ? Path.GetDirectoryName(sourcePath)!
                    : sourcePath;
                var answer = MessageBox.Show(this,
                    $"为了保存这个游戏，需要授权以下文件夹作为游戏库范围：\n{folderToAuthorize}\n\n授权后，后台扫描也可能检查此文件夹。是否继续？",
                    "确认游戏库范围", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }

                var added = await InvokeAsync("roots.add", new { root = folderToAuthorize });
                if (!added.Ok)
                {
                    ShowError($"授权游戏库失败：{added.Error?.Message}");
                    return;
                }
            }

            var created = await InvokeAsync("games.create", new
            {
                idempotencyKey = $"ui-gamecreate-{Guid.NewGuid():N}",
                sourcePath,
            });
            if (!created.Ok)
            {
                ShowError($"手动添加游戏失败：{created.Error?.Message}");
                return;
            }

            var gameId = created.Data.GetProperty("gameId").GetString() ?? "";
            if (selectionKind == "file"
                && created.Data.TryGetProperty("launchSuggestion", out var suggestion)
                && suggestion.ValueKind == JsonValueKind.Object)
            {
                var profile = await InvokeAsync("profiles.create", new
                {
                    idempotencyKey = $"ui-manualprofile-{gameId}",
                    gameId,
                    executablePath = suggestion.GetProperty("executablePath").GetString(),
                    argv = suggestion.GetProperty("argv").EnumerateArray()
                        .Select(arg => arg.GetString() ?? "").ToArray(),
                    cwd = suggestion.GetProperty("cwd").GetString(),
                    isDefault = true,
                });
                if (!profile.Ok)
                {
                    ShowError($"游戏已入库，但启动方式未保存：{profile.Error?.Message}。可在详情页重试配置。");
                }
                else
                {
                    ShowError(null);
                }
            }
            else
            {
                ShowError(null);
            }

            _updatingViewSelector = true;
            try
            {
                ViewSelector.SelectedIndex = 0;
            }
            finally
            {
                _updatingViewSelector = false;
            }
            // 新增项必须可见；否则用户先前的搜索条件会把成功入库的游戏藏起来。
            SearchBox.Text = "";
            _searchTimer.Stop();
            _searchText = "";
            // 事件轮询可能与此次刷新交错，交给下一次成功渲染按 ID 完成选中。
            _preferredGameId = gameId;
            await RefreshAsync();
            SetStatus(selectionKind == "file"
                ? "游戏已手动加入库；可在右侧查看启动方式"
                : "游戏目录已入库；请在右侧配置启动方式");
        }
        catch (Exception ex)
        {
            ShowError($"手动添加游戏失败：{ex.Message}");
        }
    }

    private static bool IsWithinDirectory(string directory, string path)
    {
        if (directory.Length == 0)
        {
            return false;
        }

        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>添加游戏库（roots.add）：图形化选择目录；与数据目录是两个不同概念/控件。</summary>
    private async void OnAddGameFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏库（扫描范围；不要选择应用数据目录）",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var envelope = await InvokeAsync("roots.add", new { root = dialog.FolderName });
            if (!envelope.Ok)
            {
                ShowError($"添加游戏库失败：{envelope.Error?.Message}");
                return;
            }

            SetStatus("游戏库已添加（显式授权扫描边界）");
            ShowError(null);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"添加游戏库失败：{ex.Message}");
        }
    }

    /// <summary>游戏库管理：列表 + 移除（roots.remove，仅解除监控边界）。</summary>
    private async void OnRootsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var envelope = await InvokeAsync("roots.list");
            if (!envelope.Ok)
            {
                ShowError($"读取游戏库失败：{envelope.Error?.Message}");
                return;
            }

            var dialog = new Window
            {
                Title = "游戏库",
                Width = 560,
                Height = 320,
                Owner = this,
                FontFamily = FontFamily,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = TryFindResource<SolidColorBrush>("BgMain"),
            };
            var list = new StackPanel { Margin = new Thickness(14) };
            var items = envelope.Data.GetProperty("items");
            if (items.GetArrayLength() == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "还没有游戏库。点主窗口顶部「添加游戏库」开始。",
                    Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
                });
            }

            foreach (var root in items.EnumerateArray())
            {
                var rootId = root.GetProperty("rootId").GetString() ?? "";
                var path = root.GetProperty("path").GetString() ?? "";
                var revision = root.TryGetProperty("revision", out var rv) ? rv.GetInt32() : 1;
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var removeButton = new Button
                {
                    Content = "移除",
                    Style = (Style)TryFindResource("SteamButton"),
                };
                DockPanel.SetDock(removeButton, Dock.Right);
                row.Children.Add(removeButton);
                row.Children.Add(new TextBlock
                {
                    Text = path,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = $"{path}（{rootId}）",
                });
                removeButton.Click += async (_, _) =>
                {
                    var result = await InvokeAsync("roots.remove", new
                    {
                        idempotencyKey = $"ui-rootrm-{Guid.NewGuid():N}",
                        rootId,
                        expectedRevision = revision,
                    });
                    if (!result.Ok)
                    {
                        ShowError($"移除失败：{result.Error?.Message}");
                        return;
                    }

                    row.Visibility = Visibility.Collapsed;
                    SetStatus("游戏库已移除（库内游戏保留）");
                    await RefreshAsync();
                };
                list.Children.Add(row);
            }

            dialog.Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowError($"打开游戏库管理失败：{ex.Message}");
        }
    }

    /// <summary>扫描：对全部游戏库依次扫描；进度与取消（审查意见：扫描进度/取消/解释）。</summary>
    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        List<string> roots;
        try
        {
            var envelope = await InvokeAsync("roots.list");
            if (!envelope.Ok)
            {
                ShowError($"读取游戏库失败：{envelope.Error?.Message}");
                return;
            }

            roots = envelope.Data.GetProperty("items").EnumerateArray()
                .Select(r => r.GetProperty("path").GetString() ?? "")
                .Where(p => p.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            ShowError($"扫描失败：{ex.Message}");
            return;
        }

        if (roots.Count == 0)
        {
            ShowError("还没有游戏库；先点「添加游戏库」。");
            return;
        }

        ScanButton.IsEnabled = false;
        _scanCancellationRequested = false;
        ShowError(null);
        CancelScanButton.Visibility = Visibility.Visible;
        ScanProgressText.Visibility = Visibility.Visible;
        var scanFailed = false;
        var scanPartial = false;
        var candidatesFound = 0L;
        try
        {
            for (var i = 0; i < roots.Count; i++)
            {
                if (_scanCancellationRequested || scanFailed)
                {
                    break;
                }

                var root = roots[i];
                ScanProgressText.Text = $"准备扫描（根 {i + 1}/{roots.Count}）";
                var start = await InvokeAsync("scan.start", new { root });
                if (!start.Ok)
                {
                    ShowError($"扫描启动失败（{root}）：{start.Error?.Code} {start.Error?.Message}");
                    scanFailed = true;
                    break;
                }

                var jobId = start.JobId ?? throw new InvalidOperationException("后台未返回扫描任务编号");
                _currentScanJobId = jobId;
                while (true)
                {
                    var progress = await InvokeAsync("scan.coverage", new { jobId });
                    if (!progress.Ok)
                    {
                        throw new InvalidOperationException($"查询扫描进度失败：{progress.Error?.Message}");
                    }

                    var stateName = progress.Data.GetProperty("state").GetString();
                    var coverage = progress.Data.GetProperty("coverage");
                    if (coverage.ValueKind == JsonValueKind.Object)
                    {
                        ScanProgressText.Text = DesktopScanProgress.Format(coverage, i + 1, roots.Count);
                    }

                    if (stateName is "succeeded" or "failed" or "cancelled")
                    {
                        if (coverage.ValueKind == JsonValueKind.Object)
                        {
                            if (coverage.TryGetProperty("candidatesFound", out var found)
                                && found.ValueKind == JsonValueKind.Number)
                            {
                                candidatesFound += found.GetInt64();
                            }

                            if (coverage.TryGetProperty("completion", out var completion)
                                && completion.GetString() == "partial")
                            {
                                scanPartial = true;
                            }
                        }

                        if (stateName == "failed")
                        {
                            ShowError($"扫描失败（{root}）；请检查游戏库后重试。");
                            scanFailed = true;
                        }
                        else if (stateName == "cancelled")
                        {
                            _scanCancellationRequested = true;
                        }

                        break;
                    }

                    await Task.Delay(350);
                }

                _currentScanJobId = null;
            }

            if (_scanCancellationRequested)
            {
                SetStatus("扫描已取消；其余游戏库未继续扫描");
            }
            else if (scanFailed)
            {
                SetStatus("扫描未完成；请查看右侧错误提示");
            }
            else
            {
                var candidates = await InvokeAsync("candidates.list");
                if (!candidates.Ok)
                {
                    throw new InvalidOperationException($"读取待确认项目失败：{candidates.Error?.Message}");
                }

                var pending = candidates.Data.GetProperty("items").EnumerateArray()
                    .Count(item => item.GetProperty("reviewState").GetString() == "pendingReview");
                ScanProgressText.Text = "扫描完成";
                if (pending > 0)
                {
                    // 有新候选时自动切到待审核视图（用户核心诉求是审核入库）。
                    foreach (var item in ViewSelector.Items.OfType<ComboBoxItem>()
                        .Where(i => (string?)i.Tag == "pending"))
                    {
                        ViewSelector.SelectedItem = item;
                        break;
                    }

                    SetStatus(scanPartial
                        ? $"扫描完成但部分分支无法访问：本次识别 {candidatesFound} 个候选，当前有 {pending} 个待确认"
                        : $"扫描完成：本次识别 {candidatesFound} 个候选，当前有 {pending} 个待确认，已切到「待确认游戏」");
                }
                else
                {
                    SetStatus(scanPartial
                        ? $"扫描完成但部分分支无法访问：本次识别 {candidatesFound} 个候选"
                        : candidatesFound > 0
                            ? $"扫描完成：本次识别 {candidatesFound} 个候选，暂无待确认项目"
                            : "扫描完成：未发现可审核的游戏");
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"扫描失败：{ex.Message}");
        }
        finally
        {
            _currentScanJobId = null;
            _scanCancellationRequested = false;
            ScanButton.IsEnabled = true;
            CancelScanButton.IsEnabled = true;
            CancelScanButton.Visibility = Visibility.Collapsed;
            ScanProgressText.Visibility = Visibility.Collapsed;
        }

        await RefreshAsync();
    }

    private async void OnCancelScanClick(object sender, RoutedEventArgs e)
    {
        if (_currentScanJobId is null)
        {
            return;
        }

        _scanCancellationRequested = true;
        CancelScanButton.IsEnabled = false;
        try
        {
            var cancellation = await InvokeAsync("scan.cancel", new { jobId = _currentScanJobId });
            if (!cancellation.Ok)
            {
                ShowError($"取消扫描失败：{cancellation.Error?.Message}；当前任务完成后仍会停止后续文件夹。");
                return;
            }

            SetStatus("已请求取消扫描（检查点生效）");
        }
        catch (Exception ex)
        {
            ShowError($"取消失败：{ex.Message}");
        }
    }

    // ---------- 设置页 ----------

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowSettingsDialogAsync();
        }
        catch (Exception ex)
        {
            ShowError($"打开设置失败：{ex.Message}");
        }
    }
}
