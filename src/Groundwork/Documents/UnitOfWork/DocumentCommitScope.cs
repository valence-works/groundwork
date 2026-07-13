using System.Collections.ObjectModel;

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
        var normalizedKinds = DocumentKindSet.Normalize(kinds, nameof(kinds));
        if (normalizedKinds.Count != kinds.Count)
            throw new ArgumentException("Document commit scope kinds must be unique.", nameof(kinds));
        Kinds = normalizedKinds;
    }

    public IReadOnlyList<string> Kinds { get; }

    public static DocumentCommitScope Of(params string[] kinds) => new(kinds);

    /// <summary>
    /// Rejects an operation whose document kind is outside this immutable commit scope. Providers
    /// call this before database traffic so a caller contract error does not poison the unit of work.
    /// </summary>
    public void EnsureIncludes(string documentKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKind);
        if (!Kinds.Contains(documentKind, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Document kind '{documentKind}' is outside the unit-of-work commit scope [{string.Join(", ", Kinds)}].",
                nameof(documentKind));
        }
    }
}

internal static class DocumentKindSet
{
    public static ReadOnlyCollection<string> Normalize(IReadOnlyList<string> kinds, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(kinds, parameterName);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one document kind is required.", parameterName);
        if (kinds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Document kinds must be non-empty.", parameterName);

        return Array.AsReadOnly(kinds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray());
    }
}
