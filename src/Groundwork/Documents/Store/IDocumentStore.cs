namespace Groundwork.Documents.Store;

public interface IDocumentStore
{
    Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default);
    Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default);
    Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentEnvelope>> QueryAsync(DocumentStoreQuery query, CancellationToken cancellationToken = default);

    /// <summary>Executes a closed portable query, returning the page window plus the total predicate count.</summary>
    Task<DocumentQueryResult> QueryAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default);

    /// <summary>Returns the first matching document (honouring ordering), or <see langword="null"/>.</summary>
    Task<DocumentEnvelope?> FirstOrDefaultAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default);

    /// <summary>Returns whether any document matches the query.</summary>
    Task<bool> AnyAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default);
}
