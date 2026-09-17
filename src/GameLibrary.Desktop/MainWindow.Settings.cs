using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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



/// <summary>MainWindow 的 Settings 关注点（阶段三结构拆分，partial；MVVM 化前置步骤）。</summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 设置对话框。打开时实时拉取 settings.get；若编辑期间其他入口修改了设置，
    /// 不用旧表单覆盖新值，而是提示用户重新打开设置确认。
    /// </summary>
    private async Task ShowSettingsDialogAsync()
    {
        var settings = await InvokeAsync("settings.get");
        if (settings.Ok)
        {
            _settings = settings.Data.Clone();
        }
        else
        {
            ShowError($"读取设置失败：{settings.Error?.Message}");
            return;
        }

        var savedTheme = TryGetSetting("theme", out var originalTheme)
            ? originalTheme.GetString() ?? "dark"
            : "dark";
        var savedFontFamily = TryGetSetting("uiFontFamily", out var originalFont)
            ? originalFont.GetString() ?? "Segoe UI"
            : "Segoe UI";
        string? selectedCacheParentDirectory = TryGetSetting("cacheParentDirectory", out var cacheSetting)
            && cacheSetting.ValueKind == JsonValueKind.String
                ? cacheSetting.GetString()
                : null;
        var settingsSaved = false;

        var dialog = new Window
        {
            Title = "设置",
            Width = 600,
            Height = 620,
            Owner = this,
            FontFamily = FontFamily,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource<SolidColorBrush>("BgMain"),
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(18) };

        Label SettingLabel(string text, Control? target = null) => new()
        {
            Content = text,
            Target = target,
            FontSize = 12,
            Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
            Margin = new Thickness(0, 10, 0, 4),
        };

        // 主题
        var themeBox = new ComboBox { SelectedIndex = 0 };
        themeBox.Items.Add(new ComboBoxItem { Content = "深色", Tag = "dark" });
        themeBox.Items.Add(new ComboBoxItem { Content = "浅色", Tag = "light" });
        var currentTheme = TryGetSetting("theme", out var ct) ? ct.GetString() : "dark";
        themeBox.SelectedIndex = currentTheme == "light" ? 1 : 0;
        AutomationProperties.SetName(themeBox, "主题");
        panel.Children.Add(SettingLabel("主题（即时预览）", themeBox));
        panel.Children.Add(themeBox);

        // 字体族（从本机已安装字体中选择；实际选择通过 Host 设置持久化）。
        var availableFonts = Fonts.SystemFontFamilies.Select(font => font.Source)
            .Where(name => name.Length is >= 1 and <= 100
                && name.All(character => char.IsLetterOrDigit(character)
                    || character is ' ' or '-' or '_' or '.'))
            .Append("Segoe UI")
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var fontBox = new ComboBox
        {
            ItemsSource = availableFonts,
            SelectedItem = availableFonts.FirstOrDefault(name =>
                name.Equals(savedFontFamily, StringComparison.CurrentCultureIgnoreCase)) ?? "Segoe UI",
            MaxDropDownHeight = 260,
        };
        AutomationProperties.SetName(fontBox, "界面字体");
        panel.Children.Add(SettingLabel("界面字体", fontBox));
        panel.Children.Add(fontBox);

        // 界面缩放同时放大文字和控件，避免仅放大字号造成裁切。
        var scaleSlider = new Slider
        {
            Minimum = 0.85,
            Maximum = 1.6,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            Value = TryGetSetting("uiFontScale", out var fs2) ? fs2.GetDouble() : 1.0,
        };
        AutomationProperties.SetName(scaleSlider, "文字与界面大小");
        var scaleLabel = SettingLabel($"文字与界面大小：{scaleSlider.Value:0.00}×", scaleSlider);
        scaleSlider.ValueChanged += (_, _) =>
            scaleLabel.Content = $"文字与界面大小：{scaleSlider.Value:0.00}×";
        panel.Children.Add(scaleLabel);
        panel.Children.Add(scaleSlider);

        // 行为
        var trayCheck = new CheckBox
        {
            Content = "关闭窗口时缩到托盘（后台继续运行）",
            Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            IsChecked = !TryGetSetting("closeToTray", out var ctt) || ctt.GetBoolean(),
            Margin = new Thickness(0, 12, 0, 0),
        };
        panel.Children.Add(trayCheck);
        var autostartCheck = new CheckBox
        {
            Content = "开机自动启动（用户启动文件夹快捷方式，不写注册表）",
            Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            IsChecked = TryGetSetting("autostartEnabled", out var ase) && ase.GetBoolean(),
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(autostartCheck);

        // 扫描周期
        var intervalBox = new TextBox
        {
            Text = (TryGetSetting("scanIntervalMinutes", out var sim) ? sim.GetInt32() : 15).ToString(),
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(intervalBox, "后台核对周期（分钟）");
        panel.Children.Add(SettingLabel("后台核对周期（分钟）", intervalBox));
        panel.Children.Add(intervalBox);

        // 缓存位置只允许通过系统目录选择器变更；Host 会在所选位置下管理专属子目录。
        var cachePathBox = new TextBox
        {
            IsReadOnly = true,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(cachePathBox, "缓存存放位置");
        AutomationProperties.SetAutomationId(cachePathBox, "CacheDirectoryPath");
        var chooseCacheButton = new Button
        {
            Content = "选择文件夹…",
            Style = (Style)TryFindResource("SteamButton"),
            MinWidth = 105,
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetName(chooseCacheButton, "选择缓存文件夹");
        AutomationProperties.SetAutomationId(chooseCacheButton, "ChooseCacheDirectoryButton");
        var resetCacheButton = new Button
        {
            Content = "恢复默认",
            Style = (Style)TryFindResource("SteamButton"),
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetName(resetCacheButton, "恢复默认缓存位置");
        AutomationProperties.SetAutomationId(resetCacheButton, "ResetCacheDirectoryButton");
        var cacheRow = new Grid();
        cacheRow.ColumnDefinitions.Add(new ColumnDefinition());
        cacheRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cacheRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(cachePathBox, 0);
        Grid.SetColumn(chooseCacheButton, 1);
        Grid.SetColumn(resetCacheButton, 2);
        cacheRow.Children.Add(cachePathBox);
        cacheRow.Children.Add(chooseCacheButton);
        cacheRow.Children.Add(resetCacheButton);
        panel.Children.Add(SettingLabel("封面缓存存放位置", cachePathBox));
        panel.Children.Add(cacheRow);

        void RefreshCachePath()
        {
            cachePathBox.Text = selectedCacheParentDirectory
                ?? Path.Combine(App.ResolvedDataDirectory, "cache") + "（默认）";
            cachePathBox.ToolTip = selectedCacheParentDirectory is null
                ? "默认缓存在应用数据位置中"
                : $"应用只会管理此位置下的 GameLibraryCache 专属子目录：{selectedCacheParentDirectory}";
        }

        RefreshCachePath();
        chooseCacheButton.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择缓存存放位置",
                InitialDirectory = selectedCacheParentDirectory
                    ?? Path.GetDirectoryName(App.ResolvedDataDirectory)
                    ?? App.ResolvedDataDirectory,
            };
            if (picker.ShowDialog(dialog) == true)
            {
                selectedCacheParentDirectory = picker.FolderName;
                RefreshCachePath();
            }
        };
        resetCacheButton.Click += (_, _) =>
        {
            selectedCacheParentDirectory = null;
            RefreshCachePath();
        };

        // 应用数据位置（只读展示 + 提示）
        panel.Children.Add(SettingLabel("应用数据位置（与游戏文件夹无关）"));
        panel.Children.Add(new TextBlock
        {
            Text = App.ResolvedDataDirectory,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            ToolTip = App.ResolvedDataDirectory,
        });

        var statusLine = new TextBlock
        {
            Foreground = TryFindResource<SolidColorBrush>("Danger"),
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(statusLine);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var saveButton = new Button { Content = "保存", Style = (Style)TryFindResource("SteamGreenButton"), MinWidth = 110 };
        var closeButton = new Button { Content = "关闭", Style = (Style)TryFindResource("SteamButton"), MinWidth = 90, Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);

        themeBox.SelectionChanged += (_, _) =>
        {
            if (themeBox.SelectedItem is ComboBoxItem selected)
            {
                App.ApplyTheme((string)selected.Tag);
                dialog.Background = TryFindResource<SolidColorBrush>("BgMain");
            }
        };
        fontBox.SelectionChanged += (_, _) =>
        {
            if (fontBox.SelectedItem is string family)
            {
                App.ApplyFontFamily(family);
            }
        };

        saveButton.Click += async (_, _) =>
        {
            try
            {
                if (!int.TryParse(intervalBox.Text.Trim(), out var interval) || interval is < 1 or > 10080)
                {
                    statusLine.Text = "核对周期必须是 1–10080 的整数。";
                    return;
                }

                async Task<Envelope<JsonElement>> SaveWithRevisionAsync(int expectedRevision)
                {
                    var patch = new Dictionary<string, object?>
                    {
                        ["idempotencyKey"] = $"ui-settings-{Guid.NewGuid():N}",
                        ["expectedRevision"] = expectedRevision,
                        ["theme"] = themeBox.SelectedItem is ComboBoxItem ti ? (string)ti.Tag : "dark",
                        ["uiFontScale"] = Math.Round(scaleSlider.Value, 2),
                        ["uiFontFamily"] = fontBox.SelectedItem as string ?? "Segoe UI",
                        ["closeToTray"] = trayCheck.IsChecked == true,
                        ["autostartEnabled"] = autostartCheck.IsChecked == true,
                        ["scanIntervalMinutes"] = interval,
                        ["cacheParentDirectory"] = selectedCacheParentDirectory,
                    };
                    return await InvokeAsync("settings.update", patch);
                }

                var currentRevision = TryGetSetting("revision", out var rv) ? rv.GetInt32() : 0;
                var envelope = await SaveWithRevisionAsync(currentRevision);

                if (!envelope.Ok)
                {
                    statusLine.Text = envelope.Error?.Code == ErrorCodes.RevisionConflict
                        ? "设置已被其他入口修改。请关闭并重新打开设置，确认新值后再保存。"
                        : $"保存失败：{envelope.Error?.Message}";
                    return;
                }

                _settings = envelope.Data.Clone();
                settingsSaved = true;
                ApplyFontScale(TryGetSetting("uiFontScale", out var savedScale) ? savedScale.GetDouble() : 1.0);
                App.ApplyFontFamily(TryGetSetting("uiFontFamily", out var savedFamily)
                    ? savedFamily.GetString() ?? "Segoe UI" : "Segoe UI");
                SetStatus("设置已保存");
                ShowError(null);
                dialog.Close();
            }
            catch (Exception ex)
            {
                statusLine.Text = $"保存失败：{ex.Message}";
            }
        };
        saveButton.IsDefault = true;
        saveButton.MinHeight = 28;
        closeButton.IsCancel = true;
        closeButton.MinHeight = 28;
        closeButton.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) =>
        {
            if (!settingsSaved)
            {
                App.ApplyTheme(savedTheme);
                App.ApplyFontFamily(savedFontFamily);
            }
        };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    // ---------- 基础设施 ----------
}
