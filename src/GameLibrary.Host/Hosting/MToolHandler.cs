using System.Diagnostics;
using System.Text.Json;
using GameLibrary.Contracts;
using GameLibrary.Contracts.Ipc;
using GameLibrary.Host.Launching;
using GameLibrary.Infrastructure.Persistence;
using GameLibrary.Infrastructure.Tools;

namespace GameLibrary.Host.Hosting;

/// <summary>短同步文件操作，复用 dispatcher 串行门/会话租约；无额外后台轮询或进程启动。</summary>
internal sealed class MToolHandler(HostRuntimeState host)
{
    private const string SettingsKey = "mtool.toolRoot";
    private const string StatePrefix = "mtool.game.";

    internal static bool HasUntranslatedTag(SqliteLibraryStore store, string gameId)
    {
        var settings = UnityTranslationPersistence.Read<UnityTranslationSettings>(store, UnityTranslationPersistence.SettingsKey);
        var tag = settings?.BoundTagId is { } id ? store.TryGetTag(id) : store.TryGetTagByName("user", "未翻译");
        return tag is { Kind: "user" } && store.ListGameTags(gameId).Any(item => item.Kind == "user" && item.Name == tag.Name);
    }

    public Envelope<object> Handle(IpcRequest request)
    {
        var store = host.Library.Store;
        if (store is null) return IpcRequests.InvalidArgument(request, "库未初始化");
        try
        {
            switch (request.OperationId)
            {
                case "mtool.settings.get":
                    return Ok(request, new { toolRoot = ToolRoot(store) });
                case "mtool.settings.set":
                    if (!IpcRequests.TryGetStringParameter(request, "toolRoot", out var path))
                        return IpcRequests.InvalidArgument(request, "缺少 toolRoot 参数");
                    path = MToolRecipeGenerator.ValidateToolRoot(path);
                    UnityTranslationPersistence.Write(store, SettingsKey, path);
                    return Ok(request, new { toolRoot = path });
                case "mtool.status":
                case "mtool.prepare":
                    if (!IpcRequests.TryGetStringParameter(request, "gameId", out var gameId)
                        || store.TryGetGame(gameId) is not { Membership: "active" })
                        return IpcRequests.NotFound(request, "游戏不存在或已移除");
                    if (request.OperationId == "mtool.status")
                        return Ok(request, UnityTranslationPersistence.Read<MToolPreparation>(store, StatePrefix + gameId)
                            ?? new("idle", "添加未翻译标签或点击生成配方后检查"));
                    IpcRequests.TryGetStringParameter(request, "profileId", out var profileId);
                    var result = Prepare(gameId, profileId.Length > 0 ? profileId : null);
                    return result.State == "blocked"
                        ? IpcRequests.Failure(request, ErrorCodes.TranslationRouteUnavailable, result.Reason)
                        : Ok(request, result);
                default: return IpcRequests.InvalidArgument(request, "不支持的 MTool 操作");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { return IpcRequests.InvalidArgument(request, ex is InvalidDataException ? ex.Message : "无法安全完成 MTool 操作，请检查路径和权限"); }
    }

    public void RequestForTaggedGame(string gameId)
    {
        try
        {
            var store = host.Library.Store;
            if (store is null || host.MaintenanceMode || !HasUntranslatedTag(store, gameId)) return;
            Prepare(gameId);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Data.Common.DbException)
        { /* 标签写入已成功；用户可在详情重试，自动化失败不能撤销标签。 */ }
    }

    public MToolPreparation Prepare(string gameId, string? profileId = null)
    {
        var store = host.Library.Store;
        if (store is null || host.MaintenanceMode) return new("blocked", "库未就绪，请稍后重试");
        var game = store.TryGetGame(gameId);
        if (game is not { Membership: "active" }) return new("blocked", "游戏不存在或已移除");
        if (string.Equals(game.Engine, "unity", StringComparison.OrdinalIgnoreCase)) return new("skipped", "Unity 游戏请使用 Unity 翻译插件配置");
        if (!TranslationLaunchRouteResolver.RequiresTranslation(game, HasUntranslatedTag(store, gameId)))
            return new("skipped", "游戏当前不需要翻译");
        MToolPreparation result;
        try
        {
            var explicitProfile = profileId is not null;
            profileId ??= host.Launches.RecommendedProfileId(gameId);
            var profile = profileId is null ? null : host.Launches.GetProfile(profileId);
            if (profile?.ValidationStatus is "discarded" or "deleted" or "verifying"
                || profile is null && host.Launches.ListProfiles(gameId).Count != 0)
                throw new InvalidDataException("请先自动识别或选择明确的游戏启动方式");
            if (profile?.GameId != gameId && (explicitProfile || profile is not null)) throw new InvalidDataException("请先自动识别或选择明确的游戏启动方式");
            if (profile?.ToolId is not null) return new("skipped", "已使用显式翻译工具启动方式");
            if (profile is { Arguments.Count: > 0 }) throw new InvalidDataException("带额外启动参数的游戏请在 MTool 中生成配方");
            var executable = profile?.ExecutablePath ?? game.EntryPath;
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidDataException("请先自动识别或选择明确的游戏启动方式");
            var root = Directory.Exists(game.RootPath) ? game.RootPath : Path.GetDirectoryName(game.RootPath)!;
            if (!host.Roots.Contains(root) || !host.Roots.Contains(executable)
                || !Path.GetFullPath(executable).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("游戏入口不在已注册的游戏目录内");
            var directory = Path.GetDirectoryName(executable)!;
            if (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) || Directory.Exists(Path.Combine(directory, Path.GetFileNameWithoutExtension(executable) + "_Data")))
                return new("skipped", "Unity 游戏请使用 Unity 翻译插件配置");
            var toolRoot = ToolRoot(store);
            // 可用旧配方不依赖 MTool 正在运行或全局设置；生成/修复时才要求安装路径。
            if (toolRoot is null && new MToolAdapter().Discover(root).Recipe is not { IsBroken: false })
                throw new InvalidDataException("请在设置中选择 MTool 目录，或先打开 MTool 以自动发现安装位置");
            result = MToolRecipeGenerator.Prepare(root, executable, toolRoot ?? root);
            if (toolRoot is not null && UnityTranslationPersistence.Read<string>(store, SettingsKey) != toolRoot)
                UnityTranslationPersistence.Write(store, SettingsKey, toolRoot);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException or InvalidOperationException)
        { result = new("blocked", ex is InvalidDataException ? ex.Message : "无法安全生成 MTool 配方，请检查游戏入口、路径和权限"); }
        UnityTranslationPersistence.Write(store, StatePrefix + gameId, result);
        host.Events.Publish("mtool.updated", "game:" + gameId, new { gameId, state = result.State, reason = result.Reason }, DateTime.UtcNow);
        return result;
    }

    private static string? ToolRoot(SqliteLibraryStore store)
    {
        if (UnityTranslationPersistence.Read<string>(store, SettingsKey) is { } saved)
        {
            try { return MToolRecipeGenerator.ValidateToolRoot(saved); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("MTool"))
        {
            using (process)
                try
                {
                    if (process.MainModule?.FileName is { } path) roots.Add(MToolRecipeGenerator.ValidateToolRoot(path));
                }
                catch (Exception ex) when (ex is InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return roots.Count == 1 ? roots.Single() : null;
    }

    private static Envelope<object> Ok(IpcRequest request, object data) => new()
    { RequestId = request.RequestId, Ok = true, Status = OperationStatus.Completed, Data = data };
}
