using System.Data;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Store;
using Groundwork.Relational.Documents;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

/// <summary>Builds the certified SQLite bounded-mutation runtime for one compiled route.</summary>
public static class SqlitePhysicalMutationRuntime
{
    public static IBoundedDocumentMutationStore Create(
        SqlitePhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider) =>
        Create(store, manifest, route, provider, null);

    internal static IBoundedDocumentMutationStore Create(
        SqlitePhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        Func<RelationalPhysicalMutationExecutionPoint, ValueTask>? intercept) =>
        RelationalPhysicalMutationRuntime.Create(
            store,
            manifest,
            route,
            provider,
            SqliteGroundworkCapabilities.Provider.Name,
            "sqlite",
            CanonicalJsonValueKinds(provider),
            intercept);

    internal static async Task<string> ExplainAsync(
        SqliteConnection connection,
        SqlitePhysicalDocumentStore store,
        StorageManifest manifest,
        ExecutableStorageRoute route,
        ProviderIdentity provider,
        DocumentMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var selection = RelationalPhysicalMutationRuntime.BuildSelectionCommand(
            store,
            manifest,
            route,
            provider,
            SqliteGroundworkCapabilities.Provider.Name,
            "sqlite",
            mutation,
            CanonicalJsonValueKinds(provider));
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {selection.CommandText};";
        foreach (var (name, value) in selection.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{name}";
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            details.Add(reader.GetString(3));
        return string.Join(Environment.NewLine, details);
    }

    private static IReadOnlySet<IndexValueKind> CanonicalJsonValueKinds(ProviderIdentity provider) =>
        SqlitePhysicalQueryRuntime.Capabilities(provider)
            .SourceValueKinds[PhysicalQuerySourceKind.PrimaryCanonicalJson];
}
