using System.Diagnostics;
using System.Globalization;
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



/// <summary>MainWindow 的 Library 关注点（阶段三结构拆分，partial；MVVM 化前置步骤）。</summary>
public partial class MainWindow : Window
{
    private void RenderSidebar(Envelope<JsonElement> games, Envelope<JsonElement> candidates)
    {
        var entries = new List<Entry>();

        foreach (var game in games.Data.GetProperty("items").EnumerateArray())
        {
            entries.Add(GameEntry(game));
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
                string.IsNullOrEmpty(relativePath) ? "(游戏库)" : relativePath!,
                "待审核",
                candidate.GetProperty("physicalPath").GetString() ?? "",
                false,
                candidate));
        }

        _selectedCandidateIds.IntersectWith(pending
            .Select(candidate => candidate.GetProperty("candidateId").GetString())
            .OfType<string>());

        _allEntries = entries;
        RenderSidebar();
    }

    private static Entry GameEntry(JsonElement game) => new(
        "game",
        game.GetProperty("gameId").GetString() ?? "",
        game.GetProperty("title").GetString() ?? "(未命名)",
        game.GetProperty("engine").GetString() ?? "未识别",
        game.GetProperty("rootPath").GetString() ?? "",
        game.GetProperty("favorite").GetBoolean(),
        game.Clone());

    private void UpdateLoadMoreButton()
    {
        var remaining = _loadedGamesTotal - _loadedGamesCount;
        LoadMoreButton.Visibility = remaining > 0 && SelectedViewId != "pending"
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoadMoreButton.Content = $"加载更多游戏（已显示 {_loadedGamesCount}/{_loadedGamesTotal}）";
    }

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        if (_loadingMoreGames || _loadedGamesCount >= _loadedGamesTotal)
        {
            return;
        }

        _loadingMoreGames = true;
        LoadMoreButton.IsEnabled = false;
        var version = _refreshVersion;
        try
        {
            var page = await InvokeAsync("games.list", BuildGameQuery(_loadedGamesCount));
            if (!page.Ok)
            {
                ShowError($"加载更多游戏失败：{page.Error?.Message}");
                return;
            }

            if (version != _refreshVersion)
            {
                return;
            }

            var newEntries = page.Data.GetProperty("items").EnumerateArray().Select(GameEntry).ToList();
            var insertAt = _allEntries.FindIndex(entry => entry.Kind == "candidate");
            _allEntries.InsertRange(insertAt < 0 ? _allEntries.Count : insertAt, newEntries);
            _loadedGamesCount += newEntries.Count;
            _loadedGamesTotal = page.Data.GetProperty("total").GetInt32();
            if (newEntries.Count == 0)
            {
                _loadedGamesCount = _loadedGamesTotal;
            }

            RenderSidebar();
            UpdateLoadMoreButton();
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError($"加载更多游戏失败：{ex.Message}");
        }
        finally
        {
            _loadingMoreGames = false;
            LoadMoreButton.IsEnabled = true;
        }
    }

    /// <summary>应用当前视图与搜索文本渲染侧边栏（T15：搜索防抖后调用）。</summary>
    private void RenderSidebar()
    {
        var previousEntry = LibraryList.SelectedItem is ListBoxItem previousItem
            ? previousItem.Tag as Entry
            : null;
        var activeView = ViewSelector.SelectedItem is ComboBoxItem item ? (string)item.Tag : "all";
        IEnumerable<Entry> visible = _allEntries;
        // 审查意见：原实现只处理 favorites 分支，"待审核候选"仍显示所有项目。
        if (activeView == "favorites")
        {
            visible = visible.Where(e => e.Kind == "game" && e.Favorite);
        }
        else if (activeView == "pending")
        {
            visible = visible.Where(e => e.Kind == "candidate");
        }
        else
        {
            visible = visible.Where(e => e.Kind == "game");
        }
        // 搜索已由服务端执行（阶段三：数据库侧检索）；此处仅对本地候选条目做即时过滤。
        if (_searchText.Length > 0)
        {
            visible = visible.Where(e => e.Kind != "candidate"
                || e.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }

        var list = visible.ToList();
        UpdateCandidateBatchUi(activeView, list);
        UpdateGameBatchUi(activeView, list);
        _renderingSidebar = true;
        try
        {
            LibraryList.Items.Clear();
            foreach (var entry in list)
            {
                // 条目直接携带 Entry（按 ID 关联）；复选框负责批量选择，列表单选负责右侧详情。
                var container = new ListBoxItem
                {
                    Style = (Style)TryFindResource("SidebarItem"),
                    Tag = entry,
                    Content = CreateSidebarEntryContent(entry, showSelection: true),
                };
                LibraryList.Items.Add(container);
            }

            if (LibraryList.Items.Count == 0)
            {
                if (activeView == "all" && _searchText.Length == 0)
                {
                    LibraryList.Items.Add(new TextBlock
                    {
                        Text = "游戏库是空的。先点顶部「添加游戏库」，再点「扫描」；扫描结果会出现在「待确认游戏」里。",
                        FontSize = 11,
                        Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(10, 6, 10, 6),
                    });
                }
                else
                {
                    LibraryList.Items.Add(new TextBlock
                    {
                        Text = _searchText.Length > 0
                            ? $"没有匹配「{_searchText}」的条目。"
                            : "此视图暂无条目。",
                        FontSize = 11,
                        Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(10, 6, 10, 6),
                    });
                }

                LibraryList.SelectedIndex = -1;
                DetailPanel.Children.Clear();
                return;
            }

            var preferredIndex = _preferredGameId is null
                ? -1
                : LibraryList.Items.OfType<ListBoxItem>()
                    .Select((item, index) => (item, index))
                    .Where(pair => pair.item.Tag is Entry entry
                        && entry.Kind == "game" && entry.Id == _preferredGameId)
                    .Select(pair => pair.index)
                    .DefaultIfEmpty(-1)
                    .First();
            var matchingIndex = preferredIndex >= 0
                ? preferredIndex
                : previousEntry is null
                    ? -1
                    : LibraryList.Items.OfType<ListBoxItem>()
                        .Select((item, index) => (item, index))
                        .Where(pair => pair.item.Tag is Entry entry
                            && entry.Kind == previousEntry.Kind && entry.Id == previousEntry.Id)
                        .Select(pair => pair.index)
                        .DefaultIfEmpty(-1)
                        .First();
            if (preferredIndex >= 0)
            {
                _preferredGameId = null;
            }

            LibraryList.SelectedIndex = matchingIndex >= 0 ? matchingIndex : 0;
        }
        finally
        {
            _renderingSidebar = false;
        }

        if (LibraryList.SelectedItem is ListBoxItem selected && selected.Tag is Entry selectedEntry)
        {
            RenderDetail(selectedEntry);
        }
        else
        {
            RenderDetailPlaceholder();
        }
    }

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingSidebar)
        {
            return;
        }

        if (LibraryList.SelectedItem is ListBoxItem container && container.Tag is Entry entry)
        {
            RenderDetail(entry);
            return;
        }

        RenderDetailPlaceholder();
    }

    // ---------- 详情渲染 ----------

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
            Text = entry.Title.Length == 0 ? "(游戏库)" : entry.Title,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = TryFindResource<SolidColorBrush>("TextPrimary"),
        });
        DetailPanel.Children.Add(new TextBlock
        {
            Text = $"等待确认 · {CandidateKindLabel(candidate.GetProperty("kind").GetString())}",
            FontSize = 13,
            Foreground = TryFindResource<SolidColorBrush>("Green"),
            Margin = new Thickness(0, 4, 0, 12),
        });
        DetailPanel.Children.Add(MetaLine("路径", entry.PhysicalPath));
        DetailPanel.Children.Add(new TextBlock
        {
            Text = "加入后会出现在游戏库中；选择“不再提示”后，此位置不会再次出现在扫描结果里。",
            FontSize = 12,
            Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
            Margin = new Thickness(0, 10, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        });

        var revision = candidate.GetProperty("revision").GetInt32();
        DetailPanel.Children.Add(ButtonRow(
            ("加入游戏库", () => ReviewAsync(entry.Id, revision, "candidates.accept"), "SteamGreenButton"),
            ("稍后处理", () => ReviewAsync(entry.Id, revision, "candidates.defer"), "SteamButton"),
            ("不再提示", () => ReviewAsync(entry.Id, revision, "candidates.ignore"), "SteamButton")));
    }

    /// <summary>游戏详情：封面 + 标题 + 启动区 + 收藏/编辑 + meta + 操作按钮排（审查意见：能从 GUI 启动游戏）。</summary>
    private void RenderGameDetail(Entry entry)
    {
        var raw = entry.Raw;
        var gameId = entry.Id;
        var revision = raw.GetProperty("revision").GetInt32();
        var title = raw.GetProperty("title").GetString() ?? "";
        var summary = raw.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : "";
        var coverAssetId = raw.TryGetProperty("coverAssetId", out var cai) && cai.ValueKind == JsonValueKind.String ? cai.GetString() : null;

        // 启动区：状态行 + 开始游戏 + 配置启动方式。
        var launchStatus = new TextBlock
        {
            Text = "启动方式：加载中…",
            FontSize = 13,
            Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var playButton = new Button
        {
            Content = "▶ 开始游戏",
            Style = (Style)TryFindResource("SteamGreenButton"),
            FontSize = 15,
            Padding = new Thickness(24, 9, 24, 9),
            MinWidth = 150,
        };
        var configureLaunchButton = new Button
        {
            Content = "配置启动方式",
            Style = (Style)TryFindResource("SteamButton"),
            Margin = new Thickness(10, 0, 0, 0),
        };
        var launchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        launchRow.Children.Add(playButton);
        launchRow.Children.Add(configureLaunchButton);
        DetailPanel.Children.Add(launchStatus);
        DetailPanel.Children.Add(launchRow);
        playButton.Click += async (_, _) => await PlayGameAsync(gameId, playButton);
        configureLaunchButton.Click += async (_, _) => await ConfigureLaunchAsync(gameId, entry.PhysicalPath, launchStatus, playButton);
        _ = LoadLaunchStatusAsync(gameId, launchStatus, playButton);

        // 封面头图（LRU 缓存 + 可取消加载）。
        if (coverAssetId is not null)
        {
            var cover = new Image
            {
                Width = 340,
                MaxHeight = 420,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 14, 0, 12),
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
                Margin = new Thickness(0, 14, 0, 12),
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
            Text = GameKindSummary(
                raw.GetProperty("kind").GetString(),
                raw.GetProperty("engine").GetString()),
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
        DetailPanel.Children.Add(MetaLine("入库时间", FormatLocalTimestamp(raw.GetProperty("acceptedUtc").GetString())));
        if (raw.TryGetProperty("updatedUtc", out var updatedUtc))
        {
            DetailPanel.Children.Add(MetaLine("修改时间", FormatLocalTimestamp(updatedUtc.GetString())));
        }
        var tagText = raw.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
            ? string.Join("；", tagsElement.EnumerateArray()
                .Select(t => (t.GetProperty("kind").GetString() == "engine" ? "[自动] " : "") + t.GetProperty("name").GetString()))
            : "";
        DetailPanel.Children.Add(MetaLine("标签", tagText.Length > 0 ? tagText : "（无）"));
        DetailPanel.Children.Add(ButtonRow(
            ("设置标签", () => EditGameCollectionsAsync(gameId, revision, raw), "SteamButton")));

        // 翻译策略显示与循环切换（Auto→Required→NotRequired）。
        var translationText = new TextBlock
        {
            Text = "翻译策略：加载中…",
            FontSize = 13,
            Foreground = TryFindResource<SolidColorBrush>("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var translationButton = new Button
        {
            Content = "切换策略",
            Style = (Style)TryFindResource("SteamButton"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var translationRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        translationRow.Children.Add(translationText);
        translationRow.Children.Add(translationButton);
        DetailPanel.Children.Add(translationRow);
        _ = LoadTranslationAsync(gameId, translationText);
        translationButton.Click += async (_, _) =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(translationText.Text, @"策略：(\w+)");
            var current = match.Success ? match.Groups[1].Value : "Auto";
            await CycleTranslationAsync(gameId, current, revision);
        };

        DetailPanel.Children.Add(ButtonRow(
            ("导入封面", () => ImportCoverAsync(gameId), "SteamButton"),
            ("打开目录", () => OpenDirectoryAsync(entry.PhysicalPath), "SteamButton")));
        var removeButton = new Button
        {
            Content = "从库中移除（不删除文件）",
            Style = (Style)TryFindResource("SteamButton"),
            Foreground = TryFindResource<SolidColorBrush>("Danger"),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        removeButton.Click += async (_, _) => await RemoveGameAsync(gameId, revision, title);
        DetailPanel.Children.Add(removeButton);
    }

    private async Task RemoveGameAsync(string gameId, int revision, string title)
    {
        var answer = MessageBox.Show(this,
            $"从游戏库中移除「{title}」？\n\n这不会删除或移动游戏文件；程序也会记住此位置，避免下次扫描自动加回来。",
            "确认从库中移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var removed = await InvokeAsync("games.remove", new
            {
                idempotencyKey = $"ui-gameremove-{Guid.NewGuid():N}",
                gameId,
                expectedRevision = revision,
            });
            if (!removed.Ok)
            {
                ShowError($"移除游戏失败：{removed.Error?.Message}");
                return;
            }

            await RefreshAsync();
            ShowError(null);
            SetStatus("游戏已从库中移除；磁盘文件未删除");
        }
        catch (Exception ex)
        {
            ShowError($"移除游戏失败：{ex.Message}");
        }
    }

    /// <summary>启动方式状态加载：默认 Profile 存在与否决定"开始游戏"是否可用。</summary>
    private async Task LoadLaunchStatusAsync(string gameId, TextBlock statusText, Button playButton)
    {
        try
        {
            var envelope = await InvokeAsync("profiles.list", new { gameId });
            if (!envelope.Ok)
            {
                statusText.Text = "启动方式：未配置（点「配置启动方式」选择游戏主程序）";
                playButton.IsEnabled = false;
                return;
            }

            var items = envelope.Data.GetProperty("items");
            string? defaultProfileId = null;
            string? defaultExe = null;
            var count = 0;
            foreach (var p in items.EnumerateArray())
            {
                count++;
                if (p.GetProperty("isDefault").GetBoolean())
                {
                    defaultProfileId = p.GetProperty("profileId").GetString() ?? "";
                    defaultExe = p.GetProperty("executablePath").GetString() ?? "";
                }
            }

            if (defaultProfileId is null)
            {
                statusText.Text = count > 0
                    ? "启动方式：未设置默认；点「配置启动方式」选择主程序并设为默认"
                    : "启动方式：未配置（点「配置启动方式」选择游戏主程序）";
                playButton.IsEnabled = false;
                playButton.Tag = null;
                return;
            }

            statusText.Text = $"启动方式：已就绪（{Path.GetFileName(defaultExe)}）";
            playButton.IsEnabled = true;
            playButton.Tag = defaultProfileId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HostClientException or KeyNotFoundException)
        {
            statusText.Text = "启动方式：状态未知（后台暂不可用）";
        }
    }

    private sealed record LaunchEntry(string ProfileId, string ExecutablePath);

    private async Task PlayGameAsync(string gameId, Button playButton)
    {
        var profileId = playButton.Tag as string;
        if (string.IsNullOrEmpty(profileId))
        {
            ShowError("尚未配置启动方式。");
            return;
        }

        try
        {
            var envelope = await InvokeAsync("launch.execute", new
            {
                idempotencyKey = $"ui-play-{Guid.NewGuid():N}",
                profileId,
            });
            if (!envelope.Ok)
            {
                ShowError($"启动失败：{envelope.Error?.Code} {envelope.Error?.Message}");
                return;
            }

            var state = envelope.Data.TryGetProperty("state", out var s) ? s.GetString() : null;
            SetStatus(state == "processCreated" ? "游戏已启动" : $"启动状态：{state}");
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError($"启动失败：{ex.Message}");
        }
    }

    /// <summary>配置启动方式：图形化选择 EXE 或 SWF → profiles.create（首个自动设默认）。</summary>
    private async Task ConfigureLaunchAsync(string gameId, string gameRootPath, TextBlock statusText, Button playButton)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择游戏启动文件（EXE / SWF）",
            // LNK 在手动入库时解析为真实目标；Profile 本身只保存可验证的 EXE/SWF。
            Filter = "游戏启动文件|*.exe;*.swf|Windows 程序|*.exe|Flash 游戏|*.swf",
            CheckFileExists = true,
        };
        if (Directory.Exists(gameRootPath))
        {
            dialog.InitialDirectory = gameRootPath;
        }
        else if (File.Exists(gameRootPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(gameRootPath);
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var launchPath = dialog.FileName;
        var cwd = Path.GetDirectoryName(launchPath) ?? gameRootPath;
        try
        {
            var listEnvelope = await InvokeAsync("profiles.list", new { gameId });
            var hasDefault = false;
            if (listEnvelope.Ok)
            {
                foreach (var p in listEnvelope.Data.GetProperty("items").EnumerateArray())
                {
                    if (p.GetProperty("isDefault").GetBoolean())
                    {
                        hasDefault = true;
                        break;
                    }
                }
            }

            var create = await InvokeAsync("profiles.create", new
            {
                idempotencyKey = $"ui-profile-{Guid.NewGuid():N}",
                gameId,
                executablePath = launchPath,
                argv = Array.Empty<string>(),
                cwd,
                isDefault = !hasDefault,
            });
            if (!create.Ok)
            {
                ShowError($"保存启动方式失败：{create.Error?.Message}");
                return;
            }

            SetStatus("启动方式已保存");
            ShowError(null);
            await LoadLaunchStatusAsync(gameId, statusText, playButton);
        }
        catch (Exception ex)
        {
            ShowError($"保存启动方式失败：{ex.Message}");
        }
    }

    private async Task LoadTranslationAsync(string gameId, TextBlock target)
    {
        try
        {
            var envelope = await InvokeAsync("translation.get", new { gameId });
            if (!envelope.Ok)
            {
                return;
            }

            var data = envelope.Data;
            var inherited = data.GetProperty("inherited").GetString();
            var userOverride = data.GetProperty("userOverride").GetString();
            var effective = data.GetProperty("effective").GetString();
            var display = userOverride == "Auto" ? $"{effective}（继承 {inherited}）" : $"{effective}（用户覆盖）";
            target.Text = $"翻译策略：{display}";
        }
        catch (InvalidOperationException)
        {
            // 连接断开时详情页无需更新。
        }
    }

    private async Task CycleTranslationAsync(string gameId, string currentEffective, int expectedRevision)
    {
        var next = currentEffective switch
        {
            "Required" => "NotRequired",
            "NotRequired" => "Auto",
            _ => "Required",
        };
        var envelope = await InvokeAsync("translation.set", new
        {
            idempotencyKey = $"ui-trans-{Guid.NewGuid():N}",
            gameId,
            @override = next,
            expectedRevision,
        });
        if (!envelope.Ok)
        {
            ShowError($"translation.set 失败：{envelope.Error?.Code} {envelope.Error?.Message}");
            return;
        }

        await RefreshAsync();
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
        // 上下文引导：待审核有候选而"全部游戏"视图为空时，说明用户还没审核入库。
        var pendingCount = _allEntries.Count(e => e.Kind == "candidate");
        var gameCount = _allEntries.Count(e => e.Kind == "game");
        var text = pendingCount > 0 && gameCount == 0 && ViewSelector.SelectedItem is ComboBoxItem v && (string?)v.Tag == "all"
            ? $"扫描发现了 {pendingCount} 个项目需要确认。切到「待确认游戏」，确认后点「加入游戏库」。"
            : "选择左侧条目查看详情";
        DetailPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static string CandidateKindLabel(string? kind) => kind switch
    {
        "gameRoot" => "游戏库",
        "nestedCandidate" => "文件夹内的游戏",
        "container" => "包含多个游戏的文件夹",
        _ => "扫描发现的项目",
    };

    private static string GameKindSummary(string? kind, string? engine)
    {
        var source = kind switch
        {
            "manualFile" => "手动添加的游戏程序",
            "manualShortcut" => "手动添加的快捷方式",
            "manualDirectory" => "手动添加的游戏库",
            "nestedCandidate" => "从游戏库中发现",
            "container" => "包含多个游戏的文件夹",
            _ => "扫描发现的游戏",
        };
        return string.IsNullOrWhiteSpace(engine) ? source : $"{source} · 识别为 {engine}";
    }

    private static TextBlock MetaLine(string label, string value) => new()
    {
        Text = $"{label}：{value}",
        FontSize = 12,
        Margin = new Thickness(0, 3, 0, 3),
        TextTrimming = TextTrimming.CharacterEllipsis,
        ToolTip = value,
    };

    internal static string FormatLocalTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : value ?? "";

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

    private async void OnViewSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingViewSelector)
        {
            return;
        }

        RenderSidebar();
        if (ViewSelector.SelectedItem is ComboBoxItem item && _connection is not null)
        {
            var viewId = (string)item.Tag;
            await RefreshAsync();
            if (SelectedViewId != viewId)
            {
                return;
            }

            if (viewId is "all" or "favorites")
            {
                try
                {
                    // 每次切换是一次新操作；固定幂等键会把“切回原视图”误判为旧请求重放。
                    var activated = await InvokeAsync("views.activate", new
                    {
                        idempotencyKey = $"ui-viewact-{Guid.NewGuid():N}",
                        viewId,
                    });
                    if (!activated.Ok)
                    {
                        ShowError($"切换视图失败：{activated.Error?.Message}");
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"切换视图失败：{ex.Message}");
                }
            }
        }

    }

    private async void OnSortSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SortSelector.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var newSort = (string)selected.Tag;
        if (newSort == _sortMode)
        {
            return;
        }

        _sortMode = newSort;
        _selectedGameIds.Clear();
        await RefreshAsync();
    }

    private async Task ReviewAsync(string candidateId, int revision, string operationId)
    {
        var action = CandidateReviewActionLabel(operationId);
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
                ShowError($"{action}失败：{envelope.Error?.Message}");
            }
            else if (operationId == "candidates.accept")
            {
                var gameId = envelope.Data.TryGetProperty("gameId", out var gid) ? gid.GetString() : null;
                SetStatus(gameId is null
                    ? "已加入游戏库；切到「全部游戏」查看"
                    : "已加入游戏库；其余项目确认完后可在「全部游戏」中查看");
            }
        }
        catch (Exception ex)
        {
            ShowError($"{action}失败：{ex.Message}");
        }

        await RefreshAsync();
    }

    private async Task OpenDirectoryAsync(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
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

    // ---------- 顶栏操作 ----------

    /// <summary>添加游戏库（roots.add）：图形化选择目录；与数据目录是两个不同概念/控件。</summary>
}
