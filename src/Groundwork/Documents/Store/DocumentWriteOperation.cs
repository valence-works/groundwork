using Groundwork.Documents.UnitOfWork;

namespace Groundwork.Documents.Store;

public enum DocumentWriteOperationKind
{
    Save,
    Delete
}

public sealed record DocumentWriteOperation
{
    private DocumentWriteOperation(
        DocumentWriteOperationKind kind,
        SaveDocumentRequest? saveRequest,
        DeleteDocumentRequest? deleteRequest)
    {
        Kind = kind;
        SaveRequest = saveRequest;
        DeleteRequest = deleteRequest;
    }

    public DocumentWriteOperationKind Kind { get; }
    public SaveDocumentRequest? SaveRequest { get; }
    public DeleteDocumentRequest? DeleteRequest { get; }

    public string DocumentKind => Kind switch
    {
        DocumentWriteOperationKind.Save => SaveRequest!.DocumentKind,
        DocumentWriteOperationKind.Delete => DeleteRequest!.DocumentKind,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported document write operation.")
    };

    public string Id => Kind switch
    {
        DocumentWriteOperationKind.Save => SaveRequest!.Id,
        DocumentWriteOperationKind.Delete => DeleteRequest!.Id,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported document write operation.")
    };

    internal DocumentStoreWriteStatus SuccessStatus => Kind switch
    {
        DocumentWriteOperationKind.Save => DocumentStoreWriteStatus.Saved,
        DocumentWriteOperationKind.Delete => DocumentStoreWriteStatus.Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported document write operation.")
    };

    public static DocumentWriteOperation Save(SaveDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new DocumentWriteOperation(DocumentWriteOperationKind.Save, request, null);
    }

    public static DocumentWriteOperation Delete(DeleteDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new DocumentWriteOperation(DocumentWriteOperationKind.Delete, null, request);
    }

    internal Task<DocumentStoreWriteResult> ApplyAsync(IDocumentUnitOfWork unitOfWork, CancellationToken cancellationToken) =>
        Kind switch
        {
            DocumentWriteOperationKind.Save => unitOfWork.SaveAsync(SaveRequest!, cancellationToken),
            DocumentWriteOperationKind.Delete => unitOfWork.DeleteAsync(DeleteRequest!, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported document write operation.")
        };
}

