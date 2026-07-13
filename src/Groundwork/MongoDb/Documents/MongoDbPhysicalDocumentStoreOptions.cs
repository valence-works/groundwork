namespace Groundwork.MongoDb.Documents;

/// <summary>Bounds MongoDB physical-document transaction recovery.</summary>
public sealed record MongoDbPhysicalDocumentStoreOptions
{
    /// <summary>
    /// Maximum transaction-body executions, including the initial attempt. A retry always uses a
    /// fresh driver session and transaction.
    /// </summary>
    public int MaximumTransactionAttempts { get; init; } = 5;

    /// <summary>Maximum elapsed time allowed for transaction-body retries.</summary>
    public TimeSpan TransactionRetryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum commit attempts, including the initial attempt, after MongoDB reports an unknown
    /// commit result. The transaction body is never re-executed in this lane.
    /// </summary>
    public int MaximumCommitAttempts { get; init; } = 5;

    /// <summary>Maximum elapsed time allowed for same-session commit acknowledgement retries.</summary>
    public TimeSpan CommitRetryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumTransactionAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumCommitAttempts, 1);
        if (TransactionRetryTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TransactionRetryTimeout));
        if (CommitRetryTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CommitRetryTimeout));
    }
}
