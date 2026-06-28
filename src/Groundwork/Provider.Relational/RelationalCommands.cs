using System.Data.Common;

namespace Groundwork.Provider.Relational;

/// <summary>
/// Reusable command helpers for relational Groundwork modules: parameterized command creation,
/// ISO-8601 timestamp encoding suitable for lexical range comparisons, and monotonic sequence
/// allocation. Identifier generation now lives behind <c>IGroundworkIdentityGenerator</c> in
/// <c>Groundwork.Core.Identity</c>.
/// </summary>
public static class RelationalCommands
{
    /// <summary>The default sequence table name used by <see cref="NextSequenceAsync"/>.</summary>
    public const string DefaultSequenceTable = "groundwork_sequence";

    /// <summary>
    /// ISO-8601 round-trip UTC format. Fixed-width and offset-stable, so lexical string comparison
    /// matches chronological order for visibility/lease/expiry columns.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    public static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    public static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{name}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Allocates the next monotonic sequence for a (unit, scope) pair within the given transaction,
    /// using an UPSERT against <paramref name="sequenceTable"/> (schema: <c>unit TEXT, scope_key TEXT,
    /// next_value INTEGER, PRIMARY KEY (unit, scope_key)</c>).
    /// </summary>
    public static async Task<long> NextSequenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string unit,
        string scopeKey,
        CancellationToken cancellationToken,
        string sequenceTable = DefaultSequenceTable)
    {
        await using var upsert = CreateCommand(connection, transaction, $"""
            INSERT INTO {sequenceTable} (unit, scope_key, next_value)
            VALUES (@unit, @scope, 1)
            ON CONFLICT (unit, scope_key) DO UPDATE SET next_value = next_value + 1;
            """);
        AddParameter(upsert, "unit", unit);
        AddParameter(upsert, "scope", scopeKey);
        await upsert.ExecuteNonQueryAsync(cancellationToken);

        await using var read = CreateCommand(connection, transaction, $"""
            SELECT next_value FROM {sequenceTable}
            WHERE unit = @unit AND scope_key = @scope;
            """);
        AddParameter(read, "unit", unit);
        AddParameter(read, "scope", scopeKey);
        var value = await read.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value);
    }
}
