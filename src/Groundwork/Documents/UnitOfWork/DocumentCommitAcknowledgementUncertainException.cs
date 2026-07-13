using System.Collections.ObjectModel;

namespace Groundwork.Documents.UnitOfWork;

/// <summary>
/// The provider may have committed a document transaction, but the commit acknowledgement could
/// not be obtained within the configured retry budget. Callers must reconcile durable state and
/// must not treat this condition as an optimistic-concurrency conflict or blindly repeat effects.
/// </summary>
public sealed class DocumentCommitAcknowledgementUncertainException : IOException
{
    public DocumentCommitAcknowledgementUncertainException(
        IReadOnlyList<string> documentKinds,
        Exception? innerException = null)
        : this(DocumentKindSet.Normalize(documentKinds, nameof(documentKinds)), innerException)
    {
    }

    public IReadOnlyList<string> DocumentKinds { get; }

    private DocumentCommitAcknowledgementUncertainException(
        ReadOnlyCollection<string> normalizedDocumentKinds,
        Exception? innerException)
        : base(BuildMessage(normalizedDocumentKinds), innerException)
    {
        DocumentKinds = normalizedDocumentKinds;
    }

    private static string BuildMessage(IReadOnlyList<string> normalizedDocumentKinds) =>
        $"The document transaction for [{string.Join(", ", normalizedDocumentKinds)}] may have committed before acknowledgement was lost.";
}
