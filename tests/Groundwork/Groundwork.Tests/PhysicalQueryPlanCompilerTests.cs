using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Xunit;

namespace Groundwork.Tests;

public sealed class PhysicalQueryPlanCompilerTests
{
    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments, PhysicalQueryAccessKind.LinkedIndexThenPrimary)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, PhysicalQueryAccessKind.LinkedIndexThenPrimary)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, PhysicalQueryAccessKind.PrimaryProjectedColumns)]
    public void TypeFilteredLookupPlansAcrossAllThreeForms(
        PhysicalStorageForm form,
        PhysicalQueryAccessKind expectedAccess)
    {
        var fixture = CreateFixture(form, BoundedQueryExecutionClass.ScaleBearing);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesFor(expectedAccess));

        var plan = AssertPlan(result);
        Assert.Equal("list-by-stimulus-type", plan.QueryIdentity);
        Assert.Equal(expectedAccess, plan.AccessKind);
        Assert.Equal("stimulusType", Assert.Single(plan.Predicates).Path);
        Assert.True(plan.Scope.IsMandatory);
        Assert.Equal(fixture.Route.ScopeKey.Column.Identifier, plan.Scope.Field.Identifier);
        Assert.Equal(expectedAccess == PhysicalQueryAccessKind.LinkedIndexThenPrimary, plan.RequiresPrimaryLookup);
    }

    [Fact]
    public void OrdinaryDedicatedLookupCanUsePrimaryCanonicalJsonWithoutClientFallback()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        Assert.Equal(PhysicalQueryAccessKind.PrimaryCanonicalJson, plan.AccessKind);
        var field = Assert.Single(plan.Predicates).Field;
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, field.Source);
        Assert.Equal(IndexValueKind.Keyword, field.ValueKind);
        Assert.Contains("\"valueKind\":\"Keyword\"", PhysicalQueryPlanSerializer.Serialize(plan));
    }

    [Theory]
    [InlineData(false, IndexValueKind.Number, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.Number, PortableQueryOperation.StartsWith)]
    [InlineData(false, IndexValueKind.Boolean, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.Boolean, PortableQueryOperation.StartsWith)]
    [InlineData(false, IndexValueKind.DateTime, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.DateTime, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.Number, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.Number, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.Boolean, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.Boolean, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.DateTime, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.DateTime, PortableQueryOperation.StartsWith)]
    public void TextOperationsCannotBeCertifiedForNonTextCanonicalOrProjectedValues(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation)
    {
        var fixture = CreateTypedFixture(projected, valueKind, operation);
        var source = projected
            ? PhysicalQuerySourceKind.PrimaryProjectedColumns
            : PhysicalQuerySourceKind.PrimaryCanonicalJson;

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(source));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-009" &&
            diagnostic.Message.Contains(operation.ToString(), StringComparison.Ordinal) &&
            diagnostic.Message.Contains(valueKind.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, IndexValueKind.String, PortableQueryOperation.Contains)]
    [InlineData(false, IndexValueKind.Keyword, PortableQueryOperation.StartsWith)]
    [InlineData(true, IndexValueKind.String, PortableQueryOperation.Contains)]
    [InlineData(true, IndexValueKind.Keyword, PortableQueryOperation.StartsWith)]
    public void TextOperationsRemainExecutableForTextCanonicalAndProjectedValues(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation)
    {
        var fixture = CreateTypedFixture(projected, valueKind, operation);
        var source = projected
            ? PhysicalQuerySourceKind.PrimaryProjectedColumns
            : PhysicalQuerySourceKind.PrimaryCanonicalJson;

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(source)));

        Assert.Equal(valueKind, Assert.Single(plan.Predicates).Field.ValueKind);
        Assert.Contains(operation, Assert.Single(plan.Predicates).Operations);
    }

    [Theory]
    [InlineData(PortablePhysicalType.Guid, PortableQueryOperation.Contains)]
    [InlineData(PortablePhysicalType.Guid, PortableQueryOperation.StartsWith)]
    [InlineData(PortablePhysicalType.Json, PortableQueryOperation.Contains)]
    [InlineData(PortablePhysicalType.Json, PortableQueryOperation.StartsWith)]
    [InlineData(PortablePhysicalType.Binary, PortableQueryOperation.Contains)]
    [InlineData(PortablePhysicalType.Binary, PortableQueryOperation.StartsWith)]
    public void TextOperationsCannotBeCertifiedForOtherNonStringProjectedTypes(
        PortablePhysicalType physicalType,
        PortableQueryOperation operation)
    {
        var fixture = CreateTypedFixture(true, IndexValueKind.Keyword, operation, physicalType);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-009");
    }

    [Theory]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.String, PortableQueryOperation.GreaterThan)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.String, PortableQueryOperation.Contains)]
    [InlineData(IndexValueKind.Boolean, PortablePhysicalType.String, PortableQueryOperation.Contains)]
    [InlineData(IndexValueKind.DateTime, PortablePhysicalType.String, PortableQueryOperation.Contains)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Int32, PortableQueryOperation.Equal)]
    [InlineData(IndexValueKind.String, PortablePhysicalType.Guid, PortableQueryOperation.Equal)]
    public void Logical_value_kind_cannot_be_silently_reinterpreted_by_projected_storage(
        IndexValueKind logicalKind,
        PortablePhysicalType physicalType,
        PortableQueryOperation operation)
    {
        var result = Resolve(CreateTypedStorage(true, logicalKind, operation, physicalType));

        Assert.False(result.IsValid);
        Assert.Empty(result.Definitions);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-PHYSICAL-031" &&
            diagnostic.Message.Contains(logicalKind.ToString(), StringComparison.Ordinal) &&
            diagnostic.Message.Contains(physicalType.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(29, 4)]
    [InlineData(8, null)]
    public void Decimal_projections_require_explicit_supported_precision_and_scale(int? precision, int? scale)
    {
        var logical = new LogicalIndexDeclaration(
            "by-value",
            [new IndexField("value")],
            IndexValueKind.Number,
            false,
            MissingValueBehavior.Excluded);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "decimal_entities",
            [new ProjectedColumnDefinition("value", "value", PortablePhysicalType.Decimal, Precision: precision, Scale: scale)],
            indexes:
            [new PhysicalIndexDefinition(logical.Identity, [new PhysicalIndexColumnDefinition("value", 0)])]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logical],
            []);

        var result = Resolve(storage);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-PHYSICAL-018");
    }

    [Theory]
    [InlineData(IndexValueKind.String, PortablePhysicalType.String)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.String)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.Int32)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.Int64)]
    [InlineData(IndexValueKind.Number, PortablePhysicalType.Decimal)]
    [InlineData(IndexValueKind.Boolean, PortablePhysicalType.Boolean)]
    [InlineData(IndexValueKind.DateTime, PortablePhysicalType.DateTime)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Guid)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Json)]
    [InlineData(IndexValueKind.Keyword, PortablePhysicalType.Binary)]
    public void Compatible_projected_storage_preserves_the_declared_logical_value_kind(
        IndexValueKind logicalKind,
        PortablePhysicalType physicalType)
    {
        var fixture = CreateTypedFixture(
            true,
            logicalKind,
            PortableQueryOperation.Equal,
            physicalType);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Equal(logicalKind, Assert.Single(plan.Predicates).Field.ValueKind);
    }

    [Fact]
    public void Unselected_projection_does_not_change_canonical_json_semantics()
    {
        var original = CreateTypedStorage(
            true,
            IndexValueKind.Number,
            PortableQueryOperation.GreaterThan,
            PortablePhysicalType.String);
        var explicitPolicy = Assert.IsType<PhysicalStoragePolicy.ExplicitPolicy>(original.Policy);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            explicitPolicy.Definition.FeatureDefaultLogicalName!,
            explicitPolicy.Definition.ProjectedColumns,
            explicitPolicy.Definition.Envelope);
        var storage = new StorageUnitPhysicalStorage(
            original.ProvisioningMode,
            PhysicalStoragePolicy.Explicit(definition),
            original.LogicalIndexes,
            original.BoundedQueries);

        var fixture = Resolve(storage, null);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        var field = Assert.Single(plan.Predicates).Field;
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, field.Source);
        Assert.Equal(IndexValueKind.Number, field.ValueKind);
    }

    [Theory]
    [InlineData("id", IndexValueKind.Number, IndexValueKind.Keyword)]
    [InlineData("version", IndexValueKind.Keyword, IndexValueKind.Number)]
    public void Envelope_fields_reject_declared_kinds_that_change_intrinsic_semantics(
        string path,
        IndexValueKind declared,
        IndexValueKind intrinsic)
    {
        var logical = new LogicalIndexDeclaration(
            "by-envelope",
            [new IndexField(path)],
            declared,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "by-envelope",
            logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "envelope_documents",
            indexes:
            [
                new PhysicalIndexDefinition(
                    logical.Identity,
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition(path, 1)
                    ])
            ]);
        var fixture = Resolve(new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logical],
            [query]), null);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-010" &&
            diagnostic.Message.Contains(intrinsic.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void EnvelopeIndexCanBeSelectedInPrimaryStorage()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-document-kind",
            [new IndexField("documentKind")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-document-kind",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.ScaleBearing);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "workflow_trigger_bindings",
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition("storage_scope", 0),
                        new PhysicalIndexColumnDefinition("document_kind", 1)
                    ],
                    target: PhysicalIndexStorageTarget.PrimaryStorage)
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        var fixture = Resolve(storage, null);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        Assert.Equal(PhysicalQueryAccessKind.PrimaryEnvelope, plan.AccessKind);
        Assert.Equal(PhysicalQueryFieldSource.Envelope, Assert.Single(plan.Predicates).Field.Source);
    }

    [Fact]
    public void ProviderPreferenceCanSelectNativeDocumentFields()
    {
        var fixture = CreateFixture(PhysicalStorageForm.PhysicalEntityTable, BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(
            PhysicalQuerySourceKind.NativeDocumentFields,
            PhysicalQuerySourceKind.PrimaryCanonicalJson);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));

        Assert.Equal(PhysicalQueryAccessKind.NativeDocumentFields, plan.AccessKind);
        Assert.Equal(PhysicalQueryFieldSource.NativeDocumentField, Assert.Single(plan.Predicates).Field.Source);
        Assert.Equal("content.stimulusType", Assert.Single(plan.Predicates).Field.Identifier);
        Assert.Equal("storage_scope", plan.Scope.Field.Identifier);
        Assert.Equal("_id.id", Assert.Single(plan.Order, order => order.IsIdentityTieBreak).Field.Identifier);
    }

    [Fact]
    public async Task RuntimeSeamPreservesQueryIdentityAndDispatchesTheCompiledHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var planned = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var handler = new RecordingHandler(
            "test.PrimaryCanonicalJson",
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications: [CertificationFor(planned)]);
        var store = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]);
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-stimulus-type",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        await store.QueryAsync(query);
        Assert.False(await store.AnyAsync(query.Select(BoundedQueryResultOperation.Any)));

        Assert.Equal("list-by-stimulus-type", handler.LastPlan!.QueryIdentity);
        Assert.Equal("by-stimulus-type", handler.LastPlan.LogicalIndexIdentity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(
            new DocumentQuery(
                "workflowTriggerBinding",
                "unknown-query",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))])));
    }

    [Fact]
    public void RuntimeSeamFailsBeforeTrafficWhenScalePlanHasNoRegisteredIndexedHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var scaleStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [Query(BoundedQueryExecutionClass.ScaleBearing)]);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var handler = new RecordingHandler(
            "test.PrimaryCanonicalJson",
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications: []);

        var exception = Assert.Throws<InvalidOperationException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            scaleStorage,
            capabilities,
            [handler]));

        Assert.Contains("GW-QUERY-005", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkedIndexCannotCertifyScaleBearingNativePrimaryFieldHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.SharedDocuments,
            BoundedQueryExecutionClass.ScaleBearing);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.NativeDocumentFields));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-005");
    }

    [Fact]
    public void RuntimeSeamRejectsCapabilityClaimsNotBackedByRegisteredHandler()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var limitedHandler = new RecordingHandler(
            "test.PrimaryCanonicalJson",
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            []);

        Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [limitedHandler]));
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForAnotherProvider()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications:
            [
                CertificationFor(plan, provider: new ProviderIdentity("another-provider", "1.0.0"))
            ]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForUnrelatedPhysicalIndex()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications:
            [
                CertificationFor(
                    plan,
                    indexName: plan.IndexName! with { Identifier = "ix_unrelated" })
            ]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForWrongObjectAndRole()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications:
            [
                CertificationFor(
                    plan,
                    target: ExecutableStorageObjectRole.LinkedIndexStorage,
                    lookupObject: plan.LookupObject with { Identifier = "unrelated_object" })
            ]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSeamRejectsHandlerCertificationForWrongFieldMapping()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var fields = PlanFieldIdentifiers(plan);
        fields["stimulusType"] = "unrelated_field";
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications: [CertificationFor(plan, fieldIdentifiers: fields)]);

        var exception = Assert.Throws<ArgumentException>(() => new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityProfileIsDeeplyImmutableAndUsesStructuralEquality()
    {
        var sources = new List<PhysicalQuerySourceKind> { PhysicalQuerySourceKind.NativeDocumentFields };
        var operations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        var handlers = new Dictionary<PhysicalQuerySourceKind, string>
        {
            [PhysicalQuerySourceKind.NativeDocumentFields] = "mongo.native"
        };
        var fields = new Dictionary<string, string> { ["stimulusType"] = "content.stimulusType" };
        var valueKinds = new HashSet<IndexValueKind> { IndexValueKind.Keyword };
        var sourceValueKinds = new Dictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>>
        {
            [PhysicalQuerySourceKind.NativeDocumentFields] = valueKinds
        };
        var first = new PhysicalQueryPlannerCapabilities(
            new ProviderIdentity("provider", "1"),
            sources,
            operations,
            handlers,
            fields,
            true, true, true, true, true, true, true, true,
            sourceValueKinds);
        var second = new PhysicalQueryPlannerCapabilities(
            new ProviderIdentity("provider", "1"),
            [PhysicalQuerySourceKind.NativeDocumentFields],
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            new Dictionary<PhysicalQuerySourceKind, string>
            {
                [PhysicalQuerySourceKind.NativeDocumentFields] = "mongo.native"
            },
            new Dictionary<string, string> { ["stimulusType"] = "content.stimulusType" },
            true, true, true, true, true, true, true, true,
            new Dictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>>
            {
                [PhysicalQuerySourceKind.NativeDocumentFields] = new HashSet<IndexValueKind> { IndexValueKind.Keyword }
            });

        sources.Clear();
        operations.Clear();
        handlers.Clear();
        fields.Clear();
        valueKinds.Clear();
        sourceValueKinds.Clear();

        Assert.Single(first.SourcePreference);
        Assert.Single(first.SupportedOperations);
        Assert.Single(first.HandlerIdentities);
        Assert.Single(first.NativeFieldIdentifiers);
        Assert.Equal(IndexValueKind.Keyword, Assert.Single(first.SourceValueKinds.Single().Value));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void HandlerCertificationIsDeeplyImmutableAndUsesStructuralEquality()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var paths = plan.LogicalIndexPaths.ToList();
        var fields = PlanFieldIdentifiers(plan);
        var first = new PhysicalQueryHandlerCertification(
            plan.Provider,
            plan.StorageUnit,
            plan.QueryIdentity,
            plan.LogicalIndexIdentity,
            paths,
            plan.AccessKind,
            plan.Scope.Field.Target,
            plan.LookupObject,
            plan.PrimaryObject,
            plan.IndexName,
            fields,
            plan.RouteFingerprint);
        var second = CertificationFor(plan);

        paths.Clear();
        fields.Clear();

        Assert.Single(first.LogicalIndexPaths);
        Assert.NotEmpty(first.FieldIdentifiers);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<string>>(first.LogicalIndexPaths).Clear());
    }

    [Fact]
    public void OneClosedDeclarationPlansEveryBoundedOperatorAndTerminal()
    {
        var predicate = new BoundedQueryPredicateField(
            "stimulusType",
            Enum.GetValues<PortableQueryOperation>().ToHashSet());
        var query = Query(
            BoundedQueryExecutionClass.Ordinary,
            predicateFields: [predicate],
            resultOperations: Enum.GetValues<BoundedQueryResultOperation>().ToHashSet(),
            pagingSupport: QueryPagingSupport.Offset,
            supportsDisjunction: true,
            supportsTotalCount: true);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        Assert.Equal(
            Enum.GetValues<PortableQueryOperation>().Order(),
            Assert.Single(plan.Predicates).Operations.Order());
        Assert.Equal(
            Enum.GetValues<BoundedQueryResultOperation>().Order(),
            plan.ResultOperations.Order());
        Assert.True(plan.SupportsDisjunction);
        Assert.Equal(QueryPagingSupport.Offset, plan.PagingSupport);
    }

    [Fact]
    public void CompoundPrefixDirectionAndIdentityTieBreakAreDeterministic()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "latest-by-stimulus-type",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            resultOperations: new HashSet<BoundedQueryResultOperation>
            {
                BoundedQueryResultOperation.Documents,
                BoundedQueryResultOperation.First
            });
        var fixture = CreateEntityFixture(logicalIndex, query);

        var first = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var second = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Collection(
            first.Order,
            order =>
            {
                Assert.Equal("createdAt", order.Path);
                Assert.Equal(PhysicalSortDirection.Descending, order.Direction);
                Assert.False(order.IsIdentityTieBreak);
            },
            order =>
            {
                Assert.Equal("id", order.Path);
                Assert.Equal(PhysicalSortDirection.Ascending, order.Direction);
                Assert.True(order.IsIdentityTieBreak);
            });
        Assert.Equal(first, second);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<PhysicalQueryOrder>>(first.Order).Clear());
    }

    [Fact]
    public void LatestPerKeyAndKeysetMustBeServedByDeclaredProviderHandlers()
    {
        var query = Query(
            BoundedQueryExecutionClass.Ordinary,
            pagingSupport: QueryPagingSupport.Cursor,
            latestPerKeyPath: "stimulusType",
            sortFields: [new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Descending)]);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);
        var unsupported = CapabilitiesWithPaging(
            supportsKeysetPaging: false,
            supportsLatestPerKey: false,
            sources: [PhysicalQuerySourceKind.PrimaryCanonicalJson]);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, unsupported);

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-007");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-008");
    }

    [Fact]
    public void ScaleBearingQueryWithoutExecutableIndexedRouteFailsBeforeTraffic()
    {
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, BoundedQueryExecutionClass.Ordinary);
        var scaleBearing = Query(BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [scaleBearing]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-005");
    }

    [Fact]
    public void ScaleBearingQueryMustBeBoundToTheCompiledRouteFingerprintInput()
    {
        var logicalIndex = StimulusTypeIndex();
        var routedQuery = Query(BoundedQueryExecutionClass.ScaleBearing);
        var fixture = CreateEntityFixture(logicalIndex, routedQuery);
        var staleQuery = new BoundedQueryDeclaration(
            "renamed-after-route-compilation",
            routedQuery.IndexIdentity,
            routedQuery.Operations,
            routedQuery.SortSupport,
            routedQuery.PagingSupport,
            routedQuery.ExecutionClass,
            routedQuery.SupportsDisjunction,
            routedQuery.SupportsTotalCount,
            routedQuery.SortFields,
            routedQuery.PredicateFields,
            routedQuery.ResultOperations,
            routedQuery.LatestPerKeyPath);
        var staleStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [staleQuery]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            staleStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-005");
    }

    [Fact]
    public void UnsupportedCompoundPrefixIsRejectedInsteadOfUsingClientFallback()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "valid-prefix",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "createdAt",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var validQuery = new BoundedQueryDeclaration(
            "valid-prefix",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, validQuery);
        var invalidStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [query]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            invalidStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-006");
    }

    [Fact]
    public void OrderedCompoundSuffixRejectsRangePrefix()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var routedQuery = new BoundedQueryDeclaration(
            "range-prefix",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, routedQuery);
        var rangeQuery = new BoundedQueryDeclaration(
            routedQuery.Identity,
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan },
            routedQuery.SortSupport,
            routedQuery.PagingSupport,
            routedQuery.ExecutionClass,
            sortFields: routedQuery.SortFields,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan })
            ]);
        var invalidStorage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [rangeQuery]);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            invalidStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-QUERY-006");
    }

    [Fact]
    public async Task RuntimeSuffixOrderingRequiresOneStandaloneEqualityForEverySkippedPrefix()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "latest-by-stimulus-type",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields: [new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, query);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications: [CertificationFor(plan)]);
        var store = new PhysicalQueryDocumentStore(fixture.Route, fixture.Storage, capabilities, [handler]);
        var missingPrefix = new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity,
            order: [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 25);
        var disjunctivePrefix = new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity,
            [DocumentQueryClause.AnyOf(
                DocumentQueryComparison.Equal("stimulusType", "http"),
                DocumentQueryComparison.Equal("stimulusType", "timer"))],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 25);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(missingPrefix));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(disjunctivePrefix));

        await store.QueryAsync(new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 25));
    }

    [Fact]
    public void NativeScopeAndDiscriminatorUseNativeFieldMetadata()
    {
        var fixture = CreateFixture(PhysicalStorageForm.PhysicalEntityTable, BoundedQueryExecutionClass.ScaleBearing);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.NativeDocumentFields)));

        Assert.Equal(PhysicalQueryFieldSource.NativeDocumentField, plan.Scope.Field.Source);
        Assert.Equal(PhysicalQueryFieldSource.NativeDocumentField, plan.Discriminator.Source);
    }

    [Fact]
    public void LegacyBridgeRejectsCompoundStablePathsInsteadOfCollapsingThemToOneIndexIdentity()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var queryDeclaration = new BoundedQueryDeclaration(
            "search-by-stimulus-created",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.Ordinary,
            predicateFields:
            [
                new BoundedQueryPredicateField("stimulusType", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal }),
                new BoundedQueryPredicateField("createdAt", new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateEntityFixture(logicalIndex, queryDeclaration);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));
        var exception = Assert.Throws<ArgumentException>(() => new LegacyPortableDocumentQueryHandler(
            plan.HandlerIdentity,
            new CapturingDocumentStore(),
            [CertificationFor(plan)]));

        Assert.Contains("single-field", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyBridgeMapsOneStablePathAndPreservesThePlannedDefaultOrder()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, capabilities));
        var legacyStore = new CapturingDocumentStore();
        var handler = new LegacyPortableDocumentQueryHandler(
            plan.HandlerIdentity,
            legacyStore,
            [CertificationFor(plan)]);
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            plan.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        await handler.QueryAsync(query, plan, CancellationToken.None);

#pragma warning disable GW0004
        var bridged = Assert.IsType<PortableDocumentQuery>(legacyStore.LastQuery);
#pragma warning restore GW0004
        Assert.Equal(plan.LogicalIndexIdentity, Assert.Single(Assert.Single(bridged.Clauses).Comparisons).IndexName);
        Assert.Equal(plan.LogicalIndexIdentity, bridged.Order!.IndexName);
        Assert.False(bridged.Order.Descending);
    }

    private static PlanningFixture CreateFixture(
        PhysicalStorageForm form,
        BoundedQueryExecutionClass executionClass) =>
        CreateFixture(form, Query(executionClass));

    private static PlanningFixture CreateFixture(
        PhysicalStorageForm form,
        BoundedQueryDeclaration query)
    {
        var logicalIndex = StimulusTypeIndex();
        if (form == PhysicalStorageForm.PhysicalEntityTable)
            return CreateEntityFixture(logicalIndex, query);

        var binding = new SharedStorageBinding("runtime-documents");
        PhysicalTableDefinition definition = form switch
        {
            PhysicalStorageForm.SharedDocuments => PhysicalTableDefinition.SharedDocuments(
                binding,
                [new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String)],
                [
                    new PhysicalIndexDefinition(
                        logicalIndex.Identity,
                        [
                            new PhysicalIndexColumnDefinition("storage_scope", 0),
                            new PhysicalIndexColumnDefinition("stimulusType", 1)
                        ])
                ],
                linkedProjectionLogicalName: "workflow_trigger_binding_index"),
            PhysicalStorageForm.DedicatedDocumentTable when query.ExecutionClass == BoundedQueryExecutionClass.ScaleBearing =>
                PhysicalTableDefinition.DedicatedDocumentTable(
                    "workflow_trigger_bindings",
                    indexes:
                    [
                        new PhysicalIndexDefinition(
                            logicalIndex.Identity,
                            [
                                new PhysicalIndexColumnDefinition("storage_scope", 0),
                                new PhysicalIndexColumnDefinition("stimulusType", 1)
                            ])
                    ],
                    linkedProjectedColumns:
                    [new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String)],
                    linkedProjectionLogicalName: "workflow_trigger_binding_index"),
            PhysicalStorageForm.DedicatedDocumentTable => PhysicalTableDefinition.DedicatedDocumentTable(
                "workflow_trigger_bindings"),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        return Resolve(storage, form == PhysicalStorageForm.SharedDocuments ? binding : null);
    }

    private static PlanningFixture CreateEntityFixture(
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query)
    {
        var projections = logicalIndex.Fields
            .Select(field => new ProjectedColumnDefinition(
                field.Path,
                field.Path,
                field.Path == "createdAt" ? PortablePhysicalType.DateTime : PortablePhysicalType.String))
            .ToArray();
        var columns = new List<PhysicalIndexColumnDefinition>
        {
            new("storage_scope", 0)
        };
        columns.AddRange(logicalIndex.Fields.Select((field, index) =>
            new PhysicalIndexColumnDefinition(
                field.Path,
                index + 1,
                query.SortFields.SingleOrDefault(sort => sort.Path == field.Path)?.Direction
                ?? PhysicalSortDirection.Ascending)));
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "workflow_trigger_bindings",
            projections,
            indexes: [new PhysicalIndexDefinition(logicalIndex.Identity, columns)]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        return Resolve(storage, null);
    }

    private static PlanningFixture CreateTypedFixture(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation,
        PortablePhysicalType? projectedType = null)
        => Resolve(CreateTypedStorage(projected, valueKind, operation, projectedType), null);

    private static StorageUnitPhysicalStorage CreateTypedStorage(
        bool projected,
        IndexValueKind valueKind,
        PortableQueryOperation operation,
        PortablePhysicalType? projectedType = null)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-value",
            [new IndexField("value")],
            valueKind,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "find-by-value",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { operation },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var definition = projected
            ? PhysicalTableDefinition.PhysicalEntityTable(
                "typed_entities",
                [TypedProjection("value", projectedType ?? ToPortableType(valueKind))],
                indexes:
                [
                    new PhysicalIndexDefinition(
                        logicalIndex.Identity,
                        [
                            new PhysicalIndexColumnDefinition("storage_scope", 0),
                            new PhysicalIndexColumnDefinition("value", 1)
                        ])
                ])
            : PhysicalTableDefinition.DedicatedDocumentTable("typed_documents");
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        return storage;
    }

    private static PhysicalStorageResolutionResult Resolve(StorageUnitPhysicalStorage storage)
    {
        var template = SampleManifests.MetadataManifest();
        var manifest = template with
        {
            StorageUnits =
            [
                template.StorageUnits.Single() with
                {
                    Identity = new StorageUnitIdentity("workflowTriggerBinding"),
                    PhysicalStorage = storage
                }
            ]
        };
        return PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
    }

    private static PortablePhysicalType ToPortableType(IndexValueKind valueKind) => valueKind switch
    {
        IndexValueKind.String or IndexValueKind.Keyword => PortablePhysicalType.String,
        IndexValueKind.Number => PortablePhysicalType.Decimal,
        IndexValueKind.Boolean => PortablePhysicalType.Boolean,
        IndexValueKind.DateTime => PortablePhysicalType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(valueKind), valueKind, null)
    };

    private static ProjectedColumnDefinition TypedProjection(string path, PortablePhysicalType type) =>
        new(
            path,
            path,
            type,
            Precision: type == PortablePhysicalType.Decimal ? 18 : null,
            Scale: type == PortablePhysicalType.Decimal ? 4 : null);

    private static PlanningFixture Resolve(StorageUnitPhysicalStorage storage, SharedStorageBinding? binding)
    {
        var template = SampleManifests.MetadataManifest();
        var unit = template.StorageUnits.Single() with
        {
            Identity = new StorageUnitIdentity("workflowTriggerBinding"),
            PhysicalStorage = storage
        };
        var manifest = template with
        {
            StorageUnits = [unit],
            SharedDocumentStorages = binding is null
                ? []
                : [new SharedDocumentStorageDefinition(binding, "documents", new DocumentEnvelopeDefinition())]
        };
        var resolved = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);
        Assert.True(resolved.IsValid, string.Join("; ", resolved.Diagnostics.Select(x => x.Message)));
        var routeResult = ExecutableStorageRouteCompiler.Compile(Assert.Single(resolved.Definitions));
        Assert.True(routeResult.IsValid, string.Join("; ", routeResult.Diagnostics.Select(x => x.Message)));
        return new PlanningFixture(Assert.Single(routeResult.Routes), storage);
    }

    private static LogicalIndexDeclaration StimulusTypeIndex() =>
        new(
            "by-stimulus-type",
            [new IndexField("stimulusType")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);

    private static BoundedQueryDeclaration Query(
        BoundedQueryExecutionClass executionClass,
        IReadOnlyList<BoundedQueryPredicateField>? predicateFields = null,
        IReadOnlySet<BoundedQueryResultOperation>? resultOperations = null,
        QueryPagingSupport pagingSupport = QueryPagingSupport.Offset,
        bool supportsDisjunction = false,
        bool supportsTotalCount = true,
        string? latestPerKeyPath = null,
        IReadOnlyList<BoundedQuerySortField>? sortFields = null) =>
        new(
            "list-by-stimulus-type",
            "by-stimulus-type",
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            sortFields is null ? QuerySortSupport.Both : QuerySortSupport.Descending,
            pagingSupport,
            executionClass,
            supportsDisjunction,
            supportsTotalCount,
            sortFields,
            predicateFields,
            resultOperations,
            latestPerKeyPath);

    private static PhysicalQueryPlannerCapabilities CapabilitiesFor(PhysicalQueryAccessKind accessKind) =>
        Capabilities(accessKind switch
        {
            PhysicalQueryAccessKind.LinkedIndexThenPrimary => PhysicalQuerySourceKind.LinkedIndex,
            PhysicalQueryAccessKind.PrimaryCanonicalJson => PhysicalQuerySourceKind.PrimaryCanonicalJson,
            PhysicalQueryAccessKind.PrimaryProjectedColumns => PhysicalQuerySourceKind.PrimaryProjectedColumns,
            _ => throw new ArgumentOutOfRangeException(nameof(accessKind), accessKind, null)
        });

    private static PhysicalQueryPlannerCapabilities Capabilities(params PhysicalQuerySourceKind[] sources) =>
        CapabilitiesWithPaging(true, true, sources);

    private static PhysicalQueryPlannerCapabilities CapabilitiesWithPaging(
        bool supportsKeysetPaging,
        bool supportsLatestPerKey,
        params PhysicalQuerySourceKind[] sources) =>
        new(
            new ProviderIdentity("test-provider", "1.0.0"),
            sources,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            sources.ToDictionary(source => source, source => $"test.{source}"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stimulusType"] = "content.stimulusType",
                ["createdAt"] = "content.createdAt",
                ["id"] = "_id.id",
                ["storageScope"] = "storage_scope",
                ["documentKind"] = "document_kind"
            },
            supportsCompoundPredicates: true,
            supportsDisjunction: true,
            supportsOffsetPaging: true,
            supportsKeysetPaging,
            supportsCount: true,
            supportsAny: true,
            supportsFirst: true,
            supportsLatestPerKey);

    private static PhysicalQueryPlan AssertPlan(PhysicalQueryPlanCompilationResult result)
    {
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        return Assert.Single(result.Plans);
    }

    private static PhysicalQueryHandlerCertification CertificationFor(
        PhysicalQueryPlan plan,
        ProviderIdentity? provider = null,
        ProviderPhysicalObjectName? indexName = null,
        ExecutableStorageObjectRole? target = null,
        ProviderPhysicalObjectName? lookupObject = null,
        IReadOnlyDictionary<string, string>? fieldIdentifiers = null)
    {
        return new PhysicalQueryHandlerCertification(
            provider ?? plan.Provider,
            plan.StorageUnit,
            plan.QueryIdentity,
            plan.LogicalIndexIdentity,
            plan.LogicalIndexPaths,
            plan.AccessKind,
            target ?? plan.Scope.Field.Target,
            lookupObject ?? plan.LookupObject,
            plan.PrimaryObject,
            indexName ?? plan.IndexName,
            fieldIdentifiers ?? PlanFieldIdentifiers(plan),
            plan.RouteFingerprint);
    }

    private static Dictionary<string, string> PlanFieldIdentifiers(PhysicalQueryPlan plan) =>
        new[] { plan.Scope.Field, plan.Discriminator }
            .Concat(plan.Predicates.Select(predicate => predicate.Field))
            .Concat(plan.Order.Select(order => order.Field))
            .GroupBy(field => field.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Identifier, StringComparer.Ordinal);

    private sealed record PlanningFixture(ExecutableStorageRoute Route, StorageUnitPhysicalStorage Storage);

    private sealed class RecordingHandler(
        string identity,
        PhysicalQuerySourceKind source,
        IReadOnlySet<PortableQueryOperation>? supportedOperations = null,
        IReadOnlyList<PhysicalQueryHandlerCertification>? certifications = null) : IPhysicalDocumentQueryHandler
    {
        public string Identity { get; } = identity;
        public PhysicalQuerySourceKind Source { get; } = source;
        public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } =
            supportedOperations ?? Enum.GetValues<PortableQueryOperation>().ToHashSet();
        public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; } =
            new Dictionary<string, string>
            {
                ["stimulusType"] = "content.stimulusType",
                ["createdAt"] = "content.createdAt",
                ["id"] = "_id.id",
                ["storageScope"] = "storage_scope",
                ["documentKind"] = "document_kind"
            };
        public IReadOnlyList<PhysicalQueryHandlerCertification> Certifications { get; } =
            certifications ?? [];
        public bool SupportsCompoundPredicates => true;
        public bool SupportsDisjunction => true;
        public bool SupportsOffsetPaging => true;
        public bool SupportsKeysetPaging => true;
        public bool SupportsCount => true;
        public bool SupportsAny => true;
        public bool SupportsFirst => true;
        public bool SupportsLatestPerKey => true;
        public PhysicalQueryPlan? LastPlan { get; private set; }

        public Task<DocumentQueryResult> QueryAsync(
            DocumentQuery query,
            PhysicalQueryPlan plan,
            CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult(DocumentQueryResult.Empty);
        }

        public Task<long> CountAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult(0L);
        }

        public Task<DocumentEnvelope?> FirstOrDefaultAsync(
            DocumentQuery query,
            PhysicalQueryPlan plan,
            CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult<DocumentEnvelope?>(null);
        }

        public Task<bool> AnyAsync(DocumentQuery query, PhysicalQueryPlan plan, CancellationToken cancellationToken)
        {
            LastPlan = plan;
            return Task.FromResult(false);
        }
    }

    private sealed class CapturingDocumentStore : IDocumentStore
    {
        public DocumentStoreAccess Access => DocumentStoreAccess.Global;
        public TransactionBoundary TransactionBoundary => TransactionBoundary.PerOperation;
#pragma warning disable GW0004
        public PortableDocumentQuery? LastQuery { get; private set; }
#pragma warning restore GW0004

        public Task<DocumentStoreWriteResult> SaveAsync(
            SaveDocumentRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentEnvelope?> LoadAsync(
            string documentKind,
            string id,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentStoreWriteResult> DeleteAsync(
            DeleteDocumentRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

#pragma warning disable GW0004
        public Task<IReadOnlyList<DocumentEnvelope>> QueryAsync(
            DocumentStoreQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DocumentQueryResult> QueryAsync(
            PortableDocumentQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(DocumentQueryResult.Empty);
        }

        public Task<DocumentEnvelope?> FirstOrDefaultAsync(
            PortableDocumentQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> AnyAsync(
            PortableDocumentQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
#pragma warning restore GW0004

        public Task<IDocumentUnitOfWork> BeginAsync(
            DocumentCommitScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
