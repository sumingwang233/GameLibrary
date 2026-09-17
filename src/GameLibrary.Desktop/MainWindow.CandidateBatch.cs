using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameLibrary.Desktop;

/// <summary>待确认游戏的多选状态与批量审核操作。</summary>
public partial class MainWindow
{
    private readonly HashSet<string> _selectedCandidateIds = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _visibleCandidateIds = [];
    private bool _batchReviewInProgress;
    private bool _updatingCandidateSelectionUi;

    private FrameworkElement CreateSidebarEntryContent(Entry entry, bool showCandidateSelection)
    {
        var text = new StackPanel();
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
            Foreground = entry.Kind == "candidate"
                ? TryFindResource<SolidColorBrush>("Green")
                : TryFindResource<SolidColorBrush>("TextMuted"),
        });

        if (!showCandidateSelection || entry.Kind != "candidate")
        {
            return text;
        }

        var checkBox = new CheckBox
        {
            IsChecked = _selectedCandidateIds.Contains(entry.Id),
            Tag = entry.Id,
            MinWidth = 24,
            MinHeight = 24,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"选择 {entry.Title}",
        };
        System.Windows.Automation.AutomationProperties.SetName(checkBox, $"选择 {entry.Title}");
        checkBox.Checked += OnCandidateSelectionChanged;
        checkBox.Unchecked += OnCandidateSelectionChanged;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(checkBox, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(checkBox);
        row.Children.Add(text);
        return row;
    }

    private void OnCandidateSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string candidateId } checkBox)
        {
            return;
        }

        if (checkBox.IsChecked == true)
        {
            _selectedCandidateIds.Add(candidateId);
        }
        else
        {
            _selectedCandidateIds.Remove(candidateId);
        }

        UpdateCandidateBatchActionState();
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

    private void UpdateCandidateBatchUi(string activeView, IReadOnlyList<Entry> visibleEntries)
    {
        var showBatch = string.Equals(activeView, "pending", StringComparison.Ordinal);
        CandidateBatchBar.Visibility = showBatch ? Visibility.Visible : Visibility.Collapsed;
        _visibleCandidateIds = showBatch
            ? visibleEntries.Where(entry => entry.Kind == "candidate").Select(entry => entry.Id).ToArray()
            : [];
        UpdateCandidateBatchActionState();
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

        var status = failures.Count == 0
            ? $"批量{action}完成：成功 {succeeded} 项"
            : $"批量{action}完成：成功 {succeeded} 项，失败 {failures.Count} 项";
        var error = failures.Count == 0
            ? null
            : $"批量{action}失败：{failures[0]}{(failures.Count > 1 ? $"；另有 {failures.Count - 1} 项失败" : "")}";
        await RefreshAsync();
        SetStatus(status);
        ShowError(error);
    }

    private static string CandidateReviewActionLabel(string operationId) => operationId switch
    {
        "candidates.accept" => "加入游戏库",
        "candidates.defer" => "暂不处理",
        "candidates.ignore" => "忽略",
        _ => "处理",
    };
}
