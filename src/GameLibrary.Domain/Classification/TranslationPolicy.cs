namespace GameLibrary.Domain.Classification;

/// <summary>翻译需求（策划案 7.4：策略字段与显示标签分离，删除标签不改变策略）。</summary>
public enum TranslationRequirement
{
    /// <summary>未设置用户覆盖：跟随继承结果。</summary>
    Auto,

    Required,

    NotRequired,
}

/// <summary>
/// 翻译策略：继承结果（InheritedFrom/Reason）与用户覆盖（Auto/Required/NotRequired）独立持久化；
/// 有效值 = 用户覆盖优先，否则继承值（Auto 视为未覆盖）。
/// </summary>
public sealed record TranslationPolicy
{
    public required TranslationRequirement UserOverride { get; init; }

    /// <summary>继承的祖先值（无 [toolNeed] 祖先即 Auto）。</summary>
    public required TranslationRequirement Inherited { get; init; }

    public string? InheritedFrom { get; init; }

    public string? Reason { get; init; }

    public TranslationRequirement Effective => UserOverride != TranslationRequirement.Auto
        ? UserOverride
        : Inherited;

    public bool IsRequired => Effective == TranslationRequirement.Required;

    public static TranslationPolicy FromInheritance(FolderClassification classification) =>
        new()
        {
            UserOverride = TranslationRequirement.Auto,
            Inherited = classification.RequiredByToolNeed
                ? TranslationRequirement.Required
                : TranslationRequirement.Auto,
            InheritedFrom = classification.ToolNeedSourceSegment,
            Reason = classification.RequiredByToolNeed
                ? $"祖先目录 [{classification.ToolNeedSourceSegment}] 标记需要翻译"
                : null,
        };
}
