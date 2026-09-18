using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

    private FrameworkElement CreateSidebarEntryContent(Entry entry, bool showSelection)
    {
        var text = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        text.Children.Add(new TextBlock
        {
            Text = (entry.Favorite ? "★ " : "") + entry.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = entry.Kind == "candidate" ? "等待确认" : entry.Subtitle,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = entry.Kind == "candidate"
                ? TryFindResource<SolidColorBrush>("Green")
                : TryFindResource<SolidColorBrush>("TextMuted"),
        });

        if (!showSelection)
        {
            return text;
        }

        var selected = entry.Kind == "candidate"
            ? _selectedCandidateIds.Contains(entry.Id)
            : _selectedGameIds.Contains(entry.Id);
        var checkBox = new CheckBox
        {
            IsChecked = selected,
            Tag = entry,
            MinWidth = 24,
            MinHeight = 24,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"选择 {entry.Title}",
        };
        System.Windows.Automation.AutomationProperties.SetName(checkBox, $"选择 {entry.Title}");
        checkBox.Checked += OnEntrySelectionChanged;
        checkBox.Unchecked += OnEntrySelectionChanged;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(checkBox, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(checkBox);
        row.Children.Add(text);
        return row;
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
