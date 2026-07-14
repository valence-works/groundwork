using System.Data;
using Groundwork.Core.PhysicalStorage;
using Groundwork.SqlServer.PhysicalStorage;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class SqlServerDocumentIdentityNoiseSeeder
{
    public static async Task SeedAsync(
        string connectionString,
        ExecutableDocumentIdentityRoute identity,
        string table,
        (string Column, object Value)? source = null,
        IReadOnlyDictionary<string, object?>? overrides = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var columns = new List<string>();
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText =
                "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND is_computed = 0 ORDER BY column_id;";
            metadata.Parameters.AddWithValue("@table", table);
            await using var reader = await metadata.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(0));
        }

        var data = new DataTable();
        object[] template;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT TOP (1) {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)}" +
                               (source is null ? ";" : $" WHERE {Quote(source.Value.Column)} = @source;");
            if (source is not null)
                read.Parameters.AddWithValue("@source", source.Value.Value);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            template = new object[reader.FieldCount];
            reader.GetValues(template);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                data.Columns.Add(reader.GetName(ordinal), reader.GetFieldType(ordinal));
        }

        var originalIndex = columns.IndexOf(identity.OriginalId.Identifier);
        var comparisonIndex = columns.IndexOf(identity.ComparisonKey.Identifier);
        var lookupIndex = columns.IndexOf(identity.LookupKey.Identifier);
        Assert.True(originalIndex >= 0);
        Assert.True(comparisonIndex >= 0);
        Assert.True(lookupIndex >= 0);
        var templateId = Assert.IsType<string>(template[originalIndex]);
        for (var number = 1; number <= 4096; number++)
        {
            var values = (object[])template.Clone();
            var noiseId = $"{templateId}-noise-{number}";
            var projected = identity.Project(noiseId);
            values[originalIndex] = noiseId;
            values[comparisonIndex] = SqlServerDocumentIdentityEncoding.Comparison(projected.ComparisonKey);
            values[lookupIndex] = SqlServerDocumentIdentityEncoding.Lookup(projected.LookupKey);
            if (overrides is not null)
            {
                foreach (var (column, value) in overrides)
                {
                    var index = columns.IndexOf(column);
                    Assert.True(index >= 0);
                    values[index] = value ?? DBNull.Value;
                }
            }
            data.Rows.Add(values);
        }

        using var bulk = new SqlBulkCopy(connection)
        {
            DestinationTableName = Quote(table),
            BatchSize = 1024
        };
        foreach (var column in columns)
            bulk.ColumnMappings.Add(column, column);
        await bulk.WriteToServerAsync(data);
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
