using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace GameLibrary.Desktop;

/// <summary>侧栏条目多选、待确认批量审核和已入库游戏批量操作。</summary>
public partial class MainWindow
{
    private readonly HashSet<string> _selectedCandidateIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedGameIds = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _visibleCandidateIds = [];
    private IReadOnlyList<string> _visibleGameIds = [];
    private bool _batchReviewInProgress;
    private bool _batchGameInProgress;
    private bool _updatingCandidateSelectionUi;
    private bool _updatingGameSelectionUi;

    // ---------- 侧栏封面卡片 ----------

    private const double CardCoverHeight = 160;
    private const double CardInfoBarHeight = 46;
    private static readonly TimeSpan CardHoverDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>封面优先卡片：封面占满卡面；悬停时卡面上浮 4px、封面放大、底部信息条滑入、
    /// 居中显示播放键；选中态（绿色竖条/行背景加深）由 SidebarItem 模板负责。</summary>
    private FrameworkElement CreateSidebarEntryContent(Entry entry, bool showSelection)
    {
        var isCandidate = entry.Kind == "candidate";
        var coverAssetId = !isCandidate
            && entry.Raw.TryGetProperty("coverAssetId", out var cai)
            && cai.ValueKind == JsonValueKind.String
                ? cai.GetString()
                : null;

        // 封面区：有封面异步加载（LRU 缓存，复用详情页 CoverCache）；无封面深色占位 + 标题。
        FrameworkElement coverVisual;
        Image? coverImage = null;
        if (coverAssetId is not null)
        {
            coverImage = new Image
            {
                Stretch = Stretch.UniformToFill,
                Tag = coverAssetId,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            coverVisual = coverImage;
        }
        else
        {
            coverVisual = new TextBlock
            {
                Text = entry.Title,
                FontSize = 13,
                Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(14, 0, 14, 0),
            };
        }

        var coverClip = new Border
        {
            Height = CardCoverHeight,
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x30, 0x3c)),
            Child = coverVisual,
        };

        // 底部信息条（默认隐藏在卡面下缘外，悬停滑入）：名称 + 最后游玩时间。
        var infoSlide = new TranslateTransform(0, CardInfoBarHeight);
        var infoText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoText.Children.Add(new TextBlock
        {
            Text = (entry.Favorite ? "★ " : "") + entry.Title,
            FontSize = 13,
            Foreground = TryFindResource<SolidColorBrush>("TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        infoText.Children.Add(new TextBlock
        {
            Text = isCandidate ? "等待确认" : LastPlayedLine(entry.Raw),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = isCandidate
                ? TryFindResource<SolidColorBrush>("Green")
                : TryFindResource<SolidColorBrush>("TextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var infoBar = new Border
        {
            Height = CardInfoBarHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0b, 0x0e, 0x14)),
            Padding = new Thickness(8, 4, 8, 4),
            RenderTransform = infoSlide,
            Child = infoText,
        };

        // 居中播放键（仅已入库游戏；候选不可启动）：悬停淡入，点击直接按默认启动方式启动。
        Border? playOverlay = null;
        if (!isCandidate)
        {
            playOverlay = new Border
            {
                Width = 54,
                Height = 54,
                CornerRadius = new CornerRadius(27),
                Background = new SolidColorBrush(Color.FromArgb(0xb3, 0, 0, 0)),
                BorderBrush = TryFindResource<SolidColorBrush>("Green"),
                BorderThickness = new Thickness(2),
                Opacity = 0,
                // 完全透明时不得拦截点击（悬停淡入时才可点）。
                IsHitTestVisible = false,
                Cursor = Cursors.Hand,
                ToolTip = $"启动 {entry.Title}",
                Tag = entry,
                Child = new TextBlock
                {
                    Text = "▶",
                    FontSize = 20,
                    Foreground = Brushes.White,
                    Margin = new Thickness(3, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            playOverlay.MouseLeftButtonUp += OnCardPlayClick;
            System.Windows.Automation.AutomationProperties.SetName(playOverlay, $"启动 {entry.Title}");
        }

        // 选中遮罩：卡片级"背景加深"，跟随宿主 ListBoxItem.IsSelected（不改动数据绑定）。
        var selectShade = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        selectShade.SetBinding(VisibilityProperty, new Binding(nameof(ListBoxItem.IsSelected))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1),
            Converter = new BooleanToVisibilityConverter(),
        });

        var layers = new Grid();
        layers.Children.Add(coverClip);
        layers.Children.Add(infoBar);
        if (playOverlay is not null)
        {
            layers.Children.Add(playOverlay);
        }

        layers.Children.Add(selectShade);

        if (showSelection)
        {
            var selected = isCandidate
                ? _selectedCandidateIds.Contains(entry.Id)
                : _selectedGameIds.Contains(entry.Id);
            var checkBox = new CheckBox
            {
                IsChecked = selected,
                Tag = entry,
                MinWidth = 24,
                MinHeight = 24,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"选择 {entry.Title}",
            };
            System.Windows.Automation.AutomationProperties.SetName(checkBox, $"选择 {entry.Title}");
            checkBox.Checked += OnEntrySelectionChanged;
            checkBox.Unchecked += OnEntrySelectionChanged;
            layers.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3),
                Margin = new Thickness(6, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = checkBox,
            });
        }

        var lift = new TranslateTransform();
        var card = new Border
        {
            ClipToBounds = true,
            RenderTransform = lift,
            Child = layers,
        };
        card.MouseEnter += (_, _) =>
        {
            AnimateDouble(lift, TranslateTransform.YProperty, -4);
            if (coverImage?.RenderTransform is ScaleTransform scale)
            {
                AnimateDouble(scale, ScaleTransform.ScaleXProperty, 1.06);
                AnimateDouble(scale, ScaleTransform.ScaleYProperty, 1.06);
            }

            AnimateDouble(infoSlide, TranslateTransform.YProperty, 0);
            if (playOverlay is not null)
            {
                playOverlay.IsHitTestVisible = true;
                AnimateDouble(playOverlay, OpacityProperty, 1);
            }
        };
        card.MouseLeave += (_, _) =>
        {
            AnimateDouble(lift, TranslateTransform.YProperty, 0);
            if (coverImage?.RenderTransform is ScaleTransform scale)
            {
                AnimateDouble(scale, ScaleTransform.ScaleXProperty, 1);
                AnimateDouble(scale, ScaleTransform.ScaleYProperty, 1);
            }

            AnimateDouble(infoSlide, TranslateTransform.YProperty, CardInfoBarHeight);
            if (playOverlay is not null)
            {
                playOverlay.IsHitTestVisible = false;
                AnimateDouble(playOverlay, OpacityProperty, 0);
            }
        };

        // 封面延迟到卡片真实呈现（虚拟化 realized）后才加载，避免渲染时全量请求。
        if (coverImage is not null && coverAssetId is not null)
        {
            var image = coverImage;
            card.Loaded += (_, _) => _ = LoadCardCoverAsync(coverAssetId, image);
        }

        return card;
    }

    /// <summary>游戏无"最后游玩时间"数据字段；预留 lastPlayedUtc，缺失时如实显示暂无记录。</summary>
    private static string LastPlayedLine(JsonElement game) =>
        game.TryGetProperty("lastPlayedUtc", out var lastPlayed) && lastPlayed.ValueKind == JsonValueKind.String
            ? $"最后游玩：{FormatLocalTimestamp(lastPlayed.GetString())}"
            : "最后游玩：暂无记录";

    private static void AnimateDouble(IAnimatable target, DependencyProperty property, double to) =>
        target.BeginAnimation(property, new DoubleAnimation(to, CardHoverDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    /// <summary>卡片封面加载：复用详情页 LRU 缓存；不占用详情页的取消令牌（选详情不应取消列表封面）。</summary>
    private async Task LoadCardCoverAsync(string assetId, Image image)
    {
        if (CoverCache.TryGetValue(assetId, out var cached))
        {
            image.Source = cached;
            return;
        }

        try
        {
            var asset = await InvokeAsync("assets.get", new { assetId });
            if (!asset.Ok || !ReferenceEquals(image.Tag, assetId))
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

            if (!ReferenceEquals(image.Tag, assetId))
            {
                return;
            }

            image.Source = bitmap;
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
            // 封面加载失败保留占位背景，不影响列表。
        }
    }

    /// <summary>卡片播放键：查默认启动方式并 launch.execute（与详情页"开始游戏"同一链路）。</summary>
    private async void OnCardPlayClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Entry entry } || entry.Kind != "game")
        {
            return;
        }

        e.Handled = true;
        try
        {
            var profiles = await InvokeAsync("profiles.list", new { gameId = entry.Id });
            string? defaultProfileId = null;
            if (profiles.Ok)
            {
                foreach (var profile in profiles.Data.GetProperty("items").EnumerateArray())
                {
                    if (profile.GetProperty("isDefault").GetBoolean())
                    {
                        defaultProfileId = profile.GetProperty("profileId").GetString();
                        break;
                    }
                }
            }

            if (defaultProfileId is null)
            {
                ShowError("尚未配置启动方式。先在右侧详情页点「配置启动方式」。");
                return;
            }

            var launched = await InvokeAsync("launch.execute", new
            {
                idempotencyKey = $"ui-play-{Guid.NewGuid():N}",
                profileId = defaultProfileId,
            });
            if (!launched.Ok)
            {
                ShowError($"启动失败：{launched.Error?.Code} {launched.Error?.Message}");
                return;
            }

            ShowError(null);
            SetStatus("游戏已启动");
        }
        catch (Exception ex)
        {
            ShowError($"启动失败：{ex.Message}");
        }
    }

    private void OnEntrySelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Entry entry } checkBox)
        {
            return;
        }

        var target = entry.Kind == "candidate" ? _selectedCandidateIds : _selectedGameIds;
        if (checkBox.IsChecked == true)
        {
            target.Add(entry.Id);
        }
        else
        {
            target.Remove(entry.Id);
        }

        UpdateCandidateBatchActionState();
        UpdateGameBatchActionState();
    }

    private void OnSelectAllCandidatesChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingCandidateSelectionUi)
        {
            return;
        }

        if (SelectAllCandidatesCheckBox.IsChecked == true)
        {
            _selectedCandidateIds.UnionWith(_visibleCandidateIds);
        }
        else
        {
            _selectedCandidateIds.ExceptWith(_visibleCandidateIds);
        }

        RenderSidebar();
    }

    private void OnSelectAllGamesChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingGameSelectionUi)
        {
            return;
        }

        if (SelectAllGamesCheckBox.IsChecked == true)
        {
            _selectedGameIds.UnionWith(_visibleGameIds);
        }
        else
        {
            _selectedGameIds.ExceptWith(_visibleGameIds);
        }

        RenderSidebar();
    }

    private void UpdateCandidateBatchUi(string activeView, IReadOnlyList<Entry> visibleEntries)
    {
        var showBatch = string.Equals(activeView, "pending", StringComparison.Ordinal);
        CandidateBatchBar.Visibility = showBatch ? Visibility.Visible : Visibility.Collapsed;
        _visibleCandidateIds = showBatch
            ? visibleEntries.Where(entry => entry.Kind == "candidate").Select(entry => entry.Id).ToArray()
            : [];
        _selectedCandidateIds.IntersectWith(_visibleCandidateIds);
        UpdateCandidateBatchActionState();
    }

    private void UpdateGameBatchUi(string activeView, IReadOnlyList<Entry> visibleEntries)
    {
        var showBatch = !string.Equals(activeView, "pending", StringComparison.Ordinal);
        GameBatchBar.Visibility = showBatch ? Visibility.Visible : Visibility.Collapsed;
        _visibleGameIds = showBatch
            ? visibleEntries.Where(entry => entry.Kind == "game").Select(entry => entry.Id).ToArray()
            : [];
        _selectedGameIds.IntersectWith(_visibleGameIds);
        UpdateGameBatchActionState();
    }

    private void UpdateCandidateBatchActionState()
    {
        var selectedCount = _selectedCandidateIds.Count;
        CandidateBatchSelectionText.Text = $"已选择 {selectedCount} 项";
        _updatingCandidateSelectionUi = true;
        try
        {
            SelectAllCandidatesCheckBox.IsChecked = _visibleCandidateIds.Count > 0
                && _visibleCandidateIds.All(_selectedCandidateIds.Contains);
        }
        finally
        {
            _updatingCandidateSelectionUi = false;
        }

        var canRun = selectedCount > 0 && !_batchReviewInProgress;
        SelectAllCandidatesCheckBox.IsEnabled = _visibleCandidateIds.Count > 0 && !_batchReviewInProgress;
        BatchAcceptButton.IsEnabled = canRun;
        BatchDeferButton.IsEnabled = canRun;
        BatchIgnoreButton.IsEnabled = canRun;
    }

    private void UpdateGameBatchActionState()
    {
        var selectedCount = _selectedGameIds.Count;
        GameBatchSelectionText.Text = $"已选择 {selectedCount} 项";
        _updatingGameSelectionUi = true;
        try
        {
            SelectAllGamesCheckBox.IsChecked = _visibleGameIds.Count > 0
                && _visibleGameIds.All(_selectedGameIds.Contains);
        }
        finally
        {
            _updatingGameSelectionUi = false;
        }

        var canRun = selectedCount > 0 && !_batchGameInProgress;
        SelectAllGamesCheckBox.IsEnabled = _visibleGameIds.Count > 0 && !_batchGameInProgress;
        BatchFavoriteButton.IsEnabled = canRun;
        BatchUnfavoriteButton.IsEnabled = canRun;
        BatchTagsButton.IsEnabled = canRun;
        BatchRemoveGamesButton.IsEnabled = canRun;
    }

    private async void OnBatchAcceptClick(object sender, RoutedEventArgs e) =>
        await ReviewSelectedCandidatesAsync("candidates.accept");

    private async void OnBatchDeferClick(object sender, RoutedEventArgs e) =>
        await ReviewSelectedCandidatesAsync("candidates.defer");

    private async void OnBatchIgnoreClick(object sender, RoutedEventArgs e) =>
        await ReviewSelectedCandidatesAsync("candidates.ignore");

    private async Task ReviewSelectedCandidatesAsync(string operationId)
    {
        if (_batchReviewInProgress)
        {
            return;
        }

        var selected = _allEntries
            .Where(entry => entry.Kind == "candidate" && _selectedCandidateIds.Contains(entry.Id))
            .ToArray();
        if (selected.Length == 0)
        {
            UpdateCandidateBatchActionState();
            return;
        }

        var action = CandidateReviewActionLabel(operationId);
        if (operationId == "candidates.ignore"
            && MessageBox.Show(
                this,
                $"确定忽略选中的 {selected.Length} 个扫描结果？以后扫描到这些位置时将不再提示，但不会删除任何文件。",
                "批量忽略扫描结果",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _batchReviewInProgress = true;
        UpdateCandidateBatchActionState();
        ShowError(null);
        var succeeded = 0;
        var failures = new List<string>();
        try
        {
            foreach (var entry in selected)
            {
                var revision = entry.Raw.GetProperty("revision").GetInt32();
                try
                {
                    var envelope = await InvokeAsync(operationId, new
                    {
                        idempotencyKey = $"ui-batch-{Guid.NewGuid():N}",
                        candidateId = entry.Id,
                        expectedRevision = revision,
                    });
                    if (envelope.Ok)
                    {
                        succeeded++;
                        _selectedCandidateIds.Remove(entry.Id);
                    }
                    else
                    {
                        failures.Add($"{entry.Title}：{envelope.Error?.Message ?? "未知错误"}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.Title}：{ex.Message}");
                }
            }
        }
        finally
        {
            _batchReviewInProgress = false;
        }

        await RefreshAsync();
        SetStatus(failures.Count == 0
            ? $"批量{action}完成：成功 {succeeded} 项"
            : $"批量{action}完成：成功 {succeeded} 项，失败 {failures.Count} 项");
        ShowError(FirstBatchError(action, failures));
    }

    private async void OnBatchFavoriteClick(object sender, RoutedEventArgs e) =>
        await SetSelectedGamesFavoriteAsync(true);

    private async void OnBatchUnfavoriteClick(object sender, RoutedEventArgs e) =>
        await SetSelectedGamesFavoriteAsync(false);

    private async Task SetSelectedGamesFavoriteAsync(bool favorite)
    {
        var selected = SelectedGameEntries();
        if (selected.Length == 0 || _batchGameInProgress)
        {
            return;
        }

        _batchGameInProgress = true;
        UpdateGameBatchActionState();
        var failures = new List<string>();
        var succeeded = 0;
        try
        {
            foreach (var entry in selected)
            {
                if (entry.Favorite == favorite)
                {
                    succeeded++;
                    continue;
                }

                var result = await InvokeAsync("games.update", new
                {
                    idempotencyKey = $"ui-batch-favorite-{Guid.NewGuid():N}",
                    gameId = entry.Id,
                    favorite,
                    expectedRevision = entry.Raw.GetProperty("revision").GetInt32(),
                });
                if (result.Ok)
                {
                    succeeded++;
                }
                else
                {
                    failures.Add($"{entry.Title}：{result.Error?.Message ?? "未知错误"}");
                }
            }
        }
        finally
        {
            _batchGameInProgress = false;
        }

        var action = favorite ? "收藏" : "取消收藏";
        await RefreshAsync();
        SetStatus($"批量{action}完成：成功 {succeeded} 项，失败 {failures.Count} 项");
        ShowError(FirstBatchError(action, failures));
    }

    private async void OnBatchRemoveGamesClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedGameEntries();
        if (selected.Length == 0 || _batchGameInProgress)
        {
            return;
        }

        if (MessageBox.Show(this,
            $"从游戏库中移出选中的 {selected.Length} 款游戏？\n\n不会删除或移动任何游戏文件。",
            "批量移出游戏库", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _batchGameInProgress = true;
        UpdateGameBatchActionState();
        var failures = new List<string>();
        var succeeded = 0;
        try
        {
            foreach (var entry in selected)
            {
                var result = await InvokeAsync("games.remove", new
                {
                    idempotencyKey = $"ui-batch-remove-{Guid.NewGuid():N}",
                    gameId = entry.Id,
                    expectedRevision = entry.Raw.GetProperty("revision").GetInt32(),
                });
                if (result.Ok)
                {
                    succeeded++;
                    _selectedGameIds.Remove(entry.Id);
                }
                else
                {
                    failures.Add($"{entry.Title}：{result.Error?.Message ?? "未知错误"}");
                }
            }
        }
        finally
        {
            _batchGameInProgress = false;
        }

        await RefreshAsync();
        SetStatus($"批量移出完成：成功 {succeeded} 项，失败 {failures.Count} 项；磁盘文件未删除");
        ShowError(FirstBatchError("移出", failures));
    }

    private async void OnBatchTagsClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedGameEntries();
        if (selected.Length == 0 || _batchGameInProgress)
        {
            return;
        }

        if (_userCollections.Count == 0)
        {
            ShowError("尚无自定义标签。请先点击左侧「新建标签」。");
            return;
        }

        var dialog = new Window
        {
            Title = $"批量设置标签（{selected.Length} 款游戏）",
            Width = 430,
            Height = 430,
            Owner = this,
            FontFamily = FontFamily,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource<SolidColorBrush>("BgMain"),
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "勾选表示全部添加，取消表示全部移除，方块状态表示保持每款游戏现状。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        var choices = new List<(CollectionItem Tag, CheckBox CheckBox)>();
        foreach (var tag in _userCollections.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var assignedCount = selected.Count(entry => HasUserTag(entry.Raw, tag.Name));
            var checkBox = new CheckBox
            {
                Content = tag.Name,
                IsThreeState = true,
                IsChecked = assignedCount == 0 ? false : assignedCount == selected.Length ? true : null,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            };
            choices.Add((tag, checkBox));
            panel.Children.Add(checkBox);
        }

        var save = new Button
        {
            Content = "应用",
            Style = (Style)TryFindResource("SteamGreenButton"),
            MinWidth = 100,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0),
        };
        panel.Children.Add(save);
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            _batchGameInProgress = true;
            UpdateGameBatchActionState();
            var failures = new List<string>();
            var changed = 0;
            try
            {
                foreach (var entry in selected)
                {
                    foreach (var (tag, checkBox) in choices)
                    {
                        if (checkBox.IsChecked is null)
                        {
                            continue;
                        }

                        var assigned = HasUserTag(entry.Raw, tag.Name);
                        var desired = checkBox.IsChecked == true;
                        if (assigned == desired)
                        {
                            continue;
                        }

                        var result = await InvokeAsync(desired ? "tags.assign" : "tags.unassign", new
                        {
                            idempotencyKey = $"ui-batch-tag-{Guid.NewGuid():N}",
                            gameId = entry.Id,
                            tagId = tag.TagId,
                            expectedRevision = entry.Raw.GetProperty("revision").GetInt32(),
                        });
                        if (result.Ok)
                        {
                            changed++;
                        }
                        else
                        {
                            failures.Add($"{entry.Title} / {tag.Name}：{result.Error?.Message ?? "未知错误"}");
                        }
                    }
                }
            }
            finally
            {
                _batchGameInProgress = false;
                UpdateGameBatchActionState();
                save.IsEnabled = true;
            }

            if (failures.Count == 0)
            {
                dialog.Close();
            }

            await RefreshAsync();
            SetStatus($"批量标签操作完成：变更 {changed} 项，失败 {failures.Count} 项");
            ShowError(FirstBatchError("设置标签", failures));
        };

        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
        await Task.CompletedTask;
    }

    private Entry[] SelectedGameEntries() => _allEntries
        .Where(entry => entry.Kind == "game" && _selectedGameIds.Contains(entry.Id))
        .ToArray();

    private static bool HasUserTag(JsonElement raw, string name) =>
        raw.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
        && tags.EnumerateArray().Any(tag => tag.GetProperty("kind").GetString() == "user"
            && string.Equals(tag.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));

    private static string? FirstBatchError(string action, IReadOnlyList<string> failures) =>
        failures.Count == 0
            ? null
            : $"批量{action}失败：{failures[0]}{(failures.Count > 1 ? $"；另有 {failures.Count - 1} 项失败" : "")}";

    private static string CandidateReviewActionLabel(string operationId) => operationId switch
    {
        "candidates.accept" => "加入游戏库",
        "candidates.defer" => "暂不处理",
        "candidates.ignore" => "忽略",
        _ => "处理",
    };
}
