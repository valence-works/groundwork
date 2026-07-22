using System.Collections.Concurrent;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Relational.Documents;

/// <summary>
/// Connection-independent precompilation of the CRUD write-path SQL for one immutable storage route.
/// The save/update/delete/lock-load statements are fully determined by the admitted
/// <see cref="ExecutableStorageRoute"/> and the provider dialect's formatting, so they are rendered once
/// here instead of being re-interpolated inside every write transaction. Instances are immutable and safe
/// for concurrent reads; the projected-column arrays are precomputed so the hot path binds values without
/// re-filtering the route on each save.
/// </summary>
internal sealed class RelationalPhysicalMutationSql
{
    private RelationalPhysicalMutationSql(
        string insertPrimary,
        IReadOnlyList<ExecutableProjectedColumnRoute> primaryProjections,
        string updatePrimaryWithoutExpectedVersion,
        string updatePrimaryWithExpectedVersion,
        string deletePrimaryWithoutExpectedVersion,
        string deletePrimaryWithExpectedVersion,
        string loadForRead,
        string loadForWrite,
        string? linkedInsert,
        IReadOnlyList<ExecutableProjectedColumnRoute> linkedProjections,
        string? linkedDelete,
        string? linkedEvidenceSelect)
    {
        InsertPrimary = insertPrimary;
        PrimaryProjections = primaryProjections;
        UpdatePrimaryWithoutExpectedVersion = updatePrimaryWithoutExpectedVersion;
        UpdatePrimaryWithExpectedVersion = updatePrimaryWithExpectedVersion;
        DeletePrimaryWithoutExpectedVersion = deletePrimaryWithoutExpectedVersion;
        DeletePrimaryWithExpectedVersion = deletePrimaryWithExpectedVersion;
        LoadForRead = loadForRead;
        LoadForWrite = loadForWrite;
        LinkedInsert = linkedInsert;
        LinkedProjections = linkedProjections;
        LinkedDelete = linkedDelete;
        LinkedEvidenceSelect = linkedEvidenceSelect;
    }

    public string InsertPrimary { get; }
    public IReadOnlyList<ExecutableProjectedColumnRoute> PrimaryProjections { get; }
    public string UpdatePrimaryWithoutExpectedVersion { get; }
    public string UpdatePrimaryWithExpectedVersion { get; }
    public string DeletePrimaryWithoutExpectedVersion { get; }
    public string DeletePrimaryWithExpectedVersion { get; }
    public string LoadForRead { get; }
    public string LoadForWrite { get; }
    public string? LinkedInsert { get; }
    public IReadOnlyList<ExecutableProjectedColumnRoute> LinkedProjections { get; }
    public string? LinkedDelete { get; }
    public string? LinkedEvidenceSelect { get; }

    public string UpdatePrimary(bool includeExpectedVersion) =>
        includeExpectedVersion ? UpdatePrimaryWithExpectedVersion : UpdatePrimaryWithoutExpectedVersion;

    public string DeletePrimary(bool includeExpectedVersion) =>
        includeExpectedVersion ? DeletePrimaryWithExpectedVersion : DeletePrimaryWithoutExpectedVersion;

    public string Load(bool lockForWrite) => lockForWrite ? LoadForWrite : LoadForRead;

    public static RelationalPhysicalMutationSql Compile(
        RelationalPhysicalDocumentDialect dialect,
        ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(route);

        string Q(string identifier) => dialect.QuoteIdentifier(identifier);
        string P(string name) => dialect.Parameter(name);

        var primaryProjections = route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.PrimaryStorage)
            .ToArray();

        // INSERT INTO primary (...) VALUES/SELECT (...)
        var insertColumns = RelationalPhysicalEnvelopeRowLayout.PersistedColumns(route)
            .Concat([RelationalPhysicalStorageColumns.CreatedUtc, RelationalPhysicalStorageColumns.UpdatedUtc])
            .Concat(primaryProjections.Select(column => column.Column.Identifier))
            .ToArray();
        var insertParameters = insertColumns.Select((_, index) => P($"v{index}")).ToArray();
        var lookupIdentity = new RelationalPhysicalIdentityPredicatePart[]
        {
            new(route.Discriminator.Column.Identifier, null, P("kind")),
            new(route.ScopeKey.Column.Identifier, null, P("scope")),
            new(route.Envelope.Identity.LookupKey.Identifier, null, P("idLookup"))
        };
        var insertPrimary = dialect.InsertPrimaryIfAbsent(
            route.PrimaryStorage.Name.Identifier,
            insertColumns,
            insertParameters,
            route.PrimaryKey.Columns.Select(column => column.Identifier).ToArray(),
            lookupIdentity);

        // UPDATE primary SET ... WHERE <identity>[ AND version = @expectedVersion]
        var updateColumns = new List<string>
        {
            route.Envelope.SchemaVersion.Identifier,
            route.Envelope.Version.Identifier,
            route.Envelope.CanonicalJson.Identifier,
            RelationalPhysicalStorageColumns.UpdatedUtc
        };
        updateColumns.AddRange(primaryProjections.Select(column => column.Column.Identifier));
        var updateHead =
            $"UPDATE {Q(route.PrimaryStorage.Name.Identifier)} SET " +
            string.Join(", ", updateColumns.Select((column, index) => $"{Q(column)} = {P($"v{index}")}")) +
            " WHERE ";
        var updateWithout = updateHead + IdentityPredicate(dialect, route, includeVersion: false) + ";";
        var updateWith = updateHead + IdentityPredicate(dialect, route, includeVersion: true) + ";";

        // DELETE FROM primary WHERE <identity>[ AND version = @expectedVersion]
        var deleteHead = $"DELETE FROM {Q(route.PrimaryStorage.Name.Identifier)} WHERE ";
        var deleteWithout = deleteHead + IdentityPredicate(dialect, route, includeVersion: false) + ";";
        var deleteWith = deleteHead + IdentityPredicate(dialect, route, includeVersion: true) + ";";

        // SELECT envelope FROM primary WHERE <lookup identity>  (read and locking variants)
        var selectionColumns = string.Join(
            ", ",
            RelationalPhysicalEnvelopeRowLayout.SelectionColumns(route).Select(Q));
        var lookupPredicate = IdentityLookupPredicate(dialect, route);
        var loadForRead =
            $"SELECT {selectionColumns} FROM {Q(route.PrimaryStorage.Name.Identifier)} " +
            $"WHERE {lookupPredicate};";
        var lockingSelect =
            $"SELECT {selectionColumns} FROM " +
            $"{dialect.MutationQuerySource(route.PrimaryStorage.Name.Identifier, "p", indexIdentifier: null)} " +
            $"WHERE {lookupPredicate}";
        var loadForWrite = dialect.CompleteMutationSelection(lockingSelect, includesLinkedStorage: false) + ";";

        // Linked-index maintenance (only present when the route declares linked storage).
        string? linkedInsert = null;
        string? linkedDelete = null;
        string? linkedEvidenceSelect = null;
        IReadOnlyList<ExecutableProjectedColumnRoute> linkedProjections = [];
        if (route.LinkedIndexStorage is not null)
        {
            var relationship = route.LinkedRelationship!;
            linkedProjections = route.ProjectedColumns
                .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
                .ToArray();
            var linkedColumns = new[]
            {
                relationship.DocumentKind.Identifier,
                relationship.StorageScope.Identifier,
                relationship.Identity.OriginalId.Identifier,
                relationship.Identity.ComparisonKey.Identifier,
                relationship.Identity.LookupKey.Identifier
            }.Concat(linkedProjections.Select(column => column.Column.Identifier)).ToArray();
            linkedInsert =
                $"INSERT INTO {Q(route.LinkedIndexStorage.Name.Identifier)} " +
                $"({string.Join(", ", linkedColumns.Select(Q))}) " +
                $"VALUES ({string.Join(", ", linkedColumns.Select((_, index) => P($"v{index}")))});";
            linkedDelete =
                $"DELETE FROM {Q(route.LinkedIndexStorage.Name.Identifier)} WHERE " +
                dialect.ExactIdentityPredicate(
                [
                    new(relationship.DocumentKind.Identifier, null, P("kind")),
                    new(relationship.StorageScope.Identifier, null, P("scope")),
                    new(relationship.Identity.LookupKey.Identifier, null, P("idLookup")),
                    new(relationship.Identity.ComparisonKey.Identifier, null, P("idComparison"))
                ]) + ";";
            linkedEvidenceSelect =
                $"SELECT {Q(relationship.DocumentId.Identifier)}, {Q(relationship.Identity.ComparisonKey.Identifier)} " +
                $"FROM {Q(route.LinkedIndexStorage.Name.Identifier)} WHERE " +
                dialect.ExactIdentityPredicate(
                [
                    new(relationship.DocumentKind.Identifier, null, P("kind")),
                    new(relationship.StorageScope.Identifier, null, P("scope")),
                    new(relationship.Identity.LookupKey.Identifier, null, P("idLookup"))
                ]) + ";";
        }

        return new RelationalPhysicalMutationSql(
            insertPrimary,
            primaryProjections,
            updateWithout,
            updateWith,
            deleteWithout,
            deleteWith,
            loadForRead,
            loadForWrite,
            linkedInsert,
            linkedProjections,
            linkedDelete,
            linkedEvidenceSelect);
    }

    private static string IdentityPredicate(
        RelationalPhysicalDocumentDialect dialect,
        ExecutableStorageRoute route,
        bool includeVersion) =>
        dialect.ExactIdentityPredicate(
        [
            new(route.Discriminator.Column.Identifier, null, dialect.Parameter("kind")),
            new(route.ScopeKey.Column.Identifier, null, dialect.Parameter("scope")),
            new(route.Envelope.Identity.LookupKey.Identifier, null, dialect.Parameter("idLookup")),
            new(route.Envelope.Identity.ComparisonKey.Identifier, null, dialect.Parameter("idComparison"))
        ]) +
        (includeVersion
            ? $" AND {dialect.QuoteIdentifier(route.Envelope.Version.Identifier)} = {dialect.Parameter("expectedVersion")}"
            : string.Empty);

    private static string IdentityLookupPredicate(
        RelationalPhysicalDocumentDialect dialect,
        ExecutableStorageRoute route) =>
        dialect.ExactIdentityPredicate(
        [
            new(route.Discriminator.Column.Identifier, null, dialect.Parameter("kind")),
            new(route.ScopeKey.Column.Identifier, null, dialect.Parameter("scope")),
            new(route.Envelope.Identity.LookupKey.Identifier, null, dialect.Parameter("idLookup"))
        ]);
}

/// <summary>
/// Process-wide memoization of <see cref="RelationalPhysicalMutationSql"/> keyed by the provider dialect's
/// mutation-SQL discriminator and the immutable route. Rendering the write-path SQL is a pure function of
/// those two inputs, so a route admitted by many stores compiles its statements once. Keys are careful:
/// the dialect discriminator separates providers (and any provider that lets instance state influence
/// mutation SQL), and the value-equal <see cref="ExecutableStorageRoute"/> separates distinct schemas so
/// no rendered SQL leaks across differently shaped units. Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// so concurrent saves resolving the same route are safe.
/// </summary>
internal sealed class RelationalPhysicalMutationSqlCache
{
    public static RelationalPhysicalMutationSqlCache Shared { get; } = new();

    private readonly ConcurrentDictionary<CacheKey, RelationalPhysicalMutationSql> entries = new();

    public RelationalPhysicalMutationSql GetOrCompile(
        RelationalPhysicalDocumentDialect dialect,
        ExecutableStorageRoute route)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(route);
        return entries.GetOrAdd(
            new CacheKey(dialect.MutationSqlCacheDiscriminator, route),
            static (key, state) => RelationalPhysicalMutationSql.Compile(state.Dialect, key.Route),
            (Dialect: dialect, Route: route));
    }

    internal int Count => entries.Count;

    private readonly record struct CacheKey(string Discriminator, ExecutableStorageRoute Route);
}
