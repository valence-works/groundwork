namespace Groundwork.Operational.UnitOfWork;

/// <summary>
/// The set of operational storage-unit identities that must commit as one logical transaction.
/// </summary>
public sealed record OperationalCommitScope(IReadOnlyList<string> Units)
{
    public static OperationalCommitScope Of(params string[] units) => new(units);
}
