using GameLibrary.Domain.Catalog;
using Microsoft.Data.Sqlite;

namespace GameLibrary.Infrastructure.Persistence;

// 批量写操作转发（optimize-v1.7.23）：games.update_batch / games.remove_batch /
// tags.assign_batch 共用「一个外层事务 + 每项一个保存点」的提交形状——单项业务失败
// 只回滚自己，整批仍可提交（对应 candidates.review_batch 的逐项语义）。业务规则全部
// 复用现有单条方法（SetGamePreferences / RemoveGame / AssignTag），不复制 SQL，也不
// 在批量里另写一套写入路径。

public sealed partial class SqliteLibraryStore
{
    /// <summary>逐项失败类别（持久层不依赖 Contracts，由宿主映射为线上错误码）。</summary>
    public enum BatchFailureKind
    {
        None,
        InvalidArgument,
        NotFound,
        RevisionConflict,
        InternalError,
    }

    /// <summary>批量更新的单项输入；三项状态全空由 SetGamePreferences 按单条语义拒绝。</summary>
    public sealed record BatchPreferenceItem(string GameId, int ExpectedRevision, bool? Favorite, bool? Played, int? Rating);

    /// <summary>批量移除/批量挂标签的单项输入（只带定位与乐观锁）。</summary>
    public sealed record BatchRevisionItem(string GameId, int ExpectedRevision);

    /// <summary>逐项回执：Failure 为 None 表示成功；Changed 供调用方在事务提交后发事件。</summary>
    public sealed record BatchWriteOutcome(
        string GameId,
        bool Ok,
        int? Revision = null,
        BatchFailureKind Failure = BatchFailureKind.None,
        string? Message = null,
        string? IgnoreId = null,
        bool Changed = false);

    /// <summary>整批回执：Items 与输入同序；Updated + Failed = Items.Count。</summary>
    public sealed record BatchWriteReport(IReadOnlyList<BatchWriteOutcome> Items, int Updated, int Failed)
    {
        /// <summary>本批实际产生写入的项（已挂标签的幂等重放不在其中）。</summary>
        public IEnumerable<BatchWriteOutcome> Written => Items.Where(item => item.Changed);
    }

    public BatchWriteReport UpdateGamesBatch(IReadOnlyList<BatchPreferenceItem> items)
    {
        var utcNow = DateTime.UtcNow;
        return RunBatch(
            items,
            static item => item.GameId,
            item =>
            {
                var game = TryGetGame(item.GameId);
                if (game is null) return NotFound(item.GameId);
                if (game.Revision != item.ExpectedRevision) return Conflict(item.GameId, item.ExpectedRevision, game.Revision);

                var revision = SetGamePreferences(item.GameId, item.Favorite, item.Played, item.Rating, item.ExpectedRevision, utcNow);
                if (revision is null)
                {
                    // 事务内先读后写之间无并发改动；仍兜底一次，形状与 games.remove 的终态冲突一致。
                    var latest = TryGetGame(item.GameId);
                    return Conflict(item.GameId, item.ExpectedRevision, latest?.Revision);
                }

                return new BatchWriteOutcome(item.GameId, true, revision, Changed: true);
            });
    }

    public BatchWriteReport RemoveGamesBatch(IReadOnlyList<BatchRevisionItem> items)
    {
        var utcNow = DateTime.UtcNow;
        return RunBatch(
            items,
            static item => item.GameId,
            item =>
            {
                var game = TryGetGame(item.GameId);
                if (game is null) return NotFound(item.GameId);
                if (game.Membership != "active")
                    return new BatchWriteOutcome(item.GameId, false, Failure: BatchFailureKind.InvalidArgument, Message: $"游戏已从库中移除：{item.GameId}");
                if (game.Revision != item.ExpectedRevision) return Conflict(item.GameId, item.ExpectedRevision, game.Revision);

                // 与 FinalizeRemoval 同形：忽略规则按当前根路径精确登记，不删文件（批量契约无 deleteFiles）。
                var ignore = new IgnoreRule
                {
                    IgnoreId = $"ignore-{Guid.NewGuid():N}",
                    Scope = "ExactPath",
                    Path = game.RootPath,
                    GameId = game.GameId,
                    Reason = "games.remove",
                    CreatedUtc = utcNow,
                };
                var revision = RemoveGame(game.GameId, item.ExpectedRevision, ignore, utcNow);
                if (revision is null)
                {
                    var latest = TryGetGame(item.GameId);
                    return Conflict(item.GameId, item.ExpectedRevision, latest?.Revision);
                }

                return new BatchWriteOutcome(game.GameId, true, revision, IgnoreId: ignore.IgnoreId, Changed: true);
            });
    }

    public BatchWriteReport AssignTagBatch(string tagId, IReadOnlyList<BatchRevisionItem> items)
    {
        var utcNow = DateTime.UtcNow;
        return RunBatch(
            items,
            static item => item.GameId,
            item =>
            {
                var game = TryGetGame(item.GameId);
                if (game is null) return NotFound(item.GameId);
                if (game.Revision != item.ExpectedRevision) return Conflict(item.GameId, item.ExpectedRevision, game.Revision);

                // tags.assign 的语义：已挂项是成功但无写入（不 bump 游戏 Revision）。
                var changed = AssignTag(item.GameId, tagId, utcNow);
                return new BatchWriteOutcome(item.GameId, true, game.Revision, Changed: changed);
            });
    }

    /// <summary>
    /// 批量公共骨架：外层一个事务保证整批原子提交，每项套一个嵌套 InTransaction
    /// （= SAVEPOINT）——单项抛错先回滚该项再抛出，由这里按错误类型转成该项回执，
    /// 其余项继续执行。已知业务异常只来自单项校验（参数/乐观锁/游戏或标签缺失）。
    /// </summary>
    private BatchWriteReport RunBatch<T>(IReadOnlyList<T> items, Func<T, string> gameIdOf, Func<T, BatchWriteOutcome> apply)
    {
        return InTransaction(() =>
        {
            var outcomes = new List<BatchWriteOutcome>(items.Count);
            foreach (var item in items)
            {
                try
                {
                    outcomes.Add(InTransaction(() => apply(item)));
                }
                catch (ArgumentException ex)
                {
                    outcomes.Add(Failure(gameIdOf(item), BatchFailureKind.InvalidArgument, ex.Message));
                }
                catch (InvalidOperationException ex)
                {
                    outcomes.Add(Failure(gameIdOf(item), BatchFailureKind.InvalidArgument, ex.Message));
                }
                catch (SqliteException ex)
                {
                    outcomes.Add(Failure(gameIdOf(item), BatchFailureKind.InternalError, ex.Message));
                }
            }

            var updated = outcomes.Count(outcome => outcome.Ok);
            return new BatchWriteReport(outcomes, updated, outcomes.Count - updated);
        });
    }

    private static BatchWriteOutcome Failure(string gameId, BatchFailureKind kind, string message)
        => new(gameId, false, Failure: kind, Message: message);

    private static BatchWriteOutcome NotFound(string gameId)
        => new(gameId, false, Failure: BatchFailureKind.NotFound, Message: $"游戏不存在：{gameId}");

    private static BatchWriteOutcome Conflict(string gameId, int expectedRevision, int? currentRevision)
        => new(gameId, false, Failure: BatchFailureKind.RevisionConflict, Message: $"游戏 Revision 不一致：期望 {expectedRevision}，当前 {currentRevision}");
}
