namespace Groundwork.Documents.Store;

public sealed class DocumentAtomicWriteException(
    DocumentWriteOperationKind operation,
    string documentKind,
    string id,
    DocumentStoreWriteStatus status)
    : InvalidOperationException($"Atomic document write aborted: {OperationName(operation)} document '{id}' of kind '{documentKind}' returned status '{status}'.")
{
    public DocumentWriteOperationKind Operation { get; } = operation;
    public string DocumentKind { get; } = documentKind;
    public string Id { get; } = id;
    public DocumentStoreWriteStatus Status { get; } = status;

    private static string OperationName(DocumentWriteOperationKind operation) =>
        operation switch
        {
            DocumentWriteOperationKind.Save => "save",
            DocumentWriteOperationKind.Delete => "delete",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported document write operation.")
        };
}

