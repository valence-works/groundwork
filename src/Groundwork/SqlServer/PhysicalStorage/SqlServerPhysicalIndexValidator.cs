using Groundwork.Core.PhysicalStorage;

namespace Groundwork.SqlServer.PhysicalStorage;

internal static class SqlServerPhysicalIndexValidator
{
    private const int MaximumKeyColumns = 32;
    private const int MaximumKeyBytes = 1700;

    public static void Validate(ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        foreach (var index in route.Indexes)
        {
            if (index.Columns.Count > MaximumKeyColumns)
            {
                throw new InvalidOperationException(
                    $"SQL Server physical index '{index.Identity}' declares {index.Columns.Count} key columns; the provider limit is {MaximumKeyColumns}.");
            }

            var keyBytes = 0;
            foreach (var indexColumn in index.Columns)
            {
                var projection = route.ProjectedColumns.SingleOrDefault(column =>
                    column.Target == index.Target &&
                    column.Column.Identifier == indexColumn.Column.Identifier);
                keyBytes += projection is null
                    ? EnvelopeKeyBytes(route, index, indexColumn.Column.Identifier)
                    : ProjectedKeyBytes(index, projection.Definition, indexColumn.Column.Identifier);
            }
            if (keyBytes > MaximumKeyBytes)
            {
                throw new InvalidOperationException(
                    $"SQL Server physical index '{index.Identity}' has a worst-case key width of {keyBytes} bytes; the provider limit is {MaximumKeyBytes} bytes.");
            }
        }
    }

    private static int ProjectedKeyBytes(
        ExecutablePhysicalIndexRoute index,
        ProjectedColumnDefinition definition,
        string column)
    {
        if (definition.Type is PortablePhysicalType.String or PortablePhysicalType.Binary && definition.Length is null)
        {
            throw new InvalidOperationException(
                $"SQL Server physical index '{index.Identity}' requires bounded {definition.Type} key column '{column}'.");
        }

        return definition.Type switch
        {
            PortablePhysicalType.String => checked(definition.Length!.Value * 2),
            PortablePhysicalType.Int32 => 4,
            PortablePhysicalType.Int64 => 8,
            PortablePhysicalType.Decimal => DecimalBytes(index, definition, column),
            PortablePhysicalType.Boolean => 1,
            PortablePhysicalType.DateTime => 10,
            PortablePhysicalType.Guid => 16,
            PortablePhysicalType.Binary => definition.Length!.Value,
            PortablePhysicalType.Json => throw Unsupported(index, column, definition.Type),
            _ => throw Unsupported(index, column, definition.Type)
        };
    }

    private static int DecimalBytes(
        ExecutablePhysicalIndexRoute index,
        ProjectedColumnDefinition definition,
        string column) => definition.Precision switch
        {
            >= 1 and <= 9 => 5,
            >= 10 and <= 19 => 9,
            >= 20 and <= 28 => 13,
            _ => throw new InvalidOperationException(
                $"SQL Server physical index '{index.Identity}' requires Decimal key column '{column}' to declare portable precision 1-28.")
        };

    private static int EnvelopeKeyBytes(
        ExecutableStorageRoute route,
        ExecutablePhysicalIndexRoute index,
        string column)
    {
        if (index.Target == ExecutableStorageObjectRole.PrimaryStorage)
        {
            if (column == route.Envelope.DocumentKind.Identifier || column == route.Envelope.Id.Identifier)
                return 450 * 2;
            if (column == route.Envelope.StorageScope.Identifier)
                return 128 * 2;
            if (column == route.Envelope.SchemaVersion.Identifier)
                return 100 * 2;
            if (column == route.Envelope.Version.Identifier)
                return 8;
            if (column == route.Envelope.CanonicalJson.Identifier)
                throw Unsupported(index, column, PortablePhysicalType.Json);
        }
        else if (route.LinkedRelationship is { } relationship)
        {
            if (column == relationship.DocumentKind.Identifier || column == relationship.DocumentId.Identifier)
                return 450 * 2;
            if (column == relationship.StorageScope.Identifier)
                return 128 * 2;
        }

        throw new InvalidOperationException(
            $"SQL Server physical index '{index.Identity}' references key column '{column}' without a supported physical type mapping.");
    }

    private static InvalidOperationException Unsupported(
        ExecutablePhysicalIndexRoute index,
        string column,
        PortablePhysicalType type) =>
        new($"SQL Server physical index '{index.Identity}' does not support {type} key column '{column}'.");
}
