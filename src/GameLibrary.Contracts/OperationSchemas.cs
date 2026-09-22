using System.Text.Json;

namespace GameLibrary.Contracts;

/// <summary>参数 schema 描述项。</summary>
public sealed record ParamSpec(string Name, string Type, bool Required, string Description);

/// <summary>
/// 操作输入/输出 schema 注册表（v1 审查修复：schema.get 不再返回 null）。
/// 以紧凑参数表声明每个已实现操作的输入契约，程序化生成 JSON Schema（draft 2020-12 子集）；
/// 未登记的操作回退到通用对象 schema 并注明待补充——这是"机器可发现"的诚实降级，
/// 不再把占位 null 冒充 schema。
/// </summary>
public static class OperationSchemas
{
    private static readonly Dictionary<string, ParamSpec[]> InputSpecs = new(StringComparer.Ordinal)
    {
        ["capabilities.get"] = [],
        ["host.status"] = [],
        ["host.stop"] = [new("idempotencyKey", "string", true, "停机幂等键")],
        ["library.init"] = [new("idempotencyKey", "string", false, "建库幂等键；提供后登记收据")],
        // roots.add / scan.cancel 在 operations.v1.json 中 requiresIdempotencyKey=true，
        // 此前 InputSpecs 漏登记该参数，schema.get 会向 MCP 客户端少报一个必填项。
        ["roots.add"] =
        [
            new("root", "string", true, "库根绝对路径"),
            new("kind", "string", false, "根类型 library/manual（默认 library；manual 仅作路径包含边界，不参与扫描枚举与候选发现）"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["roots.remove"] =
        [
            new("rootId", "string", true, "库根 ID"),
            new("expectedRevision", "integer", true, "期望库根修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["roots.list"] =
        [
            new("includeManual", "boolean", false, "包含 manual 根（默认只返回 library 根；manual 根是手动添加游戏的包含边界）"),
        ],
        ["scan.start"] =
        [
            new("root", "string", true, "扫描根（须在已注册库根内）"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["scan.status"] = [new("jobId", "string", true, "作业 ID")],
        ["scan.cancel"] =
        [
            new("jobId", "string", true, "作业 ID"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["scan.coverage"] = [new("jobId", "string", true, "作业 ID")],
        ["scan.inspect"] = [new("path", "string", true, "待识别目录（须在已注册库根内）")],
        ["candidates.list"] =
        [
            new("jobId", "string", false, "按扫描作业 ID 过滤"),
            new("state", "string", false, "按审核状态过滤"),
            new("limit", "integer", false, "分页大小（1–1000）"),
            new("offset", "integer", false, "分页偏移"),
        ],
        ["candidates.get"] = [new("candidateId", "string", true, "候选 ID")],
        ["candidates.accept"] =
        [
            new("candidateId", "string", true, "候选 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["candidates.defer"] =
        [
            new("candidateId", "string", true, "候选 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["candidates.ignore"] =
        [
            new("candidateId", "string", true, "候选 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["games.list"] =
        [
            new("search", "string", false, "搜索词（命中用户标题或原文件夹名，数据库侧执行）"),
            new("favorite", "boolean", false, "仅收藏"),
            new("sort", "string", false, "title/title-asc、title-desc、recent/updated-desc 或 accepted-desc"),
            new("viewId", "string", false, "套用视图筛选/排序"),
            new("tagId", "string", false, "按标签过滤（与搜索 AND 组合）"),
            new("limit", "integer", false, "分页大小（1–1000）"),
            new("offset", "integer", false, "分页偏移"),
        ],
        ["games.get"] = [new("gameId", "string", true, "游戏 ID")],
        ["games.create"] =
        [
            new("sourcePath", "string", true, "已注册游戏库内的目录或独立 EXE/SWF/LNK 绝对路径"),
            new("title", "string", false, "卡片标题；省略时取文件或目录名"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["games.remove"] =
        [
            new("deleteFiles", "boolean", false, "同时将原文件移入回收站，必须明确确认路径"),
            new("confirmedPath", "string", false, "删除原文件时确认的完整游戏路径"),
            new("gameId", "string", true, "游戏 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["games.update"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("favorite", "boolean", false, "收藏标记"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["games.relink"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("newPath", "string", true, "新路径（仅改绑定，不移动文件）"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["fields.set"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("field", "string", true, "字段：title / summary"),
            new("value", "string", true, "字段值"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["fields.clear"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("field", "string", true, "字段名"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["fields.reset"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("field", "string", true, "字段名"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["assets.import"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("sourcePath", "string", true, "图片绝对路径（复制入应用目录）"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["assets.list"] = [new("gameId", "string", true, "游戏 ID")],
        ["assets.get"] = [new("assetId", "string", true, "资产 ID")],
        ["assets.choose"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("assetId", "string", true, "资产 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["assets.crop"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("assetId", "string", true, "资产 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["assets.reset"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["assets.remove"] =
        [
            new("assetId", "string", true, "资产 ID"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["metadata.preview"] = [new("gameId", "string", true, "游戏 ID")],
        ["metadata.refresh"] = [new("gameId", "string", true, "游戏 ID"), new("idempotencyKey", "string", true, "幂等键")],
        ["tags.list"] = [],
        ["tags.create"] =
        [
            new("name", "string", true, "标签名（1–100 字符）"),
            new("color", "string", false, "颜色 #RRGGBB（可选）"),
            new("category", "string", false, "分类 engine/gameplay/social/special（缺省按 kind 推断：user 标签为 special）"),
            new("sortOrder", "integer", false, "排序权重（缺省 0）"),
            new("starred", "boolean", false, "星标（缺省 false）"),
            new("displayName", "string", false, "显示名（缺省同 name）"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["tags.update"] =
        [
            new("tagId", "string", true, "标签 ID"),
            new("name", "string", false, "新名称（可选；engine 标签 name 是身份键不可改，改名用 displayName）"),
            new("color", "string", false, "新颜色 #RRGGBB（可选，engine 标签同样允许）"),
            new("category", "string", false, "新分类 engine/gameplay/social/special（可选，engine 标签同样允许）"),
            new("sortOrder", "integer", false, "新排序权重（可选）"),
            new("starred", "boolean", false, "新星标（可选）"),
            new("displayName", "string", false, "新显示名（可选；传 null 清除，回落 name）"),
            new("expectedRevision", "integer", true, "期望标签修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["tags.remove"] =
        [
            new("tagId", "string", true, "标签 ID"),
            new("expectedRevision", "integer", true, "期望标签修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["tags.assign"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("tagId", "string", true, "标签 ID"),
            new("expectedRevision", "integer", true, "期望游戏修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["tags.unassign"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("tagId", "string", true, "标签 ID"),
            new("expectedRevision", "integer", true, "期望游戏修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["tags.suppress"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("tagId", "string", true, "标签 ID"),
            new("expectedRevision", "integer", true, "期望游戏修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["tags.reset"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("tagId", "string", true, "标签 ID"),
            new("expectedRevision", "integer", true, "期望游戏修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["ignores.list"] = [],
        ["ignores.create"] =
        [
            new("scope", "string", true, "ExactPath / Subtree / ConfirmedIdentity"),
            new("path", "string", false, "路径类规则的路径"),
            new("gameId", "string", false, "身份类规则的游戏 ID"),
            new("reason", "string", false, "原因"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["ignores.remove"] =
        [
            new("ignoreId", "string", true, "忽略规则 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["profiles.create"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("executablePath", "string", true, "启动 EXE 或 SWF 的绝对路径"),
            new("argv", "array", true, "启动参数字符串数组"),
            new("cwd", "string", true, "工作目录"),
            new("isDefault", "boolean", false, "是否默认 Profile"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["profiles.list"] = [new("gameId", "string", false, "按游戏过滤")],
        ["profiles.get"] = [new("profileId", "string", true, "Profile ID")],
        ["profiles.update"] =
        [
            new("profileId", "string", true, "Profile ID"),
            new("executablePath", "string", true, "新的 EXE 或 SWF 路径"),
            new("argv", "array", true, "新启动参数字符串数组"),
            new("cwd", "string", true, "新工作目录"),
            new("expectedRevision", "integer", true, "期望 Profile 修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["profiles.set_default"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("profileId", "string", true, "Profile ID"),
            new("expectedRevision", "integer", true, "期望 Profile 修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["profiles.remove"] =
        [
            new("profileId", "string", true, "Profile ID"),
            new("expectedRevision", "integer", true, "期望 Profile 修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["profiles.validate"] = [new("profileId", "string", true, "Profile ID")],
        ["translation.get"] = [new("gameId", "string", true, "游戏 ID")],
        ["translation.set"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("override", "string", true, "Auto / Required / NotRequired"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["tools.discover"] =
        [
            new("tool", "string", false, "mtool / renpythief / player / steam"),
            new("path", "string", false, "工具发现目标路径"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["verification.start"] =
        [
            new("toolId", "string", true, "工具 ID"),
            new("samplePath", "string", true, "授权样本路径"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["verification.get"] = [new("recordId", "string", true, "验证记录 ID")],
        ["verification.list"] = [new("toolId", "string", false, "按工具过滤")],
        ["verification.report"] =
        [
            new("recordId", "string", true, "验证记录 ID"),
            new("gameStarted", "boolean", true, "游戏是否启动"),
            new("translationConfirmed", "boolean", true, "翻译是否确认"),
            new("note", "string", false, "备注"),
        ],
        ["verification.invalidate"] =
        [
            new("recordId", "string", true, "验证记录 ID"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["launch.plan"] =
        [
            new("gameId", "string", true, "游戏 ID"),
            new("profileId", "string", true, "Profile ID"),
        ],
        ["launch.execute"] =
        [
            new("planId", "string", false, "预览计划 ID"),
            new("profileId", "string", false, "直接按 Profile 执行"),
            new("expectedRevision", "integer", false, "期望 Profile 修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["launch.status"] = [new("attemptId", "string", true, "尝试 ID")],
        ["launch.history"] = [new("gameId", "string", false, "按游戏过滤")],
        ["jobs.get"] = [new("jobId", "string", true, "作业 ID")],
        ["events.read"] =
        [
            // 名称以实现为准：OperationDispatcher.EventsRead 读取 "cursor"，响应回传 nextCursor。
            // 此前登记为 "after"/"lastSequence"，与实现和响应字段均不符，schema.get 会误导 MCP 客户端。
            new("cursor", "integer", false, "游标（上次响应的 nextCursor）；缺省表示从头全量读取"),
            new("limit", "integer", false, "单批上限（1–4096）"),
        ],
        ["notifications.list"] = [new("state", "string", false, "按状态过滤")],
        ["notifications.get"] = [new("notificationId", "string", true, "通知 ID")],
        ["notifications.acknowledge"] = [new("notificationId", "string", true, "通知 ID"), new("idempotencyKey", "string", true, "幂等键")],
        ["notifications.defer"] = [new("notificationId", "string", true, "通知 ID"), new("idempotencyKey", "string", true, "幂等键")],
        ["settings.get"] = [],
        ["settings.update"] =
        [
            new("activeViewId", "string", false, "激活视图（字符串或 null）"),
            new("autostartEnabled", "boolean", false, "开机启动"),
            new("scanIntervalMinutes", "integer", false, "核对周期（1–10080 分钟）"),
            new("theme", "string", false, "dark / light / system"),
            new("closeToTray", "boolean", false, "关闭即缩托盘"),
            new("uiFontScale", "number", false, "界面缩放（0.85–1.6）"),
            new("uiFontFamily", "string", false, "已安装字体的名称"),
            new("cacheParentDirectory", "string", false, "缓存存放位置的父目录；null 恢复默认"),
            new("expectedRevision", "integer", true, "期望设置修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["settings.reset"] = [new("idempotencyKey", "string", true, "幂等键")],
        ["views.list"] = [],
        ["views.get"] = [new("viewId", "string", true, "视图 ID")],
        ["views.create"] =
        [
            new("name", "string", true, "视图名"),
            new("search", "string", false, "搜索词"),
            new("favoriteOnly", "boolean", false, "仅收藏"),
            new("sort", "string", false, "title/title-asc、title-desc、recent/updated-desc 或 accepted-desc"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["views.update"] =
        [
            new("viewId", "string", true, "视图 ID"),
            new("name", "string", false, "新名称"),
            new("search", "string", false, "新搜索词"),
            new("favoriteOnly", "boolean", false, "仅收藏"),
            new("sort", "string", false, "title/title-asc、title-desc、recent/updated-desc 或 accepted-desc"),
            new("expectedRevision", "integer", true, "期望修订"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
        ["views.remove"] = [new("viewId", "string", true, "视图 ID"), new("idempotencyKey", "string", true, "幂等键")],
        ["views.activate"] = [new("viewId", "string", true, "视图 ID"), new("idempotencyKey", "string", true, "幂等键")],
        ["diagnostics.status"] = [],
        ["diagnostics.logs"] = [new("limit", "integer", false, "最近记录条数（1–1000）")],
        ["diagnostics.cache_rebuild"] = [new("idempotencyKey", "string", true, "幂等键")],
        ["backups.list"] = [],
        ["backups.create"] = [new("idempotencyKey", "string", true, "幂等键")],
        ["backups.inspect"] = [new("backupId", "string", true, "备份 ID")],
        ["backups.restore_plan"] = [new("backupId", "string", true, "备份 ID")],
        ["backups.restore"] =
        [
            new("backupId", "string", true, "备份 ID"),
            new("planId", "string", true, "restore_plan 返回的计划 ID"),
            new("idempotencyKey", "string", true, "幂等键"),
        ],
    };

    /// <summary>生成指定操作的 inputSchema；未登记操作返回通用对象 schema（注明待补充）。</summary>
    public static JsonElement BuildInputSchema(string operationId)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        if (InputSpecs.TryGetValue(operationId, out var specs))
        {
            foreach (var spec in specs)
            {
                properties[spec.Name] = new Dictionary<string, object>
                {
                    ["type"] = MapType(spec.Type),
                    ["description"] = spec.Description,
                };
                if (spec.Required)
                {
                    required.Add(spec.Name);
                }
            }

            return WriteSchema(properties, required, "操作输入参数对象");
        }

        return WriteSchema([], [], "该操作的输入 schema 尚未结构化登记；当前接受自由键值对");
    }

    /// <summary>输出统一为契约信封形状（data 结构由各操作 note 描述）。</summary>
    public static JsonElement BuildOutputSchema() =>
        WriteSchema(
            new Dictionary<string, object>
            {
                ["apiVersion"] = new Dictionary<string, object> { ["type"] = "string" },
                ["requestId"] = new Dictionary<string, object> { ["type"] = "string" },
                ["libraryInstanceId"] = new Dictionary<string, object> { ["type"] = new[] { "string", "null" } },
                ["dataEpoch"] = new Dictionary<string, object> { ["type"] = new[] { "string", "null" } },
                ["ok"] = new Dictionary<string, object> { ["type"] = "boolean" },
                ["status"] = new Dictionary<string, object> { ["type"] = "string" },
                ["data"] = new Dictionary<string, object>
                {
                    ["description"] = "操作结果载荷；结构随操作而异",
                },
                ["jobId"] = new Dictionary<string, object> { ["type"] = new[] { "string", "null" } },
                ["error"] = new Dictionary<string, object> { ["type"] = new[] { "object", "null" } },
            },
            ["apiVersion", "requestId", "ok", "status"],
            "统一结果信封（契约第 3 节）");

    private static string MapType(string type) => type switch
    {
        "integer" => "integer",
        "number" => "number",
        "boolean" => "boolean",
        "array" => "array",
        "string" => "string",
        _ => "string",
    };

    private static JsonElement WriteSchema(Dictionary<string, object> properties, List<string> required, string description)
    {
        var payload = new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
        {
            payload["required"] = required;
        }

        return JsonSerializer.SerializeToElement(payload);
    }
}
