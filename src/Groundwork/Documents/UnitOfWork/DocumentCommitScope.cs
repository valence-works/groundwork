namespace Groundwork.Documents.UnitOfWork;

/// <summary>
/// The set of document-kind identities that must commit as one logical transaction. Mirrors the
/// operational <c>OperationalCommitScope</c> so the document write lane and the operational write lane
/// share the same unit-of-work shape. Rejects null, empty, blank, or duplicate kind lists at
/// construction so providers receive a well-formed scope.
/// </summary>
public sealed record DocumentCommitScope
{
    public DocumentCommitScope(IReadOnlyList<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        if (kinds.Count == 0)
            throw new ArgumentException("A document commit scope must name at least one document kind.", nameof(kinds));

        if (kinds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Document commit scope kinds must be non-empty.", nameof(kinds));

        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count)
            throw new ArgumentException("Document commit scope kinds must be unique.", nameof(kinds));

        Kinds = kinds;
    }

    public IReadOnlyList<string> Kinds { get; }

    public static DocumentCommitScope Of(params string[] kinds) => new(kinds);
}
