namespace Groundwork.Documents.UnitOfWork;

/// <summary>
/// The set of document-kind identities that must commit as one logical transaction. Mirrors the
/// operational <c>OperationalCommitScope</c> so the document write lane and the operational write lane
/// share the same unit-of-work shape.
/// </summary>
public sealed record DocumentCommitScope(IReadOnlyList<string> Kinds)
{
    public static DocumentCommitScope Of(params string[] kinds) => new(kinds);
}
