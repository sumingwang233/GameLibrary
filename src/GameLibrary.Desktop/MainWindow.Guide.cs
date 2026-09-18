using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameLibrary.Desktop;

/// <summary>首次使用与随时可打开的操作指南；标记只写入当前库的数据目录。</summary>
public partial class MainWindow : Window
{
    private bool _guideCheckedThisSession;

    private string GuideMarkerPath => Path.Combine(App.ResolvedDataDirectory, "desktop-guide-v1.json");

    private async Task MaybeShowFirstUseGuideAsync()
    {
        if (_guideCheckedThisSession || _loadedGamesTotal != 0 || File.Exists(GuideMarkerPath))
        {
            return;
        }

        _guideCheckedThisSession = true;
        var roots = await InvokeAsync("roots.list");
        if (roots.Ok && roots.Data.GetProperty("items").GetArrayLength() == 0)
        {
            _ = Dispatcher.BeginInvoke(() => ShowGuideDialog(firstUse: true), DispatcherPriority.ApplicationIdle);
        }
    }

    private void ShowGuideDialog(bool firstUse, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = "使用指南",
            Width = 590,
            Height = 570,
            MinWidth = 430,
            MinHeight = 350,
            Owner = owner ?? this,
            FontFamily = FontFamily,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource<SolidColorBrush>("BgMain"),
        };
        AutomationProperties.SetName(dialog, "GameLibrary 使用指南");

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = firstUse ? "欢迎使用 GameLibrary" : "如何使用 GameLibrary",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        void AddStep(string title, string description)
        {
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 3),
            });
            panel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            });
        }

        AddStep("1 · 添加游戏", "选择游戏所在目录作为游戏库并扫描。扫描不到时，可直接选择 EXE、SWF 或 Windows 快捷方式 LNK；只添加目录时，稍后还需指定启动方式。");
        AddStep("2 · 确认扫描结果", "扫描发现的内容先进入左侧「待确认游戏」。确认无误后点击「加入游戏库」，它就会出现在「全部游戏」中。");
        AddStep("3 · 开始游戏", "选中游戏卡片，检查或配置启动方式，再点击「开始游戏」。加入库和移出库都不会删除原游戏文件。");
        AddStep("4 · 整理与退出", "可新建标签、筛选、排序或批量整理游戏；在设置中切换主题、字体和文字大小。关闭主窗口默认缩到托盘。");
        AddStep("翻译策略", "自动：按游戏位置和识别结果决定是否需要翻译。必须翻译：没有可用翻译工具时不启动。无需翻译：直接启动原版游戏。");

        var actions = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        string? nextAction = null;
        Button Action(string label, string action, bool primary = false)
        {
            var button = new Button
            {
                Content = label,
                MinHeight = 32,
                MinWidth = 110,
                Margin = new Thickness(0, 0, 8, 8),
                Style = (Style)TryFindResource(primary ? "SteamBlueButton" : "SteamButton"),
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                nextAction = action;
                dialog.Close();
            };
            return button;
        }

        actions.Children.Add(Action("添加游戏库", "add-folder", primary: true));
        actions.Children.Add(Action("手动添加游戏", "manual"));
        actions.Children.Add(Action("查看待审核", "pending"));
        panel.Children.Add(actions);

        var done = Action("我知道了", "done");
        done.IsDefault = true;
        done.IsCancel = true;
        panel.Children.Add(done);

        dialog.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        dialog.ShowDialog();
        (owner ?? this).Focus();

        if (firstUse)
        {
            try
            {
                File.WriteAllText(GuideMarkerPath, JsonSerializer.Serialize(new { seen = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 引导标记不是库数据；保存失败不阻止游戏库使用。
            }
        }

        switch (nextAction)
        {
            case "add-folder": OnAddGameFolderClick(this, new RoutedEventArgs()); break;
            case "manual": OnManualAddClick(this, new RoutedEventArgs()); break;
            case "pending": ViewSelector.SelectedIndex = 2; break;
        }
    }
}
