using System.Data.Common;
using Groundwork.Provider.Relational;

namespace Groundwork.Operational.Relational;

/// <summary>
/// Shared command helpers for the relational operational stores. Delegates to the reusable
/// <see cref="RelationalCommands"/> toolkit; the only operational specialization is the sequence
/// table name used for FIFO ordering.
/// </summary>
internal abstract class RelationalOperationalStoreBase(RelationalExecutor executor, IOperationalClock clock)
{
    private const string SequenceTable = "groundwork_operational_sequence";

    protected RelationalExecutor Executor { get; } = executor;

    protected IOperationalClock Clock { get; } = clock;

    protected static string Format(DateTimeOffset value) => RelationalCommands.FormatTimestamp(value);

    protected static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql) =>
        RelationalCommands.CreateCommand(connection, transaction, sql);

    protected static void AddParameter(DbCommand command, string name, object? value) =>
        RelationalCommands.AddParameter(command, name, value);

    protected static Task<long> NextSequenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string unit,
        string scopeKey,
        CancellationToken cancellationToken) =>
        RelationalCommands.NextSequenceAsync(connection, transaction, unit, scopeKey, cancellationToken, SequenceTable);

    protected static string NewMessageId() => RelationalCommands.NewId();

    protected static string NewLeaseToken() => RelationalCommands.NewId();
}
