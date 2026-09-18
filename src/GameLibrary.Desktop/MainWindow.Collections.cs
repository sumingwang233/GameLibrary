using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameLibrary.Desktop;

/// <summary>标签管理、筛选与游戏归类；用户标签可增删改，一款游戏可拥有多个标签。</summary>
public partial class MainWindow
{
    private sealed record CollectionItem(string TagId, string Name, int Revision, int GameCount);
    private sealed record TagFilterItem(string TagId, string Kind, string Name, int GameCount);

    private readonly List<TagFilterItem> _allTags = [];

    private string SelectedViewId => ViewSelector.SelectedItem is ComboBoxItem item
        ? (string)item.Tag
        : "all";

    private string SelectedTagId => TagFilterSelector.SelectedItem is ComboBoxItem item
        ? (string)item.Tag
        : "";

    private void UpdateCollectionViews(JsonElement data)
    {
        var selectedId = SelectedViewId;
        var selectedTagId = SelectedTagId;
        _userCollections.Clear();
        _allTags.Clear();
        foreach (var tag in data.GetProperty("items").EnumerateArray())
        {
            var kind = tag.GetProperty("kind").GetString() ?? "";
            var item = new TagFilterItem(
                tag.GetProperty("tagId").GetString() ?? "",
                kind,
                tag.GetProperty("name").GetString() ?? "",
                tag.GetProperty("gameCount").GetInt32());
            _allTags.Add(item);
            if (kind != "user")
            {
                continue;
            }

            _userCollections.Add(new CollectionItem(
                item.TagId,
                item.Name,
                tag.GetProperty("revision").GetInt32(),
                item.GameCount));
        }

        _updatingViewSelector = true;
        try
        {
            ViewSelector.Items.Clear();
            ViewSelector.Items.Add(new ComboBoxItem { Content = "全部游戏", Tag = "all" });
            ViewSelector.Items.Add(new ComboBoxItem { Content = "收藏", Tag = "favorites" });
            ViewSelector.Items.Add(new ComboBoxItem { Content = "待确认游戏", Tag = "pending" });
            ViewSelector.SelectedItem = ViewSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == selectedId)
                ?? ViewSelector.Items[0];
        }
        finally
        {
            _updatingViewSelector = false;
        }

        _updatingTagFilter = true;
        try
        {
            TagFilterSelector.Items.Clear();
            TagFilterSelector.Items.Add(new ComboBoxItem { Content = "全部标签", Tag = "" });
            foreach (var tag in _allTags.OrderBy(tag => tag.Kind).ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                TagFilterSelector.Items.Add(new ComboBoxItem
                {
                    Content = tag.Kind == "engine"
                        ? $"自动：{tag.Name} ({tag.GameCount})"
                        : $"{tag.Name} ({tag.GameCount})",
                    Tag = tag.TagId,
                });
            }

            TagFilterSelector.SelectedItem = TagFilterSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == selectedTagId)
                ?? TagFilterSelector.Items[0];
        }
        finally
        {
            _updatingTagFilter = false;
        }
    }

    private async void OnCreateCollectionClick(object sender, RoutedEventArgs e)
    {
        var name = PromptCollectionName("新建标签", "", this);
        if (name is null)
        {
            return;
        }

        try
        {
            var result = await InvokeAsync("tags.create", new
            {
                idempotencyKey = $"ui-tagcreate-{Guid.NewGuid():N}",
                name,
            });
            if (!result.Ok)
            {
                ShowError($"新建标签失败：{result.Error?.Message}");
                return;
            }

            var tagId = result.Data.GetProperty("tagId").GetString();
            await RefreshAsync();
            TagFilterSelector.SelectedItem = TagFilterSelector.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == tagId);
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError($"新建标签失败：{ex.Message}");
        }
    }

    private void OnManageCollectionsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "管理标签",
            Width = 520,
            Height = 340,
            Owner = this,
            FontFamily = FontFamily,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource<SolidColorBrush>("BgMain"),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = _userCollections.Count == 0
                ? "还没有自定义标签。可在左侧点击「新建标签」。"
                : "自动标签由扫描生成；这里可重命名或删除自定义标签，不会删除游戏。",
            Foreground = TryFindResource<SolidColorBrush>("TextMuted"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        foreach (var collection in _userCollections.ToArray())
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var deleteButton = new Button
            {
                Content = "删除",
                Style = (Style)TryFindResource("SteamButton"),
                Margin = new Thickness(6, 0, 0, 0),
            };
            var renameButton = new Button
            {
                Content = "重命名",
                Style = (Style)TryFindResource("SteamButton"),
            };
            DockPanel.SetDock(deleteButton, Dock.Right);
            DockPanel.SetDock(renameButton, Dock.Right);
            row.Children.Add(deleteButton);
            row.Children.Add(renameButton);
            row.Children.Add(new TextBlock
            {
                Text = $"{collection.Name}（{collection.GameCount} 款游戏）",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = TryFindResource<SolidColorBrush>("TextBody"),
            });
            panel.Children.Add(row);

            renameButton.Click += async (_, _) =>
            {
                var name = PromptCollectionName("重命名标签", collection.Name, dialog);
                if (name is null || name == collection.Name)
                {
                    return;
                }

                await ChangeCollectionAsync("tags.update", collection, name, dialog);
            };
            deleteButton.Click += async (_, _) =>
            {
                if (MessageBox.Show(dialog,
                    $"删除标签「{collection.Name}」？游戏仍会留在库中。",
                    "确认删除标签", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                await ChangeCollectionAsync("tags.remove", collection, null, dialog);
            };
        }

        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    private async Task ChangeCollectionAsync(string operation, CollectionItem collection, string? name, Window dialog)
    {
        try
        {
            var patch = new Dictionary<string, object>
            {
                ["idempotencyKey"] = $"ui-tagchange-{Guid.NewGuid():N}",
                ["tagId"] = collection.TagId,
                ["expectedRevision"] = collection.Revision,
            };
            if (name is not null)
            {
                patch["name"] = name;
            }

            var result = await InvokeAsync(operation, patch);
            if (!result.Ok)
            {
                ShowError($"修改标签失败：{result.Error?.Message}");
                return;
            }

            dialog.Close();
            await RefreshAsync();
            ShowError(null);
        }
        catch (Exception ex)
        {
            ShowError($"修改标签失败：{ex.Message}");
        }
    }

    private async Task EditGameCollectionsAsync(string gameId, int revision, JsonElement raw)
    {
        if (_userCollections.Count == 0)
        {
            ShowError("尚无自定义标签。先点击左侧「新建标签」，再为游戏设置标签。");
            return;
        }

        var assigned = raw.GetProperty("tags").EnumerateArray()
            .Where(t => t.GetProperty("kind").GetString() == "user")
            .Select(t => t.GetProperty("name").GetString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dialog = new Window
        {
            Title = "设置标签",
            Width = 400,
            Height = 380,
            Owner = this,
            FontFamily = FontFamily,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource<SolidColorBrush>("BgMain"),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var choices = new List<(CollectionItem Collection, CheckBox CheckBox)>();
        foreach (var collection in _userCollections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var check = new CheckBox
            {
                Content = collection.Name,
                IsChecked = assigned.Contains(collection.Name),
                Foreground = TryFindResource<SolidColorBrush>("TextBody"),
                Margin = new Thickness(0, 4, 0, 4),
            };
            choices.Add((collection, check));
            panel.Children.Add(check);
        }

        var saveButton = new Button
        {
            Content = "保存",
            Style = (Style)TryFindResource("SteamGreenButton"),
            Margin = new Thickness(0, 16, 0, 0),
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(saveButton);
        saveButton.Click += async (_, _) =>
        {
            saveButton.IsEnabled = false;
            try
            {
                foreach (var (collection, check) in choices)
                {
                    var desired = check.IsChecked == true;
                    if (desired == assigned.Contains(collection.Name))
                    {
                        continue;
                    }

                    var result = await InvokeAsync(desired ? "tags.assign" : "tags.unassign", new
                    {
                        idempotencyKey = $"ui-tagmember-{Guid.NewGuid():N}",
                        gameId,
                        tagId = collection.TagId,
                        expectedRevision = revision,
                    });
                    if (!result.Ok)
                    {
                        ShowError($"保存标签失败：{result.Error?.Message}");
                        return;
                    }
                }

                dialog.Close();
                await RefreshAsync();
                ShowError(null);
            }
            catch (Exception ex)
            {
                ShowError($"保存标签失败：{ex.Message}");
            }
            finally
            {
                saveButton.IsEnabled = true;
            }
        };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
        await Task.CompletedTask;
    }

    private async void OnTagFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTagFilter || !IsLoaded)
        {
            return;
        }

        _selectedGameIds.Clear();
        await RefreshAsync();
    }

    private static string? PromptCollectionName(string title, string initial, Window owner)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 170,
            Owner = owner,
            FontFamily = owner.FontFamily,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Application.Current.TryFindResource("BgMain") as SolidColorBrush,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "标签名称", Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = initial, MaxLength = 100 };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "确定", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                input.Focus();
                return;
            }

            dialog.DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        input.SelectAll();
        input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
