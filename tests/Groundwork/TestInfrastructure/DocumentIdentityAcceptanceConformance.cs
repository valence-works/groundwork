using System.Text.Json;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.TestInfrastructure;

public abstract class DocumentIdentityAcceptanceConformance
{
    protected abstract Task<DocumentIdentityAcceptanceFixture> CreateIdentityFixtureAsync(
        PhysicalStorageForm form = PhysicalStorageForm.PhysicalEntityTable,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
        DocumentIdentityAcceptanceSurface surface = DocumentIdentityAcceptanceSurface.Exact);

    protected virtual Task PrepareMutationNativeEvidenceAsync(DocumentIdentityAcceptanceFixture fixture) =>
        Task.CompletedTask;

    [Fact]
    public async Task Exact_query_matches_an_equivalent_unicode_identity_and_returns_the_authoritative_original()
    {
        await using var fixture = await CreateIdentityFixtureAsync();
        const string authoritative = "metric-\U00010428-\u00e9";
        const string equivalent = "METRIC-\U00010400-\u00c9";
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(authoritative, "pending"))).Status);
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save("unrelated", "pending"))).Status);

        var result = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, equivalent)));

        Assert.Equal(authoritative, Assert.Single(result.Documents).Id);
    }

    [Fact]
    public async Task Ordinal_identity_keeps_Foo_and_foo_distinct()
    {
        await using var fixture = await CreateIdentityFixtureAsync(stringCasePolicy: StringIdentityCasePolicy.Ordinal);
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save("Foo", "upper"))).Status);
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save("foo", "lower"))).Status);

        var upper = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, "Foo")));
        var lower = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, "foo")));

        Assert.Equal("Foo", Assert.Single(upper.Documents).Id);
        Assert.Equal("foo", Assert.Single(lower.Documents).Id);
    }

    [Fact]
    public async Task Unicode_ordinal_ignore_case_keeps_expansions_and_normalization_forms_distinct()
    {
        await using var fixture = await CreateIdentityFixtureAsync();
        string[] distinctIds = ["straße", "STRASSE", "caf\u00e9", "cafe\u0301"];
        foreach (var id in distinctIds)
        {
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(id, "pending"))).Status);
        }

        foreach (var id in distinctIds)
        {
            var result = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
                DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, id)));
            Assert.Equal(id, Assert.Single(result.Documents).Id);
        }
    }

    [Fact]
    public async Task In_and_not_equal_use_unicode_identity_semantics_and_hydrate_originals()
    {
        await using var fixture = await CreateIdentityFixtureAsync();
        string[] authoritative = ["alpha-𐐨", "beta-é", "other"];
        foreach (var id in authoritative)
        {
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(id, "pending"))).Status);
        }

        var inResult = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.In(
                PhysicalDocumentFieldPaths.Id,
                ["ALPHA-𐐀", "BETA-É"])));
        var notEqualResult = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.NotEqual(PhysicalDocumentFieldPaths.Id, "ALPHA-𐐀")));

        Assert.Equal(authoritative[..2], inResult.Documents.Select(document => document.Id).Order(StringComparer.Ordinal));
        Assert.Equal(authoritative[1..], notEqualResult.Documents.Select(document => document.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Ordered_identity_range_uses_comparison_keys_and_returns_originals()
    {
        await using var fixture = await CreateIdentityFixtureAsync(surface: DocumentIdentityAcceptanceSurface.OrderedRange);
        string[] authoritative = ["alpha", "Bravo-𐐨", "charlie", "Prefix-𐐨-one", "prefix-𐐨-two", "zulu"];
        foreach (var id in authoritative)
        {
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(id, "pending"))).Status);
        }

        var range = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.RangeQuery(
            DocumentQueryComparison.GreaterThan(PhysicalDocumentFieldPaths.Id, "BRAVO-𐐀")));

        Assert.Equal(authoritative[2..], range.Documents.Select(document => document.Id));
    }

    [Fact]
    public async Task Identity_starts_with_uses_comparison_key_prefix_and_returns_originals()
    {
        await using var fixture = await CreateIdentityFixtureAsync(surface: DocumentIdentityAcceptanceSurface.StartsWith);
        string[] authoritative = ["alpha", "Prefix-𐐨-one", "prefix-𐐨-two", "zulu"];
        foreach (var id in authoritative)
        {
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(id, "pending"))).Status);
        }

        var prefix = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.PrefixQuery(
            DocumentQueryComparison.StartsWith(PhysicalDocumentFieldPaths.Id, "PREFIX-𐐀-")));

        Assert.Equal(authoritative[1..3], prefix.Documents.Select(document => document.Id));
    }

    [Fact]
    public async Task Empty_identity_starts_with_matches_every_identity_and_returns_originals()
    {
        await using var fixture = await CreateIdentityFixtureAsync(surface: DocumentIdentityAcceptanceSurface.StartsWith);
        string[] authoritative = ["", "alpha", "Prefix-𐐨-one", "zulu"];
        foreach (var id in authoritative)
        {
            Assert.Equal(
                DocumentStoreWriteStatus.Saved,
                (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(id, "pending"))).Status);
        }

        var prefix = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.PrefixQuery(
            DocumentQueryComparison.StartsWith(PhysicalDocumentFieldPaths.Id, string.Empty)));

        Assert.Equal(authoritative, prefix.Documents.Select(document => document.Id));
    }

    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    public async Task Linked_identity_query_hydrates_the_authoritative_original_from_primary_storage(
        PhysicalStorageForm form)
    {
        await using var fixture = await CreateIdentityFixtureAsync(form);
        const string authoritative = "linked-𐐨-é";
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(authoritative, "pending"))).Status);

        var result = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, "LINKED-𐐀-É")));

        Assert.Equal(authoritative, Assert.Single(result.Documents).Id);
        Assert.NotNull(fixture.Route.LinkedIndexStorage);
    }

    [Fact]
    public async Task Bounded_transition_delete_replay_and_case_distinct_operation_ids_preserve_identity_semantics()
    {
        await using var fixture = await CreateIdentityFixtureAsync(surface: DocumentIdentityAcceptanceSurface.Mutation);
        var mutations = Assert.IsAssignableFrom<IBoundedDocumentMutationStore>(fixture.Mutations);
        await SaveAsync("transition-a", "pending");
        await SaveAsync("transition-b", "pending");

        var transition = new DocumentMutation(
            DocumentIdentityAcceptanceModel.DocumentKind,
            DocumentIdentityAcceptanceModel.TransitionMutationIdentity,
            "Transition-A");
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 2),
            await mutations.ExecuteAsync(transition));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 2),
            await mutations.ExecuteAsync(transition));
        Assert.Equal("revoked", await StatusAsync("transition-a"));
        Assert.Equal("revoked", await StatusAsync("transition-b"));

        const string retained = "delete-𐐨-é";
        await SaveAsync(retained, "pending");
        var delete = DocumentIdentityAcceptanceModel.Delete("Delete-Replay", retained);
        var equivalentReplay = DocumentIdentityAcceptanceModel.Delete("Delete-Replay", "DELETE-𐐀-É");
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(delete));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Replayed, 1),
            await mutations.ExecuteAsync(equivalentReplay));
        Assert.Null(await fixture.Documents.LoadAsync(DocumentIdentityAcceptanceModel.DocumentKind, retained));

        await SaveAsync("case-upper", "pending");
        await SaveAsync("case-lower", "pending");
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(DocumentIdentityAcceptanceModel.Delete("Case-Operation", "case-upper")));
        Assert.Equal(
            new BoundedMutationResult(BoundedMutationStatus.Completed, 1),
            await mutations.ExecuteAsync(DocumentIdentityAcceptanceModel.Delete("case-operation", "case-lower")));

        async Task SaveAsync(string id, string status) => Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(id, status))).Status);

        async Task<string> StatusAsync(string id)
        {
            var document = Assert.IsType<DocumentEnvelope>(await fixture.Documents.LoadAsync(
                DocumentIdentityAcceptanceModel.DocumentKind,
                id));
            using var json = JsonDocument.Parse(document.ContentJson);
            return json.RootElement.GetProperty("status").GetString()!;
        }
    }

    [Fact]
    public async Task Four_hundred_fifty_utf16_code_unit_identity_boundary_preserves_equivalence_and_original()
    {
        await using var fixture = await CreateIdentityFixtureAsync();
        var authoritative = new string('é', 448) + char.ConvertFromUtf32(0x10428);
        var equivalent = new string('É', 448) + char.ConvertFromUtf32(0x10400);
        Assert.Equal(450, authoritative.Length);
        Assert.Equal(450, equivalent.Length);

        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(authoritative, "pending"))).Status);
        var result = await fixture.Queries.QueryAsync(DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, equivalent)));

        Assert.Equal(authoritative, Assert.Single(result.Documents).Id);
    }

    [Fact]
    public async Task Document_identity_over_four_hundred_fifty_utf16_code_units_is_rejected_on_every_surface()
    {
        await using var fixture = await CreateIdentityFixtureAsync(surface: DocumentIdentityAcceptanceSurface.Mutation);
        var overlong = new string('x', 451);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Documents.SaveAsync(
            DocumentIdentityAcceptanceModel.Save(overlong, "pending")));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Documents.LoadAsync(
            DocumentIdentityAcceptanceModel.DocumentKind,
            overlong));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Queries.QueryAsync(
            DocumentIdentityAcceptanceModel.ExactQuery(DocumentQueryComparison.Equal(
                PhysicalDocumentFieldPaths.Id,
                overlong))));
        await Assert.ThrowsAsync<ArgumentException>(() => Assert.IsAssignableFrom<IBoundedDocumentMutationStore>(fixture.Mutations)
            .ExecuteAsync(DocumentIdentityAcceptanceModel.Delete("overlong-operation", overlong)));
    }

    [Fact]
    public async Task Native_query_explain_uses_the_derived_identity_index_without_a_full_scan()
    {
        await using var fixture = await CreateIdentityFixtureAsync();
        const string authoritative = "explain-𐐨-é";
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(authoritative, "pending"))).Status);
        var query = DocumentIdentityAcceptanceModel.ExactQuery(
            DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, "EXPLAIN-𐐀-É"));

        var evidence = await fixture.ExplainQueryAsync(query);

        Assert.True(evidence.UsesExpectedIndex, evidence.Details);
        Assert.False(evidence.HasFullScan, evidence.Details);
        Assert.True(evidence.SelectorUsesLookupKey, evidence.Details);
        Assert.True(evidence.SelectorUsesComparisonKey, evidence.Details);
        Assert.True(evidence.IndexCoversSelectorFields, evidence.Details);
    }

    [Fact]
    public async Task Native_mutation_explain_uses_the_derived_identity_index_without_a_full_scan()
    {
        await using var fixture = await CreateIdentityFixtureAsync(surface: DocumentIdentityAcceptanceSurface.Mutation);
        const string authoritative = "mutation-explain-𐐨-é";
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(authoritative, "pending"))).Status);

        var evidence = await fixture.ExplainMutationAsync(DocumentIdentityAcceptanceModel.Delete(
            "mutation-explain",
            "MUTATION-EXPLAIN-𐐀-É"));

        Assert.True(evidence.UsesExpectedIndex, evidence.Details);
        Assert.False(evidence.HasFullScan, evidence.Details);
        Assert.True(evidence.SelectorUsesLookupKey, evidence.Details);
        Assert.True(evidence.SelectorUsesComparisonKey, evidence.Details);
        Assert.True(evidence.IndexCoversSelectorFields, evidence.Details);
    }

    [Fact]
    public async Task Public_mutation_runtime_returns_admitted_plan_and_native_selector_evidence()
    {
        await using var fixture = await CreateIdentityFixtureAsync(surface: DocumentIdentityAcceptanceSurface.Mutation);
        const string authoritative = "runtime-evidence-𐐨-é";
        Assert.Equal(
            DocumentStoreWriteStatus.Saved,
            (await fixture.Documents.SaveAsync(DocumentIdentityAcceptanceModel.Save(authoritative, "pending"))).Status);
        await PrepareMutationNativeEvidenceAsync(fixture);
        var mutations = Assert.IsAssignableFrom<IPhysicalDocumentMutationExplainer>(fixture.Mutations);
        var request = DocumentIdentityAcceptanceModel.Delete(
            "runtime-evidence",
            "RUNTIME-EVIDENCE-𐐀-É");

        var plan = mutations.ResolvePlan(request);
        var evidence = await mutations.ExplainAsync(request);

        Assert.Equal(plan, evidence.Plan);
        Assert.NotEmpty(evidence.Commands);
        Assert.All(evidence.Commands, command =>
        {
            Assert.False(string.IsNullOrWhiteSpace(command.NativePlanFormat));
            Assert.False(string.IsNullOrWhiteSpace(command.NativePlan));
        });
        var target = plan.Predicate.AccessKind == PhysicalQueryAccessKind.LinkedIndexThenPrimary
            ? ExecutableStorageObjectRole.LinkedIndexStorage
            : ExecutableStorageObjectRole.PrimaryStorage;
        var selector = evidence.Commands
            .SelectMany(command => command.Selectors)
            .First(item => item.Target == target);
        Assert.Equal(plan.Predicate.LookupObject, selector.StorageObject);
        Assert.Equal(selector.Index!.Identifier, selector.ObservedIndexIdentifier);
        if (fixture.CanObserveMutationSelection)
        {
            var executed = new List<(string Identity, string CommandText)>();
            var executionRuntime = fixture.CreateObservedMutationRuntime(
                (identity, commandText) => executed.Add((identity, commandText)));

            Assert.Equal(BoundedMutationStatus.Completed, (await executionRuntime.ExecuteAsync(request)).Status);
            Assert.Equal(
                evidence.Commands.Select(command => (command.Identity, command.RenderedCommand!)),
                executed);
        }
    }
}

public enum DocumentIdentityAcceptanceSurface
{
    Exact,
    OrderedRange,
    StartsWith,
    Mutation
}

public sealed class DocumentIdentityAcceptanceFixture(
    IDocumentStore documents,
    IBoundedDocumentStore queries,
    ExecutableStorageRoute route,
    Func<ValueTask> disposeAsync,
    IBoundedDocumentMutationStore? mutations = null,
    Func<DocumentQuery, Task<DocumentIdentityNativePlanEvidence>>? explainQueryAsync = null,
    Func<DocumentMutation, Task<DocumentIdentityNativePlanEvidence>>? explainMutationAsync = null,
    Func<Action<string, string>, IBoundedDocumentMutationStore>? observedMutationRuntimeFactory = null) : IAsyncDisposable
{
    public IDocumentStore Documents { get; } = documents;
    public IBoundedDocumentStore Queries { get; } = queries;
    public ExecutableStorageRoute Route { get; } = route;
    public IBoundedDocumentMutationStore? Mutations { get; } = mutations;
    public Task<DocumentIdentityNativePlanEvidence> ExplainQueryAsync(DocumentQuery query) =>
        (explainQueryAsync ?? throw new InvalidOperationException("Native query explain was not configured."))(query);
    public Task<DocumentIdentityNativePlanEvidence> ExplainMutationAsync(DocumentMutation mutation) =>
        (explainMutationAsync ?? throw new InvalidOperationException("Native mutation explain was not configured."))(mutation);
    public bool CanObserveMutationSelection => observedMutationRuntimeFactory is not null;
    public IBoundedDocumentMutationStore CreateObservedMutationRuntime(Action<string, string> observer) =>
        (observedMutationRuntimeFactory ??
         throw new InvalidOperationException("Mutation selection observation was not configured."))(observer);
    public ValueTask DisposeAsync() => disposeAsync();
}

public sealed record DocumentIdentityNativePlanEvidence(
    bool UsesExpectedIndex,
    bool HasFullScan,
    bool SelectorUsesLookupKey,
    bool SelectorUsesComparisonKey,
    bool IndexCoversSelectorFields,
    string Details)
{
    public static DocumentIdentityNativePlanEvidence Create(
        DocumentIdentityExpectedIndex expected,
        IReadOnlyList<DocumentIdentityAccessPath> accessPaths,
        IReadOnlyList<DocumentIdentityMaterializedIndex> materializedIndexes,
        IReadOnlyList<string> selectorFields,
        string details)
    {
        var materialized = materializedIndexes
            .Where(index => index.Name.Equals(expected.Name, StringComparison.Ordinal))
            .ToArray();
        var indexCoversSelector = materialized.Length == 1 &&
            materialized[0].IsUsable &&
            HasMandatoryLeadingShape(
                materialized[0].KeyFields,
                expected.MandatoryLeadingKeyFields);
        var structuredDetails = $"""
            EXPECTED INDEX: {expected.Name} ({string.Join(", ", expected.SelectorFields)})
            MANDATORY LEADING KEYS: {string.Join(", ", expected.MandatoryLeadingKeyFields)}
            ACCESS PATHS: {string.Join("; ", accessPaths.Select(path => path.ToString()))}
            MATERIALIZED INDEXES: {string.Join("; ", materializedIndexes.Select(index => index.ToString()))}
            {details}
            """;
        return new DocumentIdentityNativePlanEvidence(
            accessPaths.Any(path => !path.IsFullScan && path.IndexName == expected.Name),
            accessPaths.Any(path => path.IsFullScan),
            selectorFields.Contains(expected.SelectorFields[0], StringComparer.Ordinal),
            selectorFields.Contains(expected.SelectorFields[1], StringComparer.Ordinal),
            indexCoversSelector,
            structuredDetails);
    }

    private static bool HasMandatoryLeadingShape(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> required)
    {
        return actual.Count >= required.Count &&
               required.Select((field, offset) => (field, offset)).All(item =>
                   actual[item.offset].Equals(item.field, StringComparison.Ordinal));
    }
}

public sealed record DocumentIdentityExpectedIndex(
    string Name,
    IReadOnlyList<string> SelectorFields,
    IReadOnlyList<string>? RequiredLeadingKeyFields = null)
{
    public IReadOnlyList<string> MandatoryLeadingKeyFields => RequiredLeadingKeyFields ?? SelectorFields;

    public IReadOnlyList<string> SelectorFieldsBoundBy(IEnumerable<string> predicateFieldIdentifiers)
    {
        var predicateFields = predicateFieldIdentifiers.ToHashSet(StringComparer.Ordinal);
        return SelectorFields.Where(predicateFields.Contains).ToArray();
    }
}

public sealed record DocumentIdentityAccessPath(string? IndexName, bool IsFullScan)
{
    public override string ToString() => IsFullScan ? "FULL SCAN" : $"INDEX {IndexName ?? "<unknown>"}";
}

public sealed record DocumentIdentityMaterializedIndex(
    string Name,
    IReadOnlyList<string> KeyFields,
    bool IsValid = true,
    bool IsReady = true,
    bool IsUnfiltered = true,
    bool IsEnabled = true,
    bool IsHypothetical = false,
    bool IsPartial = false,
    bool IsSparse = false,
    bool IsHidden = false)
{
    public bool IsUsable => IsValid && IsReady && IsUnfiltered && IsEnabled && !IsHypothetical &&
                            !IsPartial && !IsSparse && !IsHidden;

    public override string ToString() =>
        $"{Name} ({string.Join(", ", KeyFields)}); " +
        $"valid={IsValid}; ready={IsReady}; unfiltered={IsUnfiltered}; enabled={IsEnabled}; " +
        $"hypothetical={IsHypothetical}; partial={IsPartial}; sparse={IsSparse}; hidden={IsHidden}";
}

public static class DocumentIdentityAcceptanceModel
{
    public const string DocumentKind = "identityDocument";
    public const string ExactQueryIdentity = "identity-exact";
    public const string ExactIndexIdentity = "by-identity-exact";
    public const string RangeQueryIdentity = "identity-range";
    public const string PrefixQueryIdentity = "identity-prefix";
    public const string OrderedIndexIdentity = "by-identity-ordered";
    public const string StatusQueryIdentity = "identity-status";
    public const string StatusIndexIdentity = "by-identity-status";
    public const string DeleteMutationIdentity = "delete-by-identity";
    public const string TransitionMutationIdentity = "transition-pending";

    public static DocumentIdentityExpectedIndex ExactIndex(ExecutableStorageRoute route)
    {
        var index = route.Indexes.Single(candidate => candidate.Identity.StartsWith(
            ExactIndexIdentity,
            StringComparison.Ordinal));
        return new DocumentIdentityExpectedIndex(
            index.Name.Identifier,
            [
                route.Envelope.Identity.LookupKey.Identifier,
                route.Envelope.Identity.ComparisonKey.Identifier
            ],
            index.Columns.Select(column => column.Column.Identifier).ToArray());
    }

    public static StorageManifest Manifest(
        PhysicalStorageForm form = PhysicalStorageForm.PhysicalEntityTable,
        StringIdentityCasePolicy stringCasePolicy = StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
        DocumentIdentityAcceptanceSurface surface = DocumentIdentityAcceptanceSurface.Exact,
        string? instance = null)
    {
        var suffix = string.IsNullOrWhiteSpace(instance) ? string.Empty : $"_{instance}";
        var shared = new SharedStorageBinding($"identity-acceptance{suffix}");
        var exactIndexIdentity = $"{ExactIndexIdentity}{suffix}";
        var orderedIndexIdentity = $"{OrderedIndexIdentity}{suffix}";
        var statusIndexIdentity = $"{StatusIndexIdentity}{suffix}";
        var exactIndex = new PhysicalIndexDefinition(
            exactIndexIdentity,
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("id_lookup_key", 1),
                new PhysicalIndexColumnDefinition("id_comparison_key", 2)
            ]);
        var orderedIndex = new PhysicalIndexDefinition(
            orderedIndexIdentity,
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("id_comparison_key", 1)
            ]);
        var statusIndex = new PhysicalIndexDefinition(
            statusIndexIdentity,
            [
                new PhysicalIndexColumnDefinition("storage_scope", 0),
                new PhysicalIndexColumnDefinition("status", 1)
            ]);
        IReadOnlyList<PhysicalIndexDefinition> physicalIndexes = surface switch
        {
            DocumentIdentityAcceptanceSurface.Exact => [exactIndex],
            DocumentIdentityAcceptanceSurface.Mutation => [exactIndex, statusIndex],
            _ => [orderedIndex]
        };
        var projected = new ProjectedColumnDefinition(
            "status",
            "status",
            PortablePhysicalType.String,
            Length: 32,
            IsNullable: false);
        var definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                shared,
                [projected],
                physicalIndexes,
                linkedProjectionLogicalName: $"identity_documents_lookup{suffix}"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                $"identity_documents{suffix}",
                indexes: physicalIndexes,
                linkedProjectedColumns: [projected],
                linkedProjectionLogicalName: $"identity_documents_lookup{suffix}"),
            PhysicalStorageForm.PhysicalEntityTable => PhysicalTableDefinition.PhysicalEntityTable(
                $"identity_documents{suffix}",
                [projected],
                indexes: physicalIndexes),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var exactLogicalIndex = new LogicalIndexDeclaration(
            exactIndexIdentity,
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var exactQuery = new BoundedQueryDeclaration(
            ExactQueryIdentity,
            exactIndexIdentity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In,
                PortableQueryOperation.NotEqual
            },
            QuerySortSupport.None,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
        var orderedLogicalIndex = new LogicalIndexDeclaration(
            orderedIndexIdentity,
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var rangeQuery = new BoundedQueryDeclaration(
            RangeQueryIdentity,
            orderedIndexIdentity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.GreaterThan,
                PortableQueryOperation.GreaterThanOrEqual,
                PortableQueryOperation.LessThan,
                PortableQueryOperation.LessThanOrEqual
            },
            QuerySortSupport.Both,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
        var prefixQuery = new BoundedQueryDeclaration(
            PrefixQueryIdentity,
            orderedIndexIdentity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.StartsWith },
            QuerySortSupport.Both,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing,
            supportsTotalCount: true);
        var statusLogicalIndex = new LogicalIndexDeclaration(
            statusIndexIdentity,
            [new IndexField("status")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var statusQuery = new BoundedQueryDeclaration(
            StatusQueryIdentity,
            statusIndexIdentity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        IReadOnlyList<LogicalIndexDeclaration> logicalIndexes = surface switch
        {
            DocumentIdentityAcceptanceSurface.Exact => [exactLogicalIndex],
            DocumentIdentityAcceptanceSurface.Mutation => [exactLogicalIndex, statusLogicalIndex],
            _ => [orderedLogicalIndex]
        };
        IReadOnlyList<BoundedQueryDeclaration> queries = surface switch
        {
            DocumentIdentityAcceptanceSurface.Exact => [exactQuery],
            DocumentIdentityAcceptanceSurface.OrderedRange => [rangeQuery],
            DocumentIdentityAcceptanceSurface.StartsWith => [prefixQuery],
            DocumentIdentityAcceptanceSurface.Mutation => [exactQuery, statusQuery],
            _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, null)
        };
        IReadOnlyList<BoundedMutationDeclaration> mutations = surface switch
        {
            DocumentIdentityAcceptanceSurface.Mutation =>
            [
                new BoundedMutationDeclaration(
                    DeleteMutationIdentity,
                    ExactQueryIdentity,
                    BoundedMutationAction.Delete()),
                new BoundedMutationDeclaration(
                    TransitionMutationIdentity,
                    StatusQueryIdentity,
                    BoundedMutationAction.Transition("status", ["pending"], "revoked"))
            ],
            _ => []
        };
        var unit = new StorageUnit(
            new StorageUnitIdentity(DocumentKind),
            "Identity acceptance document",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(stringCasePolicy: stringCasePolicy),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                logicalIndexes,
                queries,
                boundedMutations: mutations)
        };
        return new StorageManifest(
            new StorageManifestIdentity($"identity.acceptance.{form}.{stringCasePolicy}.{surface}{suffix}"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages = form == PhysicalStorageForm.SharedDocuments
                ? [new SharedDocumentStorageDefinition(shared, $"identity_documents{suffix}", new DocumentEnvelopeDefinition())]
                : []
        };
    }

    public static SaveDocumentRequest Save(string id, string status, long expectedVersion = 0) =>
        new(DocumentKind, id, "1", $"{{\"status\":\"{status}\"}}", expectedVersion);

    public static DocumentQuery ExactQuery(DocumentQueryComparison comparison) => new(
        DocumentKind,
        ExactQueryIdentity,
        [DocumentQueryClause.Of(comparison)]);

    public static DocumentQuery RangeQuery(DocumentQueryComparison comparison) => new(
        DocumentKind,
        RangeQueryIdentity,
        [DocumentQueryClause.Of(comparison)],
        order: [new DocumentQueryOrder(PhysicalDocumentFieldPaths.Id)]);

    public static DocumentQuery PrefixQuery(DocumentQueryComparison comparison) => new(
        DocumentKind,
        PrefixQueryIdentity,
        [DocumentQueryClause.Of(comparison)],
        order: [new DocumentQueryOrder(PhysicalDocumentFieldPaths.Id)]);

    public static DocumentMutation Delete(string operationId, string id) => new(
        DocumentKind,
        DeleteMutationIdentity,
        operationId,
        [DocumentQueryClause.Of(DocumentQueryComparison.Equal(PhysicalDocumentFieldPaths.Id, id))]);
}
