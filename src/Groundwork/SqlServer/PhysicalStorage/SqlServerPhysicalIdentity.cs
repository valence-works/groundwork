using Groundwork.Core.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.Relational.PhysicalStorage;

namespace Groundwork.SqlServer.PhysicalStorage;

/// <summary>
/// Owns SQL Server's bounded opaque physical identity while retaining every original value for
/// exact ordinal verification. The hash-expression seam is internal and exists solely to prove
/// collision handling deterministically.
/// </summary>
internal sealed class SqlServerPhysicalIdentity
{
    private readonly SqlServerPhysicalIdentityHash hash;

    public SqlServerPhysicalIdentity(SqlServerPhysicalIdentityHash hash) =>
        this.hash = hash ?? throw new ArgumentNullException(nameof(hash));

    public RelationalPhysicalIdentityLayout Layout(
        IReadOnlyList<RelationalPhysicalIdentityColumn> identityColumns,
        IReadOnlyList<string> logicalPrimaryKey,
        Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(identityColumns);
        ArgumentNullException.ThrowIfNull(logicalPrimaryKey);
        ArgumentNullException.ThrowIfNull(quote);
        var identityNames = identityColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        if (logicalPrimaryKey.Any(column => !identityNames.Contains(column)))
            throw new ArgumentException("Every logical primary-key column must be a retained identity column.", nameof(logicalPrimaryKey));

        var columns = identityColumns.Select(column =>
        {
            var hidden = HiddenColumn(column.Name);
            return new RelationalProviderOwnedPhysicalColumn(
                hidden,
                $"{quote(hidden)} AS {hash.Expression(quote(column.Name))} PERSISTED NOT NULL",
                "binary(32)",
                false,
                IsComputed: true,
                IsPersisted: true,
                ComputedDefinition: hash.Expression(quote(column.Name)));
        }).ToArray();
        return new RelationalPhysicalIdentityLayout(
            Array.AsReadOnly(columns),
            Array.AsReadOnly(logicalPrimaryKey.Select(HiddenColumn).ToArray()));
    }

    public void ValidateRoute(ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ValidateTable(
            route,
            route.PrimaryStorage.Name.Identifier,
            [
                route.Envelope.DocumentKind.Identifier,
                route.Envelope.StorageScope.Identifier,
                route.Envelope.Id.Identifier
            ],
            new[]
            {
                route.Envelope.DocumentKind.Identifier,
                route.Envelope.StorageScope.Identifier,
                route.Envelope.Id.Identifier,
                route.Envelope.SchemaVersion.Identifier,
                route.Envelope.Version.Identifier,
                route.Envelope.CanonicalJson.Identifier,
                RelationalPhysicalStorageColumns.CreatedUtc,
                RelationalPhysicalStorageColumns.UpdatedUtc
            }.Concat(route.ProjectedColumns
                .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage)
                .Select(column => column.Column.Identifier)));

        if (route.LinkedIndexStorage is not null)
        {
            var relationship = route.LinkedRelationship!;
            ValidateTable(
                route,
                route.LinkedIndexStorage.Name.Identifier,
                [
                    relationship.DocumentKind.Identifier,
                    relationship.StorageScope.Identifier,
                    relationship.DocumentId.Identifier
                ],
                new[]
                {
                    relationship.DocumentKind.Identifier,
                    relationship.StorageScope.Identifier,
                    relationship.DocumentId.Identifier
                }.Concat(route.ProjectedColumns
                    .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
                    .Select(column => column.Column.Identifier)));
        }

        SqlServerPhysicalIndexValidator.Validate(route);
    }

    public string ExactPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts,
        Func<string, string> quote,
        bool includeOriginal)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(quote);
        return string.Join(" AND ", parts.SelectMany(part =>
        {
            var key = $"{Qualified(part.Alias, HiddenColumn(part.ColumnIdentifier), quote)} = {hash.Expression(part.ValueExpression)}";
            return includeOriginal
                ? new[] { key, $"{Qualified(part.Alias, part.ColumnIdentifier, quote)} = {part.ValueExpression}" }
                : [key];
        }));
    }

    public static string ExactJoin(
        IReadOnlyList<RelationalPhysicalIdentityJoinPart> parts,
        Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(quote);
        return string.Join(" AND ", parts.SelectMany(part => new[]
        {
            $"{Qualified(part.LeftAlias, HiddenColumn(part.LeftColumnIdentifier), quote)} = {Qualified(part.RightAlias, HiddenColumn(part.RightColumnIdentifier), quote)}",
            $"{Qualified(part.LeftAlias, part.LeftColumnIdentifier, quote)} = {Qualified(part.RightAlias, part.RightColumnIdentifier, quote)}"
        }));
    }

    private static void ValidateTable(
        ExecutableStorageRoute route,
        string table,
        IReadOnlyList<string> identityColumns,
        IEnumerable<string> visibleColumns)
    {
        var hidden = identityColumns.Select(HiddenColumn).ToHashSet(StringComparer.Ordinal);
        var collision = visibleColumns.FirstOrDefault(hidden.Contains);
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Executable route '{route.StorageUnit.Value}' maps visible column '{table}.{collision}', which collides with a SQL Server provider-owned identity column.");
        }
        if (hidden.Count != identityColumns.Count)
            throw new InvalidOperationException($"Executable route '{route.StorageUnit.Value}' produces duplicate SQL Server provider-owned identity columns in '{table}'.");
    }

    public static string HiddenColumn(string retainedColumn) =>
        SqlServerPhysicalName.Normalize($"{retainedColumn}_key");

    private static string Qualified(string? alias, string identifier, Func<string, string> quote) =>
        alias is null ? quote(identifier) : $"{alias}.{quote(identifier)}";
}

internal sealed class SqlServerPhysicalIdentityHash
{
    private readonly Func<string, string> expression;

    public SqlServerPhysicalIdentityHash(Func<string, string>? expression = null) =>
        this.expression = expression ?? (value =>
            $"CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(900), {value})))");

    public string Expression(string valueExpression) => expression(valueExpression);
}

internal static class SqlServerUnboundedIdentityHash
{
    public static string Expression(string valueExpression) =>
        $"CONVERT(binary(32), HASHBYTES('SHA2_256', CONVERT(varbinary(max), {valueExpression})))";
}

internal static class SqlServerMutationOperationIdentity
{
    public static string ExactPredicate(
        IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts,
        Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(quote);
        return string.Join(" AND ", parts.SelectMany(part => new[]
        {
            $"{quote(KeyColumn(part.ColumnIdentifier))} = {SqlServerUnboundedIdentityHash.Expression(part.ValueExpression)}",
            $"{quote(part.ColumnIdentifier)} = {part.ValueExpression}"
        }));
    }

    private static string KeyColumn(string retainedColumn) => retainedColumn switch
    {
        "manifest_id" => "manifest_key",
        "provider_name" => "provider_key",
        "storage_unit" => "storage_unit_key",
        "storage_scope" => "storage_scope_key",
        "operation_id" => "operation_key",
        _ => throw new ArgumentOutOfRangeException(
            nameof(retainedColumn),
            retainedColumn,
            "A mutation-ledger identity column is not recognized.")
    };
}
