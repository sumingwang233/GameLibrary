namespace GameLibrary.Domain.Detection;

/// <summary>用户确认的目录解释；只适用于该目录及当时的入口清单，不按目录名全局匹配。</summary>
public sealed record FlashDirectoryRule(string DirectoryPath, string Kind,
    IReadOnlyList<string> EntryPaths, IReadOnlyList<string> Inventory, int Revision = 1);

public sealed record FlashDirectoryGroup(string DirectoryPath, string Kind, bool RequiresReview,
    IReadOnlyList<string> EntryPaths, IReadOnlyList<string> Inventory, bool Complete,
    IReadOnlyList<string> Reasons, bool IncludeDescendants = true);
