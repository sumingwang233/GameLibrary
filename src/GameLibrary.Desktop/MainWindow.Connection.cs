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



/// <summary>MainWindow 的 Connection 关注点（阶段三结构拆分，partial；MVVM 化前置步骤）。</summary>
public partial class MainWindow : Window
{
    // ---------- 连接与初始化 ----------

    private async Task ConnectAsync()
    {
        try
        {
            SetStatus("正在启动后台服务…");
            ShowError(null);
            await DisposeConnectionAsync();
            _connection = await HostProcessLauncher.EnsureStartedAsync(
                App.ResolvedDataDirectory, clientName: "desktop", timeout: TimeSpan.FromSeconds(10));

            var status = await InvokeAsync("host.status");
            var initialized = status.Data.GetProperty("libraryInitialized").GetBoolean();
            if (!initialized)
            {
                // 审查意见 #2："初始化库"应自动完成，不出现在主界面按钮里。
                SetStatus("正在准备游戏库…");
                var init = await InvokeAsync("library.init", new { idempotencyKey = "desktop-auto-init" });
                if (!init.Ok)
                {
                    ShowError($"准备游戏库失败：{init.Error?.Message}");
                }
            }

            await LoadSettingsAsync();
            await RefreshAsync();
            await MaybeShowFirstUseGuideAsync();
            _eventCursor = 0;
            _eventTimer.Start();
        }
        catch (Exception ex)
        {
            SetStatus("暂时不可用");
            ShowError($"后台服务暂时不可用：{ex.Message}");
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var envelope = await InvokeAsync("settings.get");
            if (envelope.Ok)
            {
                _settings = envelope.Data.Clone();
                var theme = TryGetSetting("theme", out var t) ? t.GetString() : null;
                App.ApplyTheme(theme ?? "dark");
                App.ApplyFontFamily(TryGetSetting("uiFontFamily", out var f)
                    ? f.GetString() ?? "Segoe UI" : "Segoe UI");
                ApplyFontScale(TryGetSetting("uiFontScale", out var s) ? s.GetDouble() : 1.0);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HostClientException)
        {
            // 设置读取失败走默认主题，不阻塞主流程。
        }
    }

    private void ApplyFontScale(double scale)
    {
        var clamped = Math.Clamp(scale, 0.85, 1.6);
        if (Content is not FrameworkElement contentRoot)
        {
            return;
        }

        contentRoot.LayoutTransform = Math.Abs(clamped - 1.0) < 0.005
            ? Transform.Identity
            : new ScaleTransform(clamped, clamped);
    }

    // ---------- 数据刷新 ----------

    private async Task RefreshAsync()
    {
        if (_connection is null)
        {
            return;
        }

        var version = ++_refreshVersion;
        try
        {
            var hostStatus = await InvokeAsync("host.status");
            if (!hostStatus.Ok)
            {
                throw new InvalidOperationException($"读取后台状态失败：{hostStatus.Error?.Message}");
            }

            var tags = await InvokeAsync("tags.list");
            if (!tags.Ok)
            {
                throw new InvalidOperationException($"读取收藏夹失败：{tags.Error?.Message}");
            }

            if (version != _refreshVersion)
            {
                return;
            }

            UpdateCollectionViews(tags.Data);

            // 阶段三：搜索下沉数据库侧——games.list 直接携带 search（服务端 LIKE + 分页）。
            var listParameters = BuildGameQuery(offset: 0);
            var games = await InvokeAsync("games.list", listParameters);
            if (!games.Ok)
            {
                throw new InvalidOperationException($"读取游戏失败：{games.Error?.Code} {games.Error?.Message}");
            }

            var candidates = await InvokeAsync("candidates.list");
            if (!candidates.Ok)
            {
                throw new InvalidOperationException($"读取待确认项目失败：{candidates.Error?.Code} {candidates.Error?.Message}");
            }

            if (version != _refreshVersion)
            {
                return;
            }

            _loadedGamesTotal = games.Data.GetProperty("total").GetInt32();
            _loadedGamesCount = games.Data.GetProperty("items").GetArrayLength();
            RenderSidebar(games, candidates);
            UpdateLoadMoreButton();

            var gameCount = games.Data.GetProperty("total").GetInt32();
            var candidateCount = candidates.Data.GetProperty("total").GetInt32();
            var countLabel = SelectedViewId is "all" or "pending" ? "游戏" : "当前视图";
            SetStatus($"已就绪 · {countLabel} {gameCount} · 待确认 {candidateCount}");
        }
        catch (Exception ex)
        {
            if (version == _refreshVersion)
            {
                ShowError($"刷新失败：{ex.Message}");
            }
        }
    }

    private Dictionary<string, object> BuildGameQuery(int offset)
    {
        var parameters = new Dictionary<string, object> { ["limit"] = 500, ["offset"] = offset };
        if (_searchText.Length > 0)
        {
            parameters["search"] = _searchText;
        }

        if (SelectedViewId == "favorites")
        {
            parameters["favorite"] = true;
        }
        else if (SelectedViewId.StartsWith("tag:", StringComparison.Ordinal))
        {
            parameters["tagId"] = SelectedViewId[4..];
        }

        return parameters;
    }

    private async Task PollEventsAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            var envelope = await InvokeAsync("events.read", new { cursor = _eventCursor, limit = 50 });
            if (!envelope.Ok)
            {
                if (envelope.Error?.Code == ErrorCodes.CursorExpired)
                {
                    _eventCursor = 0;
                }

                return;
            }

            var next = envelope.Data.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.Number
                ? nc.GetInt64()
                : _eventCursor;
            var items = envelope.Data.GetProperty("items");
            if (items.GetArrayLength() > 0 && next != _eventCursor)
            {
                _eventCursor = next;
                await RefreshAsync();
            }
            else if (next != _eventCursor)
            {
                _eventCursor = next;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HostClientException)
        {
            // 轮询失败静默：连接恢复后继续。
        }
    }

    // ---------- 基础设施 ----------

    private async Task<Envelope<JsonElement>> InvokeAsync(string operationId, object? parameters = null)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("后台服务尚未就绪");
        }

        var json = JsonSerializer.Serialize(parameters ?? new { });
        var document = JsonDocument.Parse(json);
        var request = new IpcRequest
        {
            RequestId = $"desktop-{Guid.NewGuid():N}",
            OperationId = operationId,
            Parameters = document.RootElement.Clone(),
        };

        // 携带库实例/纪元（审查修复：变更请求可被恢复语义拒绝）。
        try
        {
            request.LibraryInstanceId = _connection.Handshake.LibraryInstanceId;
            request.ExpectedDataEpoch = _connection.Handshake.DataEpoch;
        }
        catch (InvalidOperationException)
        {
            // 未完成握手时按普通请求发送。
        }

        var envelope = await _connection.InvokeAsync(request, CancellationToken.None);
        if (!envelope.Ok && envelope.Error?.Code is ErrorCodes.DataEpochMismatch or ErrorCodes.LibraryInstanceMismatch)
        {
            // 纪元更换：立即重连并重试一次（新握手携带新纪元）。
            await _connection.ReconnectAsync(App.ResolvedDataDirectory, "desktop", CancellationToken.None);
            var retry = new IpcRequest
            {
                RequestId = $"desktop-{Guid.NewGuid():N}",
                OperationId = operationId,
                Parameters = document.RootElement.Clone(),
                LibraryInstanceId = _connection.Handshake.LibraryInstanceId,
                ExpectedDataEpoch = _connection.Handshake.DataEpoch,
            };
            envelope = await _connection.InvokeAsync(retry, CancellationToken.None);
        }

        return envelope;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowError(string? message) => ErrorText.Text = message ?? "";

    private static T? TryFindResource<T>(string key) where T : class =>
        System.Windows.Application.Current.TryFindResource(key) as T;

    private async Task DisposeConnectionAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
