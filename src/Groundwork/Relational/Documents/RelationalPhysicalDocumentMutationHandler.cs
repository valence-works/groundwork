using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;

namespace Groundwork.Relational.Documents;

/// <summary>Observable transaction boundaries used by provider conformance tests.</summary>
internal enum RelationalPhysicalMutationExecutionPoint
{
    BeforeSelection,
    AfterCandidateDiscovery,
    BeforePrimaryLocks,
    AfterPrimaryLocks,
    AfterLinkedLocks,
    BeforeRowLockCommand,
    AfterSelection,
    BeforeCommit,
    AfterCommitBeforeAcknowledgement
}

internal delegate ValueTask RelationalPhysicalMutationInterceptor(
    RelationalPhysicalMutationExecutionPoint point,
    DbConnection connection,
    DbTransaction transaction,
    CancellationToken cancellationToken);

internal delegate ValueTask RelationalPhysicalMutationSelectionObserver(
    string identity,
    RelationalPhysicalQueryCommand command,
    long? preparedRestrictionRowCount);

internal sealed record RelationalPhysicalMutationSelectionStage(
    PhysicalDocumentMutationCommandKind Kind,
    string Identity,
    RelationalPhysicalQueryCommand Command,
    IReadOnlyList<ExecutableStorageObjectRole> Selectors);

/// <summary>
/// Relational executor for one certified bounded mutation source. Selection is rendered by the
/// bounded-query SQL builder into a transaction-local identity table before any row is changed.
/// </summary>
internal sealed class RelationalPhysicalDocumentMutationHandler : IPhysicalDocumentMutationHandler
{
    private const string DiscoveryTable = "groundwork_bounded_mutation_discovery";
    private const string CandidateTable = "groundwork_bounded_mutation_candidates";
    private const string ProvisionalTable = "groundwork_bounded_mutation_provisional";
    private const string SelectionTable = "groundwork_bounded_mutation_selection";
    private const string SelectionKind = "document_kind";
    private const string SelectionScope = "storage_scope";
    private const string SelectionId = "document_id";
    private const string SelectionComparison = "document_id_comparison_key";
    private const string SelectionLookup = "document_id_lookup_key";
    private const string SelectionVersion = "document_version";
    private const string SelectionIncarnation = "document_incarnation";

    private readonly RelationalPhysicalDocumentStore store;
    private readonly RelationalPhysicalDocumentQueryHandler predicateBuilder;
    private readonly RelationalPhysicalMutationInterceptor? intercept;
    private readonly RelationalPhysicalMutationSelectionObserver? selectionObserver;

    internal RelationalPhysicalDocumentMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        RelationalPhysicalDocumentStore store,
        IReadOnlyList<PhysicalMutationHandlerCertification> certifications)
        : this(identity, source, store, certifications, null, null)
    {
    }

    internal RelationalPhysicalDocumentMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        RelationalPhysicalDocumentStore store,
        IReadOnlyList<PhysicalMutationHandlerCertification> certifications,
        RelationalPhysicalMutationInterceptor? intercept,
        RelationalPhysicalMutationSelectionObserver? selectionObserver = null)
    {
        Identity = string.IsNullOrWhiteSpace(identity)
            ? throw new ArgumentException("A handler identity is required.", nameof(identity))
            : identity;
        Source = source;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        Certifications = Array.AsReadOnly(
            (certifications ?? throw new ArgumentNullException(nameof(certifications))).ToArray());
        this.intercept = intercept;
        this.selectionObserver = selectionObserver;
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
        predicateBuilder.ValidateDocumentIdentityValues(mutation.Clauses);
        var scope = store.ResolveMutationScope(mutation.DocumentKind);
        if (scope.AcrossScopes || scope.StorageKey is null)
            throw new InvalidOperationException("Bounded mutations require one route-derived target scope.");
        var fingerprint = BoundedMutationRequestFingerprint.Create(mutation, plan, scope.StorageKey);
        var completed = false;
        DbConnection? executionConnection = null;
        DbTransaction? executionTransaction = null;
        return await store.ExecutePhysicalMutationAsync(
            async (connection, transaction, ct) =>
            {
                executionConnection = connection;
                executionTransaction = transaction;
                await store.AcquireMutationOperationLockAsync(
                    connection,
                    transaction,
                    OperationLock(mutation, plan, scope),
                    ct);
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

                if (intercept is not null)
                    await intercept(RelationalPhysicalMutationExecutionPoint.BeforeSelection, connection, transaction, ct);
                await PrepareSelectionAsync(connection, transaction, mutation, plan, scope, ct);
                var affected = await CountSelectionAsync(connection, transaction, ct);
                if (intercept is not null)
                    await intercept(RelationalPhysicalMutationExecutionPoint.AfterSelection, connection, transaction, ct);
                if (plan.Action is PhysicalDeleteMutationAction)
                    await DeleteSelectionAsync(
                        connection,
                        transaction,
                        store.GetRoute(mutation.DocumentKind),
                        affected,
                        ct);
                else
                    await TransitionSelectionAsync(
                        connection,
                        transaction,
                        store.GetRoute(mutation.DocumentKind),
                        (PhysicalTransitionMutationAction)plan.Action,
                        affected,
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
                await DropSelectionTablesAsync(connection, transaction, ct);
                completed = true;
                return BoundedMutationResult.Completed(affected);
            },
            beforeCommit: intercept is null
                ? null
                : ct => completed
                    ? intercept(
                        RelationalPhysicalMutationExecutionPoint.BeforeCommit,
                        executionConnection!,
                        executionTransaction!,
                        ct)
                    : ValueTask.CompletedTask,
            afterCommitBeforeAcknowledgement: intercept is null
                ? null
                : ct => completed
                    ? intercept(
                        RelationalPhysicalMutationExecutionPoint.AfterCommitBeforeAcknowledgement,
                        executionConnection!,
                        executionTransaction!,
                        ct)
                    : ValueTask.CompletedTask,
            cancellationToken);
    }

    internal static PhysicalMutationHandlerCertification Certify(PhysicalMutationPlan plan)
    {
        var primary = new PhysicalMutationSelectorCertification(
            ExecutableStorageObjectRole.PrimaryStorage,
            plan.Predicate.PrimaryObject,
            plan.Predicate.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary
                ? null
                : plan.Predicate.IndexName,
            new Dictionary<string, string>());
        var linked = plan.Predicate.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ? new PhysicalMutationSelectorCertification(
                ExecutableStorageObjectRole.LinkedIndexStorage,
                plan.Predicate.LookupObject,
                plan.Predicate.IndexName!,
                new Dictionary<string, string>())
            : null;
        return new PhysicalMutationHandlerCertification(
            plan,
            evidenceStages:
            [
                new(
                    PhysicalDocumentMutationCommandKind.CandidateDiscovery,
                    PhysicalDocumentMutationCommandIdentities.CandidateDiscovery,
                    linked is null ? [primary] : [linked]),
                new(
                    PhysicalDocumentMutationCommandKind.PredicateRecheck,
                    PhysicalDocumentMutationCommandIdentities.PredicateRecheck,
                    linked is null ? [primary] : [primary, linked])
            ]);
    }

    internal RelationalPhysicalQueryCommand BuildSelectionCommand(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope,
        string? restrictionTable = null,
        bool identityOnly = false)
    {
        var query = PredicateQuery(mutation, plan);
        var linkedIdentityOnly = identityOnly && plan.Predicate.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary;
        var predicate = predicateBuilder.BuildPredicate(
            query,
            plan.Predicate,
            scope,
            linkedIdentityOnly: linkedIdentityOnly);
        var route = store.GetRoute(mutation.DocumentKind);
        var restriction = restrictionTable is null
            ? string.Empty
            : $" AND EXISTS (SELECT 1 FROM {restrictionTable} AS s WHERE " +
              PrimarySelectionJoin(route, "p", "s", includeIncarnation: true) + ")";
        var identity = linkedIdentityOnly
            ? $"l.{store.Q(route.LinkedRelationship!.DocumentKind.Identifier)}, " +
              $"l.{store.Q(route.LinkedRelationship.StorageScope.Identifier)}, " +
              $"l.{store.Q(route.LinkedRelationship.DocumentId.Identifier)}, " +
              $"l.{store.Q(route.LinkedRelationship.Identity.ComparisonKey.Identifier)}, " +
              $"l.{store.Q(route.LinkedRelationship.Identity.LookupKey.Identifier)}"
            : $"p.{store.Q(route.Envelope.DocumentKind.Identifier)}, " +
              $"p.{store.Q(route.Envelope.StorageScope.Identifier)}, " +
              $"p.{store.Q(route.Envelope.Id.Identifier)}, " +
              $"p.{store.Q(route.Envelope.Identity.ComparisonKey.Identifier)}, " +
              $"p.{store.Q(route.Envelope.Identity.LookupKey.Identifier)}";
        var state = identityOnly
            ? "0, ''"
            : $"p.{store.Q(route.Envelope.Version.Identifier)}, " +
              $"p.{store.Q(RelationalPhysicalStorageColumns.CreatedUtc)}";
        return new RelationalPhysicalQueryCommand(
            $"SELECT {identity}, {state} {predicate.FromAndWhere}{restriction}",
            predicate.Parameters,
            predicate.PredicateFieldIdentifiers);
    }

    internal IReadOnlyList<RelationalPhysicalMutationSelectionStage> BuildSelectionStages(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope)
    {
        var certification = Certifications.Single(candidate => candidate.Certifies(plan));
        return certification.EvidenceStages.Select(stage => new RelationalPhysicalMutationSelectionStage(
            stage.Kind,
            stage.Identity,
            stage.Kind switch
            {
                PhysicalDocumentMutationCommandKind.CandidateDiscovery =>
                    BuildSelectionCommand(mutation, plan, scope, identityOnly: true),
                PhysicalDocumentMutationCommandKind.PredicateRecheck =>
                    BuildSelectionCommand(mutation, plan, scope, ProvisionalTableExpression),
                _ => throw new InvalidOperationException(
                    $"Relational mutation handler '{Identity}' has unsupported evidence stage '{stage.Kind}'.")
            },
            stage.Selectors.Select(selector => selector.Target).ToArray())).ToArray();
    }

    internal async Task PrepareSelectionStagesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await DropSelectionTablesAsync(connection, transaction, cancellationToken);
        await CreateSelectionTableAsync(connection, transaction, DiscoveryTableExpression, cancellationToken);
        await CreateSelectionTableAsync(connection, transaction, CandidateTableExpression, cancellationToken);
        await CreateSelectionTableAsync(connection, transaction, ProvisionalTableExpression, cancellationToken);
        await CreateSelectionTableAsync(connection, transaction, SelectionTableExpression, cancellationToken);
    }

    internal async Task PopulatePredicateRecheckInputsAsync(
        DbConnection connection,
        DbTransaction transaction,
        DocumentMutation mutation,
        RelationalPhysicalMutationSelectionStage candidateDiscovery,
        CancellationToken cancellationToken)
    {
        var route = store.GetRoute(mutation.DocumentKind);
        await InsertSelectionAsync(
            connection,
            transaction,
            DiscoveryTableExpression,
            candidateDiscovery,
            cancellationToken);
        await ThrowIfDiscoveryIdentityCollisionAsync(connection, transaction, route, cancellationToken);
        await InterceptAsync(
            RelationalPhysicalMutationExecutionPoint.AfterCandidateDiscovery,
            connection,
            transaction,
            cancellationToken);
        await InsertCandidatePrimaryRowsAsync(connection, transaction, route, cancellationToken);
        await InterceptAsync(
            RelationalPhysicalMutationExecutionPoint.BeforePrimaryLocks,
            connection,
            transaction,
            cancellationToken);
        await LockRowsAsync(
            connection,
            transaction,
            route,
            CandidateTableExpression,
            linked: false,
            cancellationToken);
        await InterceptAsync(
            RelationalPhysicalMutationExecutionPoint.AfterPrimaryLocks,
            connection,
            transaction,
            cancellationToken);

        await InsertCurrentPrimaryRowsAsync(connection, transaction, route, cancellationToken);
        if (route.LinkedIndexStorage is not null)
        {
            await LockRowsAsync(
                connection,
                transaction,
                route,
                ProvisionalTableExpression,
                linked: true,
                cancellationToken);
        }
        await InterceptAsync(
            RelationalPhysicalMutationExecutionPoint.AfterLinkedLocks,
            connection,
            transaction,
            cancellationToken);
    }

    internal Task<long> CountPreparedRestrictionRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        CountSelectionAsync(connection, transaction, ProvisionalTableExpression, cancellationToken);

    internal async Task CleanupSelectionStagesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        await DropSelectionTablesAsync(connection, transaction, cancellationToken);

    internal RelationalPhysicalQueryCommand BuildOperationReadCommand(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope) => new(
        $"SELECT request_fingerprint, affected_count FROM {store.Q(RelationalPhysicalStorageColumns.MutationOperationsTable)} " +
        $"WHERE {store.MutationOperationIdentityPredicate(OperationIdentityPredicate())};",
        OperationIdentity(mutation, plan, scope),
        OperationIdentityPredicate().Select(part => part.ColumnIdentifier).ToArray());

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
        // Server providers execute this under their default READ COMMITTED isolation. Discovery is
        // deliberately a completed statement before primary state is read: linked-index discovery
        // therefore cannot retain a mutation row lock while waiting for a primary row. Candidates
        // are then locked primary-first in stable identity order, followed by linked rows in the
        // same order. The final predicate recheck binds the selected version and incarnation used
        // by DML; completion of that recheck is the mutation's linearization point.
        var selectionStages = BuildSelectionStages(mutation, plan, scope);
        await PrepareSelectionStagesAsync(connection, transaction, ct);
        await PopulatePredicateRecheckInputsAsync(
            connection,
            transaction,
            mutation,
            selectionStages[0],
            ct);

        await InsertSelectionAsync(
            connection,
            transaction,
            SelectionTableExpression,
            selectionStages[1],
            ct);
    }

    private async Task CreateSelectionTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableExpression,
        CancellationToken ct) =>
        await ExecuteAsync(
            connection,
            transaction,
            store.CreateMutationSelectionTable(
                tableExpression,
                SelectionKind,
                SelectionScope,
                SelectionId,
                SelectionComparison,
                SelectionLookup,
                SelectionVersion,
                SelectionIncarnation),
            [],
            ct);

    private async Task InsertSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableExpression,
        RelationalPhysicalMutationSelectionStage selection,
        CancellationToken ct)
    {
        if (selectionObserver is not null)
        {
            long? preparedRestrictionRowCount =
                selection.Kind == PhysicalDocumentMutationCommandKind.PredicateRecheck
                    ? await CountPreparedRestrictionRowsAsync(connection, transaction, ct)
                    : null;
            await selectionObserver(selection.Identity, selection.Command, preparedRestrictionRowCount);
        }
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {tableExpression} ({SelectionColumns}) {selection.Command.CommandText};",
            selection.Command.Parameters,
            ct);
    }

    private async Task InsertCurrentPrimaryRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        CancellationToken ct) =>
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {ProvisionalTableExpression} ({SelectionColumns}) " +
            $"SELECT p.{store.Q(route.Envelope.DocumentKind.Identifier)}, " +
            $"p.{store.Q(route.Envelope.StorageScope.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Id.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Identity.ComparisonKey.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Identity.LookupKey.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Version.Identifier)}, " +
            $"p.{store.Q(RelationalPhysicalStorageColumns.CreatedUtc)} " +
            $"FROM {store.Q(route.PrimaryStorage.Name.Identifier)} AS p " +
            $"WHERE EXISTS (SELECT 1 FROM {CandidateTableExpression} AS s WHERE " +
            PrimarySelectionJoin(route, "p", "s", includeVersion: false, includeIncarnation: true) + ");",
            [],
            ct);

    private async Task InsertCandidatePrimaryRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        CancellationToken ct) =>
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {CandidateTableExpression} ({SelectionColumns}) " +
            $"SELECT p.{store.Q(route.Envelope.DocumentKind.Identifier)}, " +
            $"p.{store.Q(route.Envelope.StorageScope.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Id.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Identity.ComparisonKey.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Identity.LookupKey.Identifier)}, " +
            $"p.{store.Q(route.Envelope.Version.Identifier)}, " +
            $"p.{store.Q(RelationalPhysicalStorageColumns.CreatedUtc)} " +
            $"FROM {store.Q(route.PrimaryStorage.Name.Identifier)} AS p " +
            $"WHERE EXISTS (SELECT 1 FROM {DiscoveryTableExpression} AS s WHERE " +
            PrimarySelectionJoin(route, "p", "s", includeVersion: false) + ");",
            [],
            ct);

    private async Task ThrowIfDiscoveryIdentityCollisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        CancellationToken ct)
    {
        if (route.LinkedRelationship is null)
            return;

        var join = store.ExactPhysicalIdentityJoin(
        [
            new(route.Envelope.DocumentKind.Identifier, "p", SelectionKind, "s"),
            new(route.Envelope.StorageScope.Identifier, "p", SelectionScope, "s"),
            new(route.Envelope.Identity.LookupKey.Identifier, "p", SelectionLookup, "s")
        ]);
        var sql = store.ApplyFirst(
            $"SELECT s.{store.Q(SelectionId)}, p.{store.Q(route.Envelope.Id.Identifier)}, " +
            $"s.{store.Q(SelectionLookup)} FROM {DiscoveryTableExpression} AS s " +
            $"JOIN {store.Q(route.PrimaryStorage.Name.Identifier)} AS p ON {join} " +
            $"WHERE p.{store.Q(route.Envelope.Identity.ComparisonKey.Identifier)} <> " +
            $"s.{store.Q(SelectionComparison)}");
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
            connection,
            sql,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;

        throw new DocumentIdentityLookupCollisionException(
            route.Discriminator.Value,
            reader.GetString(0),
            reader.GetString(1),
            store.ReadDocumentIdentityLookup(reader, 2));
    }

    private async Task LockRowsAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        string selectionTableExpression,
        bool linked,
        CancellationToken ct)
    {
        var table = linked ? route.LinkedIndexStorage!.Name.Identifier : route.PrimaryStorage.Name.Identifier;
        var join = linked
            ? LinkedSelectionJoin(route, "p", "s")
            : PrimarySelectionJoin(route, "p", "s", includeVersion: false);
        await InterceptAsync(RelationalPhysicalMutationExecutionPoint.BeforeRowLockCommand, connection, transaction, ct);
        await ExecuteAsync(
            connection,
            transaction,
            store.LockByMutationSelection(
                table,
                selectionTableExpression,
                join,
                SelectionKind,
                SelectionScope,
                SelectionId),
            [],
            ct);
    }

    private async ValueTask InterceptAsync(
        RelationalPhysicalMutationExecutionPoint point,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken ct)
    {
        if (intercept is not null)
            await intercept(point, connection, transaction, ct);
    }

    private Task<long> CountSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken ct) =>
        CountSelectionAsync(connection, transaction, SelectionTableExpression, ct);

    private async Task<long> CountSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableExpression,
        CancellationToken ct)
    {
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
            connection,
            $"SELECT COUNT(*) FROM {tableExpression};",
            transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task DeleteSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        long expectedAffected,
        CancellationToken ct)
    {
        if (route.LinkedIndexStorage is not null)
        {
            var linkedAffected = await ExecuteAsync(
                connection,
                transaction,
                store.DeleteByMutationSelection(
                    store.Q(route.LinkedIndexStorage.Name.Identifier),
                    "l",
                    SelectionTableExpression,
                    LinkedSelectionJoin(route, "l", "s")),
                [],
                ct);
            EnsureAffectedCount("linked delete", linkedAffected, expectedAffected);
        }
        var primaryAffected = await ExecuteAsync(
            connection,
            transaction,
            store.DeleteByMutationSelection(
                store.Q(route.PrimaryStorage.Name.Identifier),
                "p",
                SelectionTableExpression,
                PrimarySelectionJoin(route, "p", "s")),
            [],
            ct);
        EnsureAffectedCount("primary delete", primaryAffected, expectedAffected);
    }

    private async Task TransitionSelectionAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        PhysicalTransitionMutationAction transition,
        long expectedAffected,
        CancellationToken ct)
    {
        var parameters = new List<(string Name, object? Value)>
        {
            ("transitionPath", store.ConvertMutationJsonPath(transition.Path)),
            ("transitionJson", store.ConvertMutationJsonValue(transition.TargetValue, transition.Field.ValueKind)),
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
        var primaryAffected = await ExecuteAsync(
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
        EnsureAffectedCount("primary transition", primaryAffected, expectedAffected);

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
        var linkedAffected = await ExecuteAsync(
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
        EnsureAffectedCount("linked transition", linkedAffected, expectedAffected);
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
        var rendered = BuildOperationReadCommand(mutation, plan, scope);
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(
            connection,
            rendered.CommandText,
            transaction);
        AddParameters(command, rendered.Parameters);
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
        store.AddPhysicalParameter(command, "completed", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(ct);
    }

    private void AddOperationIdentity(
        DbCommand command,
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope)
    {
        AddParameters(command, OperationIdentity(mutation, plan, scope));
    }

    private IReadOnlyList<RelationalPhysicalIdentityPredicatePart> OperationIdentityPredicate() =>
    [
        new("manifest_id", null, store.P("manifestId")),
        new("provider_name", null, store.P("providerName")),
        new("storage_unit", null, store.P("storageUnit")),
        new("storage_scope", null, store.P("storageScope")),
        new("operation_id", null, store.P("operationId"))
    ];

    private IReadOnlyList<(string Name, object? Value)> OperationIdentity(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope) =>
    [
        ("manifestId", store.ManifestIdentity),
        ("providerName", plan.Predicate.Provider.Name),
        ("storageUnit", mutation.DocumentKind),
        ("storageScope", scope.StorageKey!),
        ("operationId", mutation.OperationId)
    ];

    private void AddParameters(DbCommand command, IReadOnlyList<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            store.AddPhysicalParameter(command, name, value);
    }

    private string OperationLock(
        DocumentMutation mutation,
        PhysicalMutationPlan plan,
        DocumentScopeSelection scope)
    {
        var identity = string.Concat(
            Encode(store.ManifestIdentity),
            Encode(plan.Predicate.Provider.Name),
            Encode(mutation.DocumentKind),
            Encode(scope.StorageKey!),
            Encode(mutation.OperationId));
        return "groundwork:mutation:" +
               Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string Encode(string value) => $"{value.Length}:{value}";

    private async Task DropSelectionTablesAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            transaction,
            store.DropMutationSelectionTable(SelectionTableExpression),
            [],
            ct);
        await ExecuteAsync(
            connection,
            transaction,
            store.DropMutationSelectionTable(ProvisionalTableExpression),
            [],
            ct);
        await ExecuteAsync(
            connection,
            transaction,
            store.DropMutationSelectionTable(CandidateTableExpression),
            [],
            ct);
        await ExecuteAsync(
            connection,
            transaction,
            store.DropMutationSelectionTable(DiscoveryTableExpression),
            [],
            ct);
    }

    private string DiscoveryTableExpression => store.MutationSelectionTable(DiscoveryTable);
    private string CandidateTableExpression => store.MutationSelectionTable(CandidateTable);
    private string ProvisionalTableExpression => store.MutationSelectionTable(ProvisionalTable);
    private string SelectionTableExpression => store.MutationSelectionTable(SelectionTable);
    private string SelectionColumns =>
        $"{store.Q(SelectionKind)}, {store.Q(SelectionScope)}, {store.Q(SelectionId)}, " +
        $"{store.Q(SelectionComparison)}, {store.Q(SelectionLookup)}, " +
        $"{store.Q(SelectionVersion)}, {store.Q(SelectionIncarnation)}";

    private async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken ct)
    {
        await using var command = RelationalPhysicalDocumentStore.CreatePhysicalCommand(connection, sql, transaction);
        foreach (var (name, value) in parameters)
            store.AddPhysicalParameter(command, name, value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private string PrimarySelectionJoin(
        ExecutableStorageRoute route,
        string primaryAlias,
        string selectionAlias,
        bool includeVersion = true,
        bool includeIncarnation = false) =>
        store.ExactPhysicalIdentityJoin(
        [
            new(route.Envelope.DocumentKind.Identifier, primaryAlias, SelectionKind, selectionAlias),
            new(route.Envelope.StorageScope.Identifier, primaryAlias, SelectionScope, selectionAlias),
            new(route.Envelope.Identity.LookupKey.Identifier, primaryAlias, SelectionLookup, selectionAlias),
            new(route.Envelope.Identity.ComparisonKey.Identifier, primaryAlias, SelectionComparison, selectionAlias)
        ]) +
        (includeVersion
            ? $" AND {primaryAlias}.{store.Q(route.Envelope.Version.Identifier)} = {selectionAlias}.{store.Q(SelectionVersion)}"
            : string.Empty) +
        (includeIncarnation
            ? $" AND {primaryAlias}.{store.Q(RelationalPhysicalStorageColumns.CreatedUtc)} = {selectionAlias}.{store.Q(SelectionIncarnation)}"
            : string.Empty);

    private string LinkedSelectionJoin(ExecutableStorageRoute route, string linkedAlias, string selectionAlias) =>
        store.ExactPhysicalIdentityJoin(
        [
            new(route.LinkedRelationship!.DocumentKind.Identifier, linkedAlias, SelectionKind, selectionAlias),
            new(route.LinkedRelationship.StorageScope.Identifier, linkedAlias, SelectionScope, selectionAlias),
            new(route.LinkedRelationship.Identity.LookupKey.Identifier, linkedAlias, SelectionLookup, selectionAlias),
            new(route.LinkedRelationship.Identity.ComparisonKey.Identifier, linkedAlias, SelectionComparison, selectionAlias)
        ]);

    private static void EnsureAffectedCount(string operation, int actual, long expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Bounded mutation {operation} affected {actual} rows after selecting {expected}; the transaction was rolled back.");
        }
    }

}
