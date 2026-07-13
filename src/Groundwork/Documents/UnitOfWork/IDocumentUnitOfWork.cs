using Groundwork.Core.Transactions;
using Groundwork.Documents.Store;

namespace Groundwork.Documents.UnitOfWork;

/// <summary>
/// Opens an <see cref="IDocumentUnitOfWork"/> spanning a declared set of document kinds that must
/// commit as one logical transaction. Mirrors the operational <c>IOperationalSessionFactory</c>;
/// maps to the storage capability <c>AtomicCommit</c>.
/// </summary>
public interface IDocumentSessionFactory
{
    /// <summary>
    /// The furthest atomic-commit boundary this store can honour. Callers can inspect this to detect
    /// native cross-document atomicity (<see cref="TransactionBoundary.CrossUnitAtomic"/>) and skip a
    /// compensation fallback, rather than discovering the limit by catching an exception from
    /// <see cref="BeginAsync"/>.
    /// </summary>
    TransactionBoundary TransactionBoundary { get; }

    /// <summary>
    /// Begins a unit of work over the document kinds named in <paramref name="scope"/>. A provider that
    /// cannot honor cross-document atomicity for the requested scope (for example a standalone MongoDB
    /// deployment) throws <see cref="UnsupportedAtomicCommitException"/> rather than silently degrading
    /// to non-atomic writes.
    /// </summary>
    Task<IDocumentUnitOfWork> BeginAsync(DocumentCommitScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// A multi-document atomic unit of work over a single <see cref="IDocumentStore"/>. Save and delete
/// operations staged here are applied within one underlying database transaction and become durable
/// only when <see cref="CommitAsync"/> succeeds. Disposing without committing rolls back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Commit-scope contract:</b> save, delete, and load reject a document kind that was not named
/// when the unit of work began. This argument rejection happens before database traffic and does not
/// make the unit of work terminal; a subsequent in-scope operation may continue normally.
/// </para>
/// <para>
/// <b>Staging:</b> each <see cref="SaveAsync"/>/<see cref="DeleteAsync"/> executes against the open
/// unit of work and returns its <see cref="DocumentStoreWriteResult"/> immediately. A non-success
/// outcome (including optimistic-concurrency <c>ConcurrencyConflict</c>/<c>NotFound</c>) atomically
/// rolls back and terminally poisons that unit of work; the caller must begin a new unit of work.
/// </para>
/// <para>
/// <b>All-or-nothing contract:</b> nothing is durable until <see cref="CommitAsync"/>. A non-successful
/// or failed save/delete rolls back the complete transaction before returning or throwing. Disposing
/// without committing also rolls back.
/// </para>
/// <para>
/// <b>Failure contract:</b> some providers (for example PostgreSQL) abort the whole transaction when a
/// single statement fails at the database level. Groundwork therefore makes every failed or
/// non-successful save/delete terminal consistently across providers. <see cref="CommitAsync"/>,
/// <see cref="RollbackAsync"/>, and further operations reject the completed unit. Commit-time failures
/// surface as exceptions and leave the unit terminal.
/// </para>
/// </remarks>
public interface IDocumentUnitOfWork : IAsyncDisposable
{
    /// <summary>Stages a save within the unit of work and returns its immediate result.</summary>
    Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stages a delete within the unit of work and returns its immediate result.</summary>
    Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a document through the unit of work (sees this unit of work's uncommitted writes).</summary>
    Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically commits every staged operation. Throws if the commit cannot be honoured. A
    /// <see cref="DocumentCommitAcknowledgementUncertainException"/> means the commit may be durable
    /// and callers must reconcile rather than repeat the transaction blindly.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards every staged operation.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
