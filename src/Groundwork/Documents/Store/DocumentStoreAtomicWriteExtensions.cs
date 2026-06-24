using Groundwork.Documents.UnitOfWork;

namespace Groundwork.Documents.Store;

public static class DocumentStoreAtomicWriteExtensions
{
    public static Task SaveAllAsync(
        this IDocumentStore store,
        DocumentCommitScope scope,
        IReadOnlyList<SaveDocumentRequest> saves,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saves);
        return store.WriteAllAsync(scope, saves.Select(DocumentWriteOperation.Save).ToArray(), cancellationToken);
    }
    public static Task DeleteAllAsync(
        this IDocumentStore store,
        DocumentCommitScope scope,
        IReadOnlyList<DeleteDocumentRequest> deletes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletes);
        return store.WriteAllAsync(scope, deletes.Select(DocumentWriteOperation.Delete).ToArray(), cancellationToken);
    }

    public static async Task WriteAllAsync(
        this IDocumentStore store,
        DocumentCommitScope scope,
        IReadOnlyList<DocumentWriteOperation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
            throw new ArgumentException("At least one document write operation is required.", nameof(operations));

        await using var unitOfWork = await store.BeginAsync(scope, cancellationToken);

        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);

            var result = await operation.ApplyAsync(unitOfWork, cancellationToken);
            if (result.Status != operation.SuccessStatus)
                throw new DocumentAtomicWriteException(operation.Kind, operation.DocumentKind, operation.Id, result.Status);
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }
}
