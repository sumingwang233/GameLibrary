using GameLibrary.Application.Catalog;

namespace GameLibrary.Infrastructure.Persistence;

public sealed partial class SqliteLibraryStore
{
    public T InTransaction<T>(Func<T> action)
    {
        lock (_sync)
        {
            if (_writeTransaction is not null) return action();
            using var transaction = _connection.BeginTransaction();
            _writeTransaction = transaction;
            try
            {
                var result = action();
                transaction.Commit();
                return result;
            }
            finally { _writeTransaction = null; }
        }
    }

    public CandidateReviewOutcome InReviewSavepoint(Func<CandidateReviewOutcome> action)
    {
        if (_writeTransaction is null) throw new InvalidOperationException("审核保存点必须属于事务");
        _writeTransaction.Save("review_item");
        var result = action();
        if (result.ErrorCode is not null) _writeTransaction.Rollback("review_item");
        _writeTransaction.Release("review_item");
        return result;
    }
}
