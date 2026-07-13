using System.Data;
using Groundwork.Core.Capabilities;
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
        Func<RelationalPhysicalMutationExecutionPoint, ValueTask>? intercept)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(provider);
        var storage = Storage(manifest, route);
        var capabilities = SqlitePhysicalQueryRuntime.Capabilities(provider);
        var compilation = Compile(route, storage, capabilities);
        var handlers = capabilities.HandlerIdentities.Select(registration =>
        {
            var certifications = compilation.Plans
                .Where(plan => plan.HandlerIdentity == registration.Value)
                .Select(RelationalPhysicalDocumentMutationHandler.Certify)
                .ToArray();
            return (IPhysicalDocumentMutationHandler)new RelationalPhysicalDocumentMutationHandler(
                registration.Value,
                registration.Key,
                store,
                certifications,
                intercept is null ? null : (point, _) => intercept(point));
        }).ToArray();
        return new PhysicalMutationDocumentStore(route, storage, capabilities, handlers);
    }

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
        var storage = Storage(manifest, route);
        var capabilities = SqlitePhysicalQueryRuntime.Capabilities(provider);
        var plan = Compile(route, storage, capabilities).Plans.Single(candidate =>
            candidate.MutationIdentity == mutation.MutationIdentity);
        var handler = new RelationalPhysicalDocumentMutationHandler(
            plan.HandlerIdentity,
            capabilities.HandlerIdentities.Single(item => item.Value == plan.HandlerIdentity).Key,
            store,
            [RelationalPhysicalDocumentMutationHandler.Certify(plan)]);
        var selection = handler.BuildSelectionCommand(
            mutation,
            plan,
            store.ResolveMutationScope(mutation.DocumentKind));
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

    private static StorageUnitPhysicalStorage Storage(StorageManifest manifest, ExecutableStorageRoute route) =>
        manifest.StorageUnits.Single(candidate => candidate.Identity == route.StorageUnit).PhysicalStorage
        ?? throw new InvalidOperationException($"Storage unit '{route.StorageUnit.Value}' has no physical mutation declarations.");

    private static PhysicalMutationPlanCompilationResult Compile(
        ExecutableStorageRoute route,
        StorageUnitPhysicalStorage storage,
        PhysicalQueryPlannerCapabilities capabilities)
    {
        var compilation = PhysicalMutationPlanCompiler.Compile(route, storage, capabilities);
        if (!compilation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }
        return compilation;
    }
}
