using System.Data;
using System.Data.Common;
using Groundwork.Core.Physicalization;
using Groundwork.Materialization;

namespace Groundwork.Relational.Materialization;

public abstract class RelationalMaterializerBase(DbConnection connection)
{
    protected virtual string ParameterPrefix => "@";

    protected abstract IReadOnlyList<string> SchemaStatements { get; }

    protected abstract string InsertSchemaHistorySql { get; }

    protected abstract IReadOnlyList<string> CreateOptimizedProjectionStatements(MaterializedProjection projection);

    public async Task MaterializeAsync(MaterializationPlan plan, CancellationToken cancellationToken = default)
    {
        if (!plan.IsPlannable)
            throw new InvalidOperationException("Cannot execute an unplannable materialization plan.");

        await EnsureOpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var statement in SchemaStatements)
            await ExecuteAsync(statement, transaction, cancellationToken);

        foreach (var operation in plan.Operations)
        {
            switch (operation)
            {
                case CreateStorageUnitOperation:
                case CreateIndexOperation:
                    break;
                case CreateOptimizedProjectionOperation projection:
                    await MaterializeOptimizedProjectionAsync(projection.Projection, transaction, cancellationToken);
                    break;
                case RecordSchemaHistoryOperation history:
                    await RecordSchemaHistoryAsync(history.Entry, transaction, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported materialization operation '{operation.Kind}'.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordSchemaHistoryAsync(
        SchemaHistoryEntry history,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(InsertSchemaHistorySql, transaction);
        AddParameter(command, "manifestId", history.ManifestIdentity.Value);
        AddParameter(command, "manifestVersion", history.ManifestVersion.Value);
        AddParameter(command, "providerName", history.Provider.Name);
        AddParameter(command, "providerVersion", history.Provider.Version);
        AddParameter(command, "appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected virtual async Task MaterializeOptimizedProjectionAsync(
        MaterializedProjection projection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in CreateOptimizedProjectionStatements(projection))
            await ExecuteAsync(statement, transaction, cancellationToken);
    }

    protected async Task ExecuteAsync(string sql, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected DbCommand CreateCommand(string commandText, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    protected void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"{ParameterPrefix}{name}";
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
    }
}
