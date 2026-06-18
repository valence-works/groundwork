namespace Groundwork.Documents.Store;

/// <summary>
/// A multi-document atomic unit of work over a single <see cref="IDocumentStore"/>. Save and delete
/// operations staged through the transaction are applied within one underlying database transaction
/// and become durable only when <see cref="CommitAsync"/> succeeds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Staging:</b> each <see cref="SaveAsync"/>/<see cref="DeleteAsync"/> executes against the open
/// transaction and returns its <see cref="DocumentStoreWriteResult"/> immediately (including
/// optimistic-concurrency <c>ConcurrencyConflict</c>/<c>NotFound</c> outcomes), so the caller can
/// decide whether to continue, <see cref="CommitAsync"/>, or <see cref="RollbackAsync"/>.
/// </para>
/// <para>
/// <b>All-or-nothing contract:</b> nothing is durable until <see cref="CommitAsync"/>. To honour
/// all-or-nothing semantics the caller must roll back when any staged operation reports a non-success
/// status (a status other than <see cref="DocumentStoreWriteStatus.Saved"/> or
/// <see cref="DocumentStoreWriteStatus.Deleted"/>). If a staged operation throws, or the transaction
/// is disposed without committing, the transaction is rolled back.
/// </para>
/// <para>
/// <b>Failure contract:</b> some providers (for example PostgreSQL) abort the whole transaction when a
/// single statement fails at the database level; after such a failure the only valid next step is
/// <see cref="RollbackAsync"/>/dispose. <see cref="CommitAsync"/> surfaces any commit-time failure as
/// an exception, leaving nothing durable.
/// </para>
/// </remarks>
public interface IDocumentTransaction : IAsyncDisposable
{
    /// <summary>Stages a save within the transaction and returns its immediate result.</summary>
    Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stages a delete within the transaction and returns its immediate result.</summary>
    Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a document through the transaction (sees this transaction's uncommitted writes).</summary>
    Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default);

    /// <summary>Atomically commits every staged operation. Throws if the commit cannot be honoured.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards every staged operation.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown by <see cref="IDocumentStore.BeginTransactionAsync"/> when the underlying provider/deployment
/// cannot honour a multi-document atomic transaction (for example a standalone MongoDB server, which
/// requires a replica set for multi-document transactions). Providers fail loudly here rather than
/// silently degrading to non-atomic writes.
/// </summary>
public sealed class UnsupportedDocumentTransactionException(string provider, string reason)
    : InvalidOperationException($"Provider '{provider}' cannot begin a multi-document atomic transaction: {reason}")
{
    public string Provider { get; } = provider;

    public string Reason { get; } = reason;
}
