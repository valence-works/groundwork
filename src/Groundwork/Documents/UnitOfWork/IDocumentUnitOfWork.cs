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
/// <b>Staging:</b> each <see cref="SaveAsync"/>/<see cref="DeleteAsync"/> executes against the open
/// unit of work and returns its <see cref="DocumentStoreWriteResult"/> immediately (including
/// optimistic-concurrency <c>ConcurrencyConflict</c>/<c>NotFound</c> outcomes), so the caller can
/// decide whether to continue, <see cref="CommitAsync"/>, or <see cref="RollbackAsync"/>.
/// </para>
/// <para>
/// <b>All-or-nothing contract:</b> nothing is durable until <see cref="CommitAsync"/>. To honour
/// all-or-nothing semantics the caller must roll back when any staged operation reports a non-success
/// status (a status other than <see cref="DocumentStoreWriteStatus.Saved"/> or
/// <see cref="DocumentStoreWriteStatus.Deleted"/>). If a staged operation throws, or the unit of work
/// is disposed without committing, it is rolled back.
/// </para>
/// <para>
/// <b>Failure contract:</b> some providers (for example PostgreSQL) abort the whole transaction when a
/// single statement fails at the database level; after such a failure the only valid next step is
/// <see cref="RollbackAsync"/>/dispose. <see cref="CommitAsync"/> surfaces any commit-time failure as
/// an exception, leaving nothing durable.
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

    /// <summary>Atomically commits every staged operation. Throws if the commit cannot be honoured.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards every staged operation.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
