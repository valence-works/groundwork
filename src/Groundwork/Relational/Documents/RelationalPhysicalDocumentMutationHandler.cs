using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;

namespace Groundwork.Relational.Documents;

/// <summary>Observable transaction boundaries used by provider conformance tests.</summary>
internal enum RelationalPhysicalMutationExecutionPoint
{
    BeforeCommit,
    BeforeRollback,
    AfterCommitBeforeAcknowledgement
}

/// <summary>
/// Relational executor for one certified bounded mutation source. Selection is rendered by the
/// bounded-query SQL builder into a transaction-local identity table before any row is changed.
/// </summary>
internal sealed class RelationalPhysicalDocumentMutationHandler : IPhysicalDocumentMutationHandler
{
    private const string SelectionTable = "groundwork_bounded_mutation_selection";
    private const string SelectionKind = "document_kind";
    private const string SelectionScope = "storage_scope";
    private const string SelectionId = "document_id";

    private readonly RelationalPhysicalDocumentStore store;
    private readonly RelationalPhysicalDocumentQueryHandler predicateBuilder;
    private readonly Func<RelationalPhysicalMutationExecutionPoint, CancellationToken, ValueTask>? intercept;

    internal RelationalPhysicalDocumentMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        RelationalPhysicalDocumentStore store,
        IReadOnlyList<PhysicalMutationHandlerCertification> certifications)
        : this(identity, source, store, certifications, null)
    {
    }

    internal RelationalPhysicalDocumentMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        RelationalPhysicalDocumentStore store,
        IReadOnlyList<PhysicalMutationHandlerCertification> certifications,
        Func<RelationalPhysicalMutationExecutionPoint, CancellationToken, ValueTask>? intercept)
    {
        Identity = string.IsNullOrWhiteSpace(identity)
            ? throw new ArgumentException("A handler identity is required.", nameof(identity))
            : identity;
        Source = source;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        Certifications = Array.AsReadOnly(
            (certifications ?? throw new ArgumentNullException(nameof(certifications))).ToArray());
        this.intercept = intercept;
        predicateBuilder = new RelationalPhysicalDocumentQueryHandler(identity, source, store, []);
    }

    public string Identity { get; }

    public PhysicalQuerySourceKind Source { get; }

    public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } =
        Enum.GetValues<PortableQueryOperation>().ToFrozenSet();

    public IReadOnlySet<BoundedMutationActionKind> SupportedActions { get; } =
        Enum.GetValues<BoundedMutationActionKind>().ToFrozenSet();

    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; } =
        FrozenDictionary<string, string>.Empty;

    public IReadOnlyList<PhysicalMutationHandlerCertification> Certifications { get; }

    public bool SupportsCompoundPredicates => true;

    public bool SupportsDisjunction => true;

    public async Task<BoundedMutationResult> ExecuteAsync(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        CancellationToken cancellationToken)
    {
        var scope = store.ResolveMutationScope(mutation.DocumentKind);
        if (scope.AcrossScopes || scope.StorageKey is null)
            throw new InvalidOperationException("Bounded mutations require one route-derived target scope.");
        var fingerprint = BoundedMutationRequestFingerprint.Create(mutation, plan, scope.StorageKey);
        var completed = false;
        return await store.ExecutePhysicalMutationAsync(
            async (connection, transaction, ct) =>
            {
                var durable = await ReadOperationAsync(connection, transaction, mutation, plan, scope, ct);
                if (durable is not null)
                {
                    if (!string.Equals(durable.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        throw new BoundedMutationOperationConflictException(
                            mutation.OperationId,
                            fingerprint,
                            durable.Value.Fingerprint);
                    }
                    return BoundedMutationResult.Replayed(durable.Value.AffectedCount);
                }

                await PrepareSelectionAsync(connection, transaction, mutation, plan, scope, ct);
                var affected = await CountSelectionAsync(connection, transaction, ct);
                if (plan.Action is PhysicalDeleteMutationAction)
                    await DeleteSelectionAsync(connection, transaction, store.GetRoute(mutation.DocumentKind), ct);
                else
                    await TransitionSelectionAsync(
                        connection,
                        transaction,
                        store.GetRoute(mutation.DocumentKind),
                        (PhysicalTransitionMutationAction)plan.Action,
                        ct);
                await RecordOperationAsync(
                    connection,
                    transaction,
                    mutation,
                    plan,
                    scope,
                    fingerprint,
                    affected,
                    ct);
                await DropSelectionAsync(connection, transaction, ct);
                completed = true;
                return BoundedMutationResult.Completed(affected);
            },
            beforeCommit: intercept is null
                ? null
                : ct => completed
                    ? intercept(RelationalPhysicalMutationExecutionPoint.BeforeCommit, ct)
                    : ValueTask.CompletedTask,
            afterCommitBeforeAcknowledgement: intercept is null
                ? null
                : ct => completed
                    ? intercept(RelationalPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement, ct)
                    : ValueTask.CompletedTask,
            beforeRollback: intercept is null
                ? null
                : ct => intercept(RelationalPhysicalMutationExecutionPoint.BeforeRollback, ct),
            cancellationToken);
    }

    internal static PhysicalMutationHandlerCertification Certify(PhysicalMutationPlan plan) => new(plan);

    internal RelationalPhysicalQueryCommand BuildSelectionCommand(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope)
    {
        var query = PredicateQuery(mutation, plan);
        var predicate = predicateBuilder.BuildPredicate(query, plan.Predicate, scope);
        var route = store.GetRoute(mutation.DocumentKind);
        return new RelationalPhysicalQueryCommand(
            $"SELECT DISTINCT p.{store.Q(route.Envelope.DocumentKind.Identifier)}, " +
            $"p.{store.Q(route.Envelope.StorageScope.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Id.Identifier)} {predicate.FromAndWhere}",
            predicate.Parameters);
    }

    private static DocumentQuery PredicateQuery(DocumentMutation mutation, PhysicalMutationPlan plan)
    {
        var clauses = mutation.Clauses.ToList();
        if (plan.Action is PhysicalTransitionMutationAction transition)
        {
            var predicate = plan.Predicate.Predicates.Single(item => item.Path == transition.Path);
            clauses.Add(predicate.Operations.Contains(PortableQueryOperation.In)
                ? DocumentQueryClause.Of(DocumentQueryComparison.In(transition.Path, transition.AllowedSourceValues))
                : DocumentQueryClause.AnyOf(transition.AllowedSourceValues
                    .Select(value => DocumentQueryComparison.Equal(transition.Path, value))
                    .ToArray()));
        }
        return new DocumentQuery(mutation.DocumentKind, plan.Predicate.QueryIdentity, clauses);
    }

    private async Task PrepareSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        CancellationToken ct)
    {
        await DropSelectionAsync(connection, transaction, ct);
        await ExecuteAsync(
            connection,
            transaction,
            store.CreateMutationSelectionTable(
                SelectionTableExpression,
                SelectionKind,
                SelectionScope,
                SelectionId),
            [],
            ct);
        var selection = BuildSelectionCommand(mutation, plan, scope);
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {SelectionTableExpression} ({store.Q(SelectionKind)}, {store.Q(SelectionScope)}, {store.Q(SelectionId)}) " +
            selection.CommandText + ";",
            selection.Parameters,
            ct);
    }

    private async Task<long> CountSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken ct)
    {
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
            connection,
            $"SELECT COUNT(*) FROM {SelectionTableExpression};",
            transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task DeleteSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        CancellationToken ct)
    {
        if (route.LinkedIndexStorage is not null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                store.DeleteByMutationSelection(
                    store.Q(route.LinkedIndexStorage.Name.Identifier),
                    "l",
                    SelectionTableExpression,
                    LinkedSelectionJoin(route, "l", "s")),
                [],
                ct);
        }
        await ExecuteAsync(
            connection,
            transaction,
            store.DeleteByMutationSelection(
                store.Q(route.PrimaryStorage.Name.Identifier),
                "p",
                SelectionTableExpression,
                PrimarySelectionJoin(route, "p", "s")),
            [],
            ct);
    }

    private async Task TransitionSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        PhysicalTransitionMutationAction transition,
        CancellationToken ct)
    {
        var parameters = new List<(string Name, object? Value)>
        {
            ("transitionPath", JsonPath(transition.Path)),
            ("transitionJson", JsonValue(transition.TargetValue, transition.Field.ValueKind)),
            ("transitionUpdated", DateTimeOffset.UtcNow.ToUniversalTime().ToString("O"))
        };
        var primaryAssignments = new List<string>
        {
            $"{store.Q(route.Envelope.CanonicalJson.Identifier)} = " +
            store.SetJsonValue(
                $"p.{store.Q(route.Envelope.CanonicalJson.Identifier)}",
                store.P("transitionPath"),
                store.P("transitionJson")),
            $"{store.Q(route.Envelope.Version.Identifier)} = p.{store.Q(route.Envelope.Version.Identifier)} + 1",
            $"{store.Q(RelationalPhysicalStorageColumns.UpdatedUtc)} = {store.P("transitionUpdated")}"
        };
        var primaryProjections = route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage &&
                             column.Definition.Path == transition.Path)
            .ToArray();
        AddProjectionAssignments(primaryAssignments, parameters, primaryProjections, transition);
        await ExecuteAsync(
            connection,
            transaction,
            store.UpdateByMutationSelection(
                store.Q(route.PrimaryStorage.Name.Identifier),
                "p",
                primaryAssignments,
                SelectionTableExpression,
                PrimarySelectionJoin(route, "p", "s")),
            parameters,
            ct);

        if (route.LinkedIndexStorage is null)
            return;
        var linkedAssignments = new List<string>();
        var linkedParameters = new List<(string Name, object? Value)>();
        var linkedProjections = route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage &&
                             column.Definition.Path == transition.Path)
            .ToArray();
        AddProjectionAssignments(linkedAssignments, linkedParameters, linkedProjections, transition);
        if (linkedAssignments.Count == 0)
            return;
        await ExecuteAsync(
            connection,
            transaction,
            store.UpdateByMutationSelection(
                store.Q(route.LinkedIndexStorage.Name.Identifier),
                "l",
                linkedAssignments,
                SelectionTableExpression,
                LinkedSelectionJoin(route, "l", "s")),
            linkedParameters,
            ct);
    }

    private void AddProjectionAssignments(
        List<string> assignments,
        List<(string Name, object? Value)> parameters,
        IReadOnlyList<ExecutableProjectedColumnRoute> projections,
        PhysicalTransitionMutationAction transition)
    {
        foreach (var projection in projections)
        {
            var name = $"transitionProjection{parameters.Count}";
            assignments.Add($"{store.Q(projection.Column.Identifier)} = {store.P(name)}");
            parameters.Add((
                name,
                store.ConvertPhysicalQueryValue(
                    transition.TargetValue,
                    transition.Field.ValueKind,
                    projection.Definition)));
        }
    }

    private async Task<(string Fingerprint, long AffectedCount)?> ReadOperationAsync(
        DbConnection connection,
        DbTransaction transaction,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        CancellationToken ct)
    {
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
            connection,
            $"SELECT request_fingerprint, affected_count FROM {store.Q(RelationalPhysicalStorageColumns.MutationOperationsTable)} " +
            "WHERE manifest_id = @manifestId AND provider_name = @providerName " +
            "AND storage_unit = @storageUnit AND storage_scope = @storageScope AND operation_id = @operationId;",
            transaction);
        AddOperationIdentity(command, mutation, plan, scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private async Task RecordOperationAsync(
        DbConnection connection,
        DbTransaction transaction,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        string fingerprint,
        long affectedCount,
        CancellationToken ct)
    {
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
            connection,
            $"INSERT INTO {store.Q(RelationalPhysicalStorageColumns.MutationOperationsTable)} " +
            "(manifest_id, provider_name, completed_provider_version, storage_unit, storage_scope, operation_id, request_fingerprint, affected_count, completed_utc) " +
            "VALUES (@manifestId, @providerName, @providerVersion, @storageUnit, @storageScope, @operationId, @fingerprint, @affected, @completed);",
            transaction);
        AddOperationIdentity(command, mutation, plan, scope);
        store.AddPhysicalParameter(command, "providerVersion", plan.Predicate.Provider.Version);
        store.AddPhysicalParameter(command, "fingerprint", fingerprint);
        store.AddPhysicalParameter(command, "affected", affectedCount);
        store.AddPhysicalParameter(command, "completed", DateTimeOffset.UtcNow.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private void AddOperationIdentity(
        DbCommand command,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope)
    {
        store.AddPhysicalParameter(command, "manifestId", store.ManifestIdentity);
        store.AddPhysicalParameter(command, "providerName", plan.Predicate.Provider.Name);
        store.AddPhysicalParameter(command, "storageUnit", mutation.DocumentKind);
        store.AddPhysicalParameter(command, "storageScope", scope.StorageKey!);
        store.AddPhysicalParameter(command, "operationId", mutation.OperationId);
    }

    private async Task DropSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken ct) =>
        await ExecuteAsync(
            connection,
            transaction,
            store.DropMutationSelectionTable(SelectionTableExpression),
            [],
            ct);

    private string SelectionTableExpression => store.MutationSelectionTable(SelectionTable);

    private async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken ct)
    {
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, sql, transaction);
        foreach (var (name, value) in parameters)
            store.AddPhysicalParameter(command, name, value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private string PrimarySelectionJoin(ExecutableStorageRoute route, string primaryAlias, string selectionAlias) =>
        store.ExactPhysicalIdentityJoin(
        [
            new(route.Envelope.DocumentKind.Identifier, primaryAlias, SelectionKind, selectionAlias),
            new(route.Envelope.StorageScope.Identifier, primaryAlias, SelectionScope, selectionAlias),
            new(route.Envelope.Id.Identifier, primaryAlias, SelectionId, selectionAlias)
        ]);

    private string LinkedSelectionJoin(ExecutableStorageRoute route, string linkedAlias, string selectionAlias) =>
        store.ExactPhysicalIdentityJoin(
        [
            new(route.LinkedRelationship!.DocumentKind.Identifier, linkedAlias, SelectionKind, selectionAlias),
            new(route.LinkedRelationship.StorageScope.Identifier, linkedAlias, SelectionScope, selectionAlias),
            new(route.LinkedRelationship.DocumentId.Identifier, linkedAlias, SelectionId, selectionAlias)
        ]);

    private static string JsonPath(string stablePath) =>
        "$." + string.Join('.', stablePath.Split('.').Select(segment =>
            $"\"{segment.Replace("\"", "\\\"")}\""));

    private static string JsonValue(string value, IndexValueKind kind) => kind switch
    {
        IndexValueKind.Boolean => JsonSerializer.Serialize(bool.Parse(value)),
        IndexValueKind.Number => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value)
    };
}
