using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

public static class SqlServerPhysicalQueryRuntime
{
    /// <summary>
    /// Builds a query runtime whose native explanation executes each exact parameterized read-only
    /// production command while SQL Server STATISTICS XML is enabled.
    /// </summary>
    public static IBoundedDocumentStore Create(
        SqlServerPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) => Create(store, manifest, route, provider, new SqlServerPhysicalQueryExplainHooks());

    internal static IBoundedDocumentStore Create(
        SqlServerPhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        SqlServerPhysicalQueryExplainHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        return
        RelationalPhysicalQueryRuntime.CreateWithExplainer(
            store,
            manifest,
            route,
            provider,
            "sqlserver",
            (command, cancellationToken) => ExplainAsync(command, hooks, cancellationToken));
    }

    internal static Task<RelationalPhysicalNativeQueryPlan> ExplainAsync(
        DbCommand command,
        CancellationToken cancellationToken) =>
        ExplainAsync(command, new SqlServerPhysicalQueryExplainHooks(), cancellationToken);

    private static async Task<RelationalPhysicalNativeQueryPlan> ExplainAsync(
        DbCommand command,
        SqlServerPhysicalQueryExplainHooks hooks,
        CancellationToken cancellationToken)
    {
        var connection = command.Connection as SqlConnection ?? throw new InvalidOperationException(
            "SQL Server native explain requires a bound SqlConnection.");
        Exception? primaryFailure = null;
        try
        {
            await SetStatisticsXmlAsync(connection, enabled: true, cancellationToken);
            await InvokeAsync(hooks.AfterEnableAcknowledged, cancellationToken);
            await InvokeAsync(hooks.BeforeRead, command, cancellationToken);
            var plans = await ExecuteAndReadPlansAsync(command, cancellationToken);

            if (plans.Count == 0)
                throw new InvalidOperationException("SQL Server returned no STATISTICS XML for the physical document query.");
            return new RelationalPhysicalNativeQueryPlan("sqlserver-statistics-xml", string.Join(Environment.NewLine, plans));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await InvokeAsync(hooks.BeforeDisable, CancellationToken.None);
                await SetStatisticsXmlAsync(connection, enabled: false, CancellationToken.None);
            }
            catch (Exception cleanupFailure)
            {
                await QuarantineAsync(connection, cleanupFailure);
                if (primaryFailure is not null)
                    RelationalCleanupFailures.Attach(primaryFailure, cleanupFailure);
                else
                    throw;
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ExecuteAndReadPlansAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (!reader.GetName(ordinal).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase) &&
                        reader.GetFieldType(ordinal) != typeof(SqlXml))
                        continue;
                    var content = reader.GetValue(ordinal) switch
                    {
                        SqlXml xml when !xml.IsNull => xml.Value,
                        SqlString text when !text.IsNull => text.Value,
                        string text => text,
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(content))
                        plans.Add(content);
                }
            }
        } while (await reader.NextResultAsync(cancellationToken));
        return plans;
    }

    private static async Task SetStatisticsXmlAsync(
        DbConnection connection,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML {(enabled ? "ON" : "OFF")};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task QuarantineAsync(SqlConnection connection, Exception cleanupFailure)
    {
        try
        {
            SqlConnection.ClearPool(connection);
        }
        catch (Exception quarantineFailure)
        {
            RelationalCleanupFailures.Attach(cleanupFailure, quarantineFailure);
        }
        try
        {
            await connection.CloseAsync();
        }
        catch (Exception quarantineFailure)
        {
            RelationalCleanupFailures.Attach(cleanupFailure, quarantineFailure);
        }
    }

    private static ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask>? hook,
        CancellationToken cancellationToken) =>
        hook?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;

    private static ValueTask InvokeAsync(
        Func<DbCommand, CancellationToken, ValueTask>? hook,
        DbCommand command,
        CancellationToken cancellationToken) =>
        hook?.Invoke(command, cancellationToken) ?? ValueTask.CompletedTask;
}

internal sealed record SqlServerPhysicalQueryExplainHooks(
    Func<CancellationToken, ValueTask>? AfterEnableAcknowledged = null,
    Func<DbCommand, CancellationToken, ValueTask>? BeforeRead = null,
    Func<CancellationToken, ValueTask>? BeforeDisable = null);
