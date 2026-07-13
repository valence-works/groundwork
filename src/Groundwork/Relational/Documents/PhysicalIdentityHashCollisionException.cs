namespace Groundwork.Relational.Documents;

/// <summary>
/// A provider-owned opaque identity key matched an existing row whose retained original identity
/// differs. Callers must not treat this as an optimistic-concurrency conflict.
/// </summary>
public sealed class PhysicalIdentityHashCollisionException : Exception
{
    public PhysicalIdentityHashCollisionException(string table, IReadOnlyList<string> identityColumns)
        : base(CreateMessage(table, identityColumns))
    {
        Table = table;
        IdentityColumns = Array.AsReadOnly(identityColumns.ToArray());
    }

    public string Table { get; }
    public IReadOnlyList<string> IdentityColumns { get; }

    private static string CreateMessage(string table, IReadOnlyList<string> identityColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(identityColumns);
        if (identityColumns.Count == 0)
            throw new ArgumentException("At least one retained identity column is required.", nameof(identityColumns));
        return $"Physical identity hash collision in table '{table}' for retained columns " +
               $"({string.Join(", ", identityColumns)}).";
    }
}
