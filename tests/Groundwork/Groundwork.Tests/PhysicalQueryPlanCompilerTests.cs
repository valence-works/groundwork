using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Xunit;

namespace Groundwork.Tests;

public sealed class PhysicalQueryPlanCompilerTests
{
    [Fact]
    public void Exact_identity_query_binding_projects_provider_neutral_linked_evidence()
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked: true,
            BoundedMutationAction.Delete(),
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex)));
        var binding = plan.DocumentIdentity;
        var value = binding.Bind(
            PortableQueryOperation.Equal,
            "METRIC-\U00010400-\u00c9");
        var equivalent = binding.Bind(
            PortableQueryOperation.Equal,
            "metric-\U00010428-\u00e9");
        var exact = Assert.IsType<PhysicalQueryIdentityValue.Exact>(value);
        var equivalentExact = Assert.IsType<PhysicalQueryIdentityValue.Exact>(equivalent);

        Assert.Equal(fixture.Route.LinkedRelationship!.Identity.OriginalId.Identifier, binding.Original.Identifier);
        Assert.Equal(fixture.Route.LinkedRelationship.Identity.ComparisonKey.Identifier, binding.Comparison.Identifier);
        Assert.Equal(fixture.Route.LinkedRelationship.Identity.LookupKey.Identifier, binding.Lookup.Identifier);
        Assert.Equal("00004D00004500005400005200004900004300002D01040000002D0000C9", value.ComparisonKey);
        Assert.Equal("61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181", exact.LookupKey);
        Assert.Equal(value.ComparisonKey, equivalent.ComparisonKey);
        Assert.Equal(exact.LookupKey, equivalentExact.LookupKey);
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope, PhysicalQueryFieldSource.Envelope, "id")]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields, PhysicalQueryFieldSource.NativeDocumentField, "_id.id")]
    public void Identity_query_binding_uses_the_selected_primary_or_native_source(
        PhysicalQuerySourceKind source,
        PhysicalQueryFieldSource expectedFieldSource,
        string expectedOriginalIdentifier)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(source)));

        Assert.Equal(expectedFieldSource, plan.DocumentIdentity.Original.Source);
        Assert.Equal(expectedOriginalIdentifier, plan.DocumentIdentity.Original.Identifier);
        Assert.Equal(fixture.Route.Envelope.Identity.ComparisonKey.Identifier, plan.DocumentIdentity.Comparison.Identifier);
        Assert.Equal(fixture.Route.Envelope.Identity.LookupKey.Identifier, plan.DocumentIdentity.Lookup.Identifier);
    }

    [Fact]
    public void Identity_contains_is_rejected_before_a_physical_plan_is_published()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains });

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-011" &&
            diagnostic.Message.Contains("identity", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Message.Contains("Contains", StringComparison.Ordinal));
    }

    [Fact]
    public void Canonical_query_plan_serializes_the_complete_identity_binding()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        var canonical = PhysicalQueryPlanSerializer.Serialize(plan);

        Assert.Contains("\"documentIdentity\":", canonical, StringComparison.Ordinal);
        Assert.Contains("\"stringCasePolicy\":\"UnicodeOrdinalIgnoreCase\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"original\":{\"path\":\"id.original\",\"identifier\":\"id\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"comparison\":{\"path\":\"id.comparison\",\"identifier\":\"id_comparison_key\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"lookup\":{\"path\":\"id.lookup\",\"identifier\":\"id_lookup_key\"", canonical, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortableQueryOperation.Equal, true)]
    [InlineData(PortableQueryOperation.In, true)]
    [InlineData(PortableQueryOperation.NotEqual, true)]
    [InlineData(PortableQueryOperation.StartsWith, false)]
    [InlineData(PortableQueryOperation.GreaterThan, false)]
    [InlineData(PortableQueryOperation.GreaterThanOrEqual, false)]
    [InlineData(PortableQueryOperation.LessThan, false)]
    [InlineData(PortableQueryOperation.LessThanOrEqual, false)]
    public void Identity_operators_bind_structurally_valid_exact_or_ordered_evidence_without_adapter_policy(
        PortableQueryOperation operation,
        bool exact)
    {
        var fixture = CreateIdentityQueryFixture(new HashSet<PortableQueryOperation> { operation });
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        var value = plan.DocumentIdentity.Bind(operation, "metric-\U00010428-\u00e9");

        Assert.Equal("00004D00004500005400005200004900004300002D01040000002D0000C9", value.ComparisonKey);
        if (exact)
        {
            var exactValue = Assert.IsType<PhysicalQueryIdentityValue.Exact>(value);
            Assert.Equal("61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181", exactValue.LookupKey);
        }
        else
        {
            Assert.IsType<PhysicalQueryIdentityValue.Ordered>(value);
        }
        var tieBreak = Assert.Single(plan.Order, order =>
            order.Path == PhysicalDocumentFieldPaths.Id && order.IsIdentityTieBreak);
        Assert.Equal(plan.DocumentIdentity.Comparison, tieBreak.Field);
    }

    [Fact]
    public void Identity_binding_rejects_null_instead_of_publishing_partial_evidence()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope)));

        Assert.Throws<ArgumentNullException>(() =>
            plan.DocumentIdentity.Bind(PortableQueryOperation.Equal, null!));
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope)]
    [InlineData(PhysicalQuerySourceKind.LinkedIndex)]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields)]
    public void Exact_identity_plan_certifies_only_lookup_leading_full_comparison_indexes(
        PhysicalQuerySourceKind source)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            source: source,
            indexLayout: IdentityIndexLayout.Exact);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, Capabilities(source));

        var plan = AssertPlan(result);
        Assert.NotNull(plan.IndexName);
        Assert.Equal(plan.DocumentIdentity.Comparison, plan.Predicates.Single().Field);
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope)]
    [InlineData(PhysicalQuerySourceKind.LinkedIndex)]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields)]
    public void Ordered_identity_plan_certifies_only_comparison_key_indexes(
        PhysicalQuerySourceKind source)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.GreaterThan },
            source: source,
            indexLayout: IdentityIndexLayout.Ordered);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, Capabilities(source));

        var plan = AssertPlan(result);
        Assert.NotNull(plan.IndexName);
        Assert.Equal(plan.DocumentIdentity.Comparison, plan.Predicates.Single().Field);
    }

    [Theory]
    [InlineData(PhysicalQuerySourceKind.PrimaryEnvelope)]
    [InlineData(PhysicalQuerySourceKind.LinkedIndex)]
    [InlineData(PhysicalQuerySourceKind.NativeDocumentFields)]
    public void Retained_original_identity_index_cannot_certify_projected_query_evidence(
        PhysicalQuerySourceKind source)
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            source: source,
            indexLayout: IdentityIndexLayout.Original);

        var result = PhysicalQueryPlanCompiler.Compile(fixture.Route, fixture.Storage, Capabilities(source));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-006" &&
            diagnostic.Message.Contains("id.lookup", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("id.comparison", StringComparison.Ordinal));
    }

    [Fact]
    public void Scale_bearing_mixed_exact_and_ordered_identity_demand_is_rejected_without_choosing_index_order()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.GreaterThan
            },
            indexLayout: IdentityIndexLayout.Exact);
        var ordinary = fixture.Storage.BoundedQueries.Single();
        var scaleBearing = new BoundedQueryDeclaration(
            ordinary.Identity,
            ordinary.IndexIdentity,
            ordinary.Operations,
            ordinary.SortSupport,
            ordinary.PagingSupport,
            BoundedQueryExecutionClass.ScaleBearing,
            ordinary.SupportsDisjunction,
            ordinary.SupportsTotalCount,
            ordinary.SortFields,
            ordinary.PredicateFields,
            ordinary.ResultOperations,
            ordinary.LatestPerKeyPath);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            [scaleBearing],
            fixture.Storage.NameOverrides,
            fixture.Storage.BoundedMutations);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-012" &&
            diagnostic.Message.Contains("mixed exact and ordered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ordinary_mixed_exact_and_ordered_identity_demand_uses_server_execution_without_certifying_one_index_shape()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.GreaterThan
            },
            indexLayout: IdentityIndexLayout.Exact);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope));

        var plan = AssertPlan(result);
        Assert.Null(plan.IndexName);
        Assert.False(plan.IsScaleBearing);
    }

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
        Assert.Collection(
            plan.Order.Where(order => order.IsIdentityTieBreak),
            order => Assert.Equal("storage_scope", order.Field.Identifier),
            order => Assert.Equal(plan.DocumentIdentity.Comparison.Identifier, order.Field.Identifier));
    }

    [Fact]
    public void Global_route_uses_only_id_as_the_structural_identity_tie_break()
    {
        var scoped = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, BoundedQueryExecutionClass.Ordinary);
        var global = Resolve(scoped.Storage, binding: null, TenancyPolicy.Global);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            global.Route,
            global.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));

        Assert.Equal("id", Assert.Single(plan.Order, order => order.IsIdentityTieBreak).Path);
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
    public void Runtime_invocation_fingerprint_omits_raw_values_and_covers_query_route_scope_and_exact_utf16()
    {
        const string sensitiveValue = "tenant-secret-value";
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var alternateFixture = CreateFixture(
            PhysicalStorageForm.SharedDocuments,
            BoundedQueryExecutionClass.Ordinary);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));
        var alternatePlan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            alternateFixture.Route,
            alternateFixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson)));
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            "list-by-stimulus-type",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", sensitiveValue))],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            skip: 3,
            take: 25,
            latestPerKeyPath: "correlationId");
        var scoped = new DocumentScopeSelection("tenant-a", new StorageScope("tenant-a"), false);
        var acrossScopes = new DocumentScopeSelection(null, null, true);

        var fingerprint = PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, scoped);

        Assert.Equal(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, scoped));
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.DoesNotContain(sensitiveValue, fingerprint, StringComparison.Ordinal);
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query.Page(4, 25), plan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(new DocumentQuery(
            query.DocumentKind,
            query.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "different"))],
            query.Order,
            query.Skip,
            query.Take,
            query.Continuation,
            query.LatestPerKeyPath,
            query.ResultOperation), plan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(
            query.Select(BoundedQueryResultOperation.Count), plan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query, alternatePlan, scoped));
        Assert.NotEqual(fingerprint, PhysicalDocumentQueryInvocationFingerprint.Compute(query, plan, acrossScopes));
        Assert.NotEqual(
            PhysicalDocumentQueryInvocationFingerprint.Compute(QueryWithValue("\ud800"), plan, scoped),
            PhysicalDocumentQueryInvocationFingerprint.Compute(QueryWithValue("\ud801"), plan, scoped));

        DocumentQuery QueryWithValue(string value) => new(
            query.DocumentKind,
            query.QueryIdentity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", value))]);
    }

    [Fact]
    public void Continuation_codec_allows_page_size_changes_and_rejects_query_scope_and_token_rewriting()
    {
        var declaration = Query(
            BoundedQueryExecutionClass.ScaleBearing,
            pagingSupport: QueryPagingSupport.Cursor);
        var fixture = CreateEntityFixture(StimulusTypeIndex(), declaration);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                supportsKeysetPaging: true,
                supportsLatestPerKey: false,
                sources: [PhysicalQuerySourceKind.PrimaryProjectedColumns])));
        var upgradedPlan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                new ProviderIdentity("test-provider", "2.0.0"),
                supportsKeysetPaging: true,
                supportsLatestPerKey: false,
                sources: [PhysicalQuerySourceKind.PrimaryProjectedColumns])));
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            declaration.Identity,
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))],
            take: 10);
        var scope = new DocumentScopeSelection("tenant-a", new StorageScope("tenant-a"), false);
        var values = DocumentQueryOrderResolver.Resolve(query, plan)
            .Select((order, index) => new DocumentQueryContinuationValue(
                order.Field.ValueKind,
                DocumentQueryContinuationScalarKind.String,
                $"value-{index}"))
            .ToArray();

        var token = DocumentQueryContinuationCodec.Encode(query, plan, scope, values);

        Assert.Equal(values, DocumentQueryContinuationCodec.Decode(
            token,
            new DocumentQuery(
                query.DocumentKind,
                query.QueryIdentity,
                query.Clauses,
                query.Order,
                take: 50,
                continuation: token),
            plan,
            scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                new DocumentQuery(
                    query.DocumentKind,
                    query.QueryIdentity,
                    [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "timer"))],
                    take: 10,
                    continuation: token),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                query.ContinueAfter(token),
                plan,
                new DocumentScopeSelection("tenant-b", new StorageScope("tenant-b"), false)));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token[..^1] + (token[^1] == 'a' ? 'b' : 'a'),
                query.ContinueAfter(token[..^1] + (token[^1] == 'a' ? 'b' : 'a')),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                "not-a-groundwork-continuation",
                query.ContinueAfter("not-a-groundwork-continuation"),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(" ", query, plan, scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Encode(
                query,
                plan,
                scope,
                values.Select((value, index) => index == 0
                        ? value with
                        {
                            ScalarKind = DocumentQueryContinuationScalarKind.Int64,
                            Value = "not-an-integer"
                        }
                        : value)
                    .ToArray()));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                new DocumentQuery(
                    query.DocumentKind,
                    query.QueryIdentity,
                    query.Clauses,
                    [new DocumentQueryOrder("stimulusType", PhysicalSortDirection.Descending)],
                    take: 10,
                    continuation: token),
                plan,
                scope));
        Assert.Throws<InvalidDocumentQueryContinuationException>(() =>
            DocumentQueryContinuationCodec.Decode(
                token,
                query.ContinueAfter(token),
                upgradedPlan,
                scope));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentQueryContinuationCodec.ValidateScope(
                plan,
                new DocumentScopeSelection(null, null, true)));
    }

    [Fact]
    public async Task Runtime_explain_uses_the_same_resolution_path_and_fails_closed_for_custom_handlers()
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
            planned.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryCanonicalJson,
            certifications: [CertificationFor(planned)]);
        var store = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]);
        var invalid = new DocumentQuery(
            "workflowTriggerBinding",
            "unknown-query",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        var execution = await Assert.ThrowsAsync<InvalidOperationException>(() => store.QueryAsync(invalid));
        var explain = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExplainAsync(invalid));
        var unsupported = new List<NotSupportedException>();
        foreach (var operation in Enum.GetValues<BoundedQueryResultOperation>())
        {
            unsupported.Add(await Assert.ThrowsAsync<NotSupportedException>(() => store.ExplainAsync(
                new DocumentQuery(
                    "workflowTriggerBinding",
                    "list-by-stimulus-type",
                    [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))],
                    resultOperation: operation))));
        }

        Assert.Equal(execution.Message, explain.Message);
        Assert.All(unsupported, exception =>
            Assert.Contains(handler.Identity, exception.Message, StringComparison.Ordinal));
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
    public void NamedDeleteMutationCompilesFromAClosedBoundedPredicateAndInheritsRouteScope()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var mutation = new BoundedMutationDeclaration(
            "prune-by-stimulus-type",
            "list-by-stimulus-type",
            BoundedMutationAction.Delete());
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations: [mutation]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        var plan = Assert.Single(result.Plans);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        Assert.Equal("prune-by-stimulus-type", plan.MutationIdentity);
        Assert.Equal(BoundedMutationActionKind.Delete, plan.Action.Kind);
        Assert.Equal("list-by-stimulus-type", plan.Predicate.QueryIdentity);
        Assert.Equal("test.PrimaryProjectedColumns", plan.HandlerIdentity);
        Assert.Equal(fixture.Route.Fingerprint, plan.RouteFingerprint);
        Assert.Equal(fixture.Route.ScopePolicy, plan.Predicate.Scope.Policy);
        Assert.True(plan.Predicate.Scope.IsMandatory);
    }

    [Fact]
    public void NamedTransitionFixesTheAllowedSourceAndTargetValuesAtCompileTime()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var mutation = new BoundedMutationDeclaration(
            "revoke-http-stimuli",
            "list-by-stimulus-type",
            BoundedMutationAction.Transition(
                "stimulusType",
                ["active", "inactive"],
                "revoked"));
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations: [mutation]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        var transition = Assert.IsType<PhysicalTransitionMutationAction>(Assert.Single(result.Plans).Action);
        Assert.Equal("stimulusType", transition.Path);
        Assert.Equal(new[] { "active", "inactive" }, transition.AllowedSourceValues);
        Assert.Equal("revoked", transition.TargetValue);
        Assert.Equal(IndexValueKind.Keyword, transition.Field.ValueKind);
        Assert.Equal(Assert.Single(fixture.Route.ProjectedColumns).Column.Identifier, transition.Field.Identifier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedTransitionRejectsEnvelopeAndLinkedRelationshipFields(bool linked)
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked,
            BoundedMutationAction.Transition(linked ? "id" : "schemaVersion", ["1"], "2"));

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(linked ? PhysicalQuerySourceKind.LinkedIndex : PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-005" &&
            diagnostic.Message.Contains("content", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, PhysicalQueryFieldSource.Envelope)]
    [InlineData(true, PhysicalQueryFieldSource.LinkedRelationship)]
    public void NamedDeleteRetainsEnvelopeAndLinkedRelationshipPredicates(
        bool linked,
        PhysicalQueryFieldSource expectedSource)
    {
        var fixture = CreateIntrinsicMutationFixture(linked, BoundedMutationAction.Delete());

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(linked ? PhysicalQuerySourceKind.LinkedIndex : PhysicalQuerySourceKind.PrimaryEnvelope));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
        var plan = Assert.Single(result.Plans);
        Assert.IsType<PhysicalDeleteMutationAction>(plan.Action);
        Assert.Equal(expectedSource, Assert.Single(plan.Predicate.Predicates).Field.Source);
    }

    [Fact]
    public void MutationCompilationRejectsAnOrdinaryPredicateThatCouldScanWithoutAnIndex()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.DedicatedDocumentTable,
            BoundedQueryExecutionClass.Ordinary);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "unsafe-prune",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-MUTATION-004" &&
            diagnostic.Message.Contains("indexed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MutationRuntimeRejectsUndeclaredWorkBeforeDispatchingProviderIo()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "prune-by-stimulus-type",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            capabilities).Plans);
        var handler = new RecordingMutationHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            [new PhysicalMutationHandlerCertification(plan)]);
        var mutations = new PhysicalMutationDocumentStore(
            fixture.Route,
            storage,
            capabilities,
            [handler]);

        var completed = await mutations.ExecuteAsync(new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "operation-1",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]));

        Assert.Equal(BoundedMutationStatus.Completed, completed.Status);
        Assert.Equal(3, completed.AffectedCount);
        Assert.Equal(1, handler.ExecutionCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ExecuteAsync(new DocumentMutation(
            "workflowTriggerBinding",
            "undeclared-prune",
            "operation-2")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ExecuteAsync(new DocumentMutation(
            "workflowTriggerBinding",
            "prune-by-stimulus-type",
            "operation-3",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("undeclaredPath", "http"))])));
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public void MutationRequestFingerprintIsDeterministicForEquivalentSetPredicates()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "prune-by-stimulus-type",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var first = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "first",
            [DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["timer", "http", "http"]))]);
        var equivalent = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "second",
            [DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["http", "timer"]))]);
        var different = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "third",
            [DocumentQueryClause.Of(DocumentQueryComparison.In("stimulusType", ["http"]))]);

        var fingerprint = BoundedMutationRequestFingerprint.Create(first, plan, "tenant-a");

        Assert.Equal(fingerprint, BoundedMutationRequestFingerprint.Create(equivalent, plan, "tenant-a"));
        Assert.NotEqual(fingerprint, BoundedMutationRequestFingerprint.Create(different, plan, "tenant-a"));
        Assert.NotEqual(fingerprint, BoundedMutationRequestFingerprint.Create(first, plan, "tenant-b"));
    }

    [Fact]
    public void Mutation_request_fingerprint_uses_canonical_identity_evidence_but_not_operation_id_case()
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked: true,
            BoundedMutationAction.Delete(),
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex)).Plans);
        var retainedSpelling = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "Operation-A",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal(
                PhysicalDocumentFieldPaths.Id,
                "metric-\U00010428-\u00e9"))]);
        var equivalentSpelling = new DocumentMutation(
            "workflowTriggerBinding",
            plan.MutationIdentity,
            "operation-a",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal(
                PhysicalDocumentFieldPaths.Id,
                "METRIC-\U00010400-\u00c9"))]);

        Assert.NotEqual(retainedSpelling.OperationId, equivalentSpelling.OperationId);
        Assert.Equal(
            BoundedMutationRequestFingerprint.Create(retainedSpelling, plan, "tenant-a"),
            BoundedMutationRequestFingerprint.Create(equivalentSpelling, plan, "tenant-a"));
    }

    [Fact]
    public void Bounded_mutation_selection_receives_the_same_canonical_identity_values_as_replay()
    {
        var fixture = CreateIntrinsicMutationFixture(
            linked: true,
            BoundedMutationAction.Delete(),
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex)).Plans);
        var comparison = DocumentQueryComparison.In(
            PhysicalDocumentFieldPaths.Id,
            ["metric-\U00010428-\u00e9", "METRIC-\U00010400-\u00c9"]);

        var bound = PhysicalDocumentIdentityQuery.Bind(plan.Predicate, comparison);

        Assert.Equal(plan.Predicate.DocumentIdentity, bound.Identity);
        var value = Assert.Single(bound.Values);
        Assert.IsType<PhysicalQueryIdentityValue.Exact>(value);
        Assert.Equal("00004D00004500005400005200004900004300002D01040000002D0000C9", value.ComparisonKey);
        Assert.Equal(
            "61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181",
            ((PhysicalQueryIdentityValue.Exact)value).LookupKey);
    }

    [Fact]
    public void MutationRequestFingerprintIsStableAcrossProviderVersionUpgrades()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "prune-by-stimulus-type",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete())
            ]);
        var firstPlan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(
                new ProviderIdentity("test-provider", "1.0.0"),
                PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var upgradedPlan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(
                new ProviderIdentity("test-provider", "2.0.0"),
                PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        var mutation = new DocumentMutation(
            "workflowTriggerBinding",
            firstPlan.MutationIdentity,
            "rolling-upgrade",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("stimulusType", "http"))]);

        Assert.Equal(
            BoundedMutationRequestFingerprint.Create(mutation, firstPlan, "tenant-a"),
            BoundedMutationRequestFingerprint.Create(mutation, upgradedPlan, "tenant-a"));
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
    public void Runtime_seam_rejects_an_incomplete_identity_binding_certification()
    {
        var fixture = CreateIdentityQueryFixture(
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryEnvelope);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var fields = PlanFieldIdentifiers(plan);
        fields.Remove(plan.DocumentIdentity.Lookup.Path);
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryEnvelope,
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
    public void ScaleBearingQueryPlansDeclaredResidualPredicatesOnTheIndexedPrimaryRoute()
    {
        var indexedPredicate = new BoundedQueryPredicateField(
            "stimulusType",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var residualPredicate = new BoundedQueryResidualPredicateField(
            "status",
            IndexValueKind.Keyword,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In
            });
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In
            },
            QuerySortSupport.Ascending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields: [indexedPredicate],
            residualPredicateFields: [residualPredicate]);
        var fixture = CreateEntityFixture(StimulusTypeIndex(), query);

        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.Equal(PhysicalQueryAccessKind.PrimaryProjectedColumns, plan.AccessKind);
        Assert.NotNull(plan.IndexName);
        Assert.Empty(plan.RequiredEqualityPrefixPaths);
        Assert.Collection(
            plan.Predicates,
            predicate =>
            {
                Assert.Equal("stimulusType", predicate.Path);
                Assert.False(predicate.IsResidual);
            },
            predicate =>
            {
                Assert.Equal("status", predicate.Path);
                Assert.True(predicate.IsResidual);
                Assert.Equal(IndexValueKind.Keyword, predicate.Field.ValueKind);
                Assert.Equal(
                    new[] { PortableQueryOperation.Equal, PortableQueryOperation.In },
                    predicate.Operations.Order());
            });
        Assert.Contains("\"residual\":true", PhysicalQueryPlanSerializer.Serialize(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void ResidualPredicateShapeParticipatesInThePlanFingerprint()
    {
        PlanningFixture Fixture(IReadOnlySet<PortableQueryOperation> residualOperations)
        {
            var query = new BoundedQueryDeclaration(
                "list-by-stimulus-type",
                "by-stimulus-type",
                new HashSet<PortableQueryOperation>
                {
                    PortableQueryOperation.Equal,
                    PortableQueryOperation.In
                },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing,
                predicateFields:
                [
                    new BoundedQueryPredicateField(
                        "stimulusType",
                        new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
                ],
                residualPredicateFields:
                [
                    new BoundedQueryResidualPredicateField(
                        "status",
                        IndexValueKind.Keyword,
                        residualOperations)
                ]);
            return CreateEntityFixture(StimulusTypeIndex(), query);
        }

        var equalityFixture = Fixture(new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });
        var membershipFixture = Fixture(new HashSet<PortableQueryOperation>
        {
            PortableQueryOperation.Equal,
            PortableQueryOperation.In
        });
        var equality = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            equalityFixture.Route,
            equalityFixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var membership = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            membershipFixture.Route,
            membershipFixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));

        Assert.NotEqual(equality.Fingerprint, membership.Fingerprint);
    }

    [Fact]
    public async Task RequiredResidualPredicateMustBeSuppliedBeforeHandlerDispatch()
    {
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                    isRequired: true)
            ]);
        var fixture = CreateEntityFixture(StimulusTypeIndex(), query);
        var capabilities = Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            capabilities));
        var handler = new RecordingHandler(
            plan.HandlerIdentity,
            PhysicalQuerySourceKind.PrimaryProjectedColumns,
            certifications: [CertificationFor(plan)]);
        var store = new PhysicalQueryDocumentStore(
            fixture.Route,
            fixture.Storage,
            capabilities,
            [handler]);
        var missing = new DocumentQuery(
            "workflowTriggerBinding",
            query.Identity);
        var supplied = missing.Where(DocumentQueryClause.Of(
            DocumentQueryComparison.Equal("status", "ready")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.QueryAsync(missing));
        await store.QueryAsync(supplied);

        Assert.Contains("status", exception.Message, StringComparison.Ordinal);
        Assert.True(plan.Predicates.Single(predicate => predicate.Path == "status").IsRequired);
        Assert.Equal(plan, handler.LastPlan);
    }

    [Fact]
    public void ResidualEnvelopePredicateMustUseTheIntrinsicValueKind()
    {
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary,
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    PhysicalDocumentFieldPaths.Version,
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryCanonicalJson));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-010" &&
            diagnostic.Message.Contains(PhysicalDocumentFieldPaths.Version, StringComparison.Ordinal));
    }

    [Fact]
    public void ScaleBearingResidualPredicateUsesALinkedProjectionBeforeHydration()
    {
        var logicalIndex = StimulusTypeIndex();
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
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
            [
                new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String),
                new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)
            ],
            linkedProjectionLogicalName: "workflow_trigger_binding_index");
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [logicalIndex],
            [query]);
        var fixture = Resolve(storage, null);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.LinkedIndex));

        var plan = AssertPlan(result);
        Assert.Equal(PhysicalQueryAccessKind.LinkedIndexThenPrimary, plan.AccessKind);
        Assert.Equal(
            ExecutableStorageObjectRole.LinkedIndexStorage,
            plan.Predicates.Single(predicate => predicate.Path == "status").Field.Target);
    }

    [Fact]
    public void BoundedMutationRejectsAQueryWithResidualPredicates()
    {
        var query = new BoundedQueryDeclaration(
            "list-by-stimulus-type",
            "by-stimulus-type",
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "stimulusType",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                "workflow_trigger_bindings",
                [
                    new ProjectedColumnDefinition("stimulusType", "stimulusType", PortablePhysicalType.String),
                    new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)
                ],
                indexes:
                [
                    new PhysicalIndexDefinition(
                        "by-stimulus-type",
                        [
                            new PhysicalIndexColumnDefinition("storage_scope", 0),
                            new PhysicalIndexColumnDefinition("stimulusType", 1)
                        ])
                ])),
            [StimulusTypeIndex()],
            [query],
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "delete-by-stimulus-type",
                    query.Identity,
                    BoundedMutationAction.Delete())
            ]);
        var fixture = Resolve(storage, null);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-006");
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
                Assert.Equal("storageScope", order.Path);
                Assert.Equal(PhysicalSortDirection.Ascending, order.Direction);
                Assert.True(order.IsIdentityTieBreak);
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
    public void RuntimeOrderPrefixRetainsTheRemainingDeclaredCompoundOrderBeforeTieBreaks()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-stimulus-created",
            [new IndexField("stimulusType"), new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var declaration = new BoundedQueryDeclaration(
            "list-by-stimulus-created",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Both,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)
            ]);
        var fixture = CreateEntityFixture(logicalIndex, declaration);
        var plan = AssertPlan(PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)));
        var query = new DocumentQuery(
            "workflowTriggerBinding",
            declaration.Identity,
            order: [new DocumentQueryOrder("stimulusType", PhysicalSortDirection.Ascending)]);

        var resolved = DocumentQueryOrderResolver.Resolve(query, plan);

        Assert.Equal(
            ["stimulusType", "createdAt", "storageScope", "id"],
            resolved.Select(order => order.Path));
        Assert.Equal(
            [
                PhysicalSortDirection.Ascending,
                PhysicalSortDirection.Descending,
                PhysicalSortDirection.Ascending,
                PhysicalSortDirection.Ascending
            ],
            resolved.Select(order => order.Direction));
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
    public void LatestPerKeyRejectsCursorPagingEvenWhenTheProviderSupportsBothCapabilitiesSeparately()
    {
        var query = Query(
            BoundedQueryExecutionClass.Ordinary,
            pagingSupport: QueryPagingSupport.Cursor,
            latestPerKeyPath: "stimulusType",
            sortFields: [new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Descending)]);
        var fixture = CreateFixture(PhysicalStorageForm.DedicatedDocumentTable, query);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                supportsKeysetPaging: true,
                supportsLatestPerKey: true,
                sources: [PhysicalQuerySourceKind.PrimaryCanonicalJson]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-008" &&
            diagnostic.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LatestPerKeyRequiresTheGroupingPathToLeadTheDeclaredOrder()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category-created-stimulus",
            [
                new IndexField("category"),
                new IndexField("createdAt", IndexValueKind.DateTime),
                new IndexField("stimulusType")
            ],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "latest-by-stimulus",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.Both,
            QueryPagingSupport.Offset,
            BoundedQueryExecutionClass.Ordinary,
            sortFields:
            [
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Ascending),
                new BoundedQuerySortField("stimulusType", PhysicalSortDirection.Ascending)
            ],
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "category",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ],
            latestPerKeyPath: "stimulusType");
        var fixture = CreateEntityFixture(logicalIndex, query);

        var result = PhysicalQueryPlanCompiler.Compile(
            fixture.Route,
            fixture.Storage,
            CapabilitiesWithPaging(
                supportsKeysetPaging: true,
                supportsLatestPerKey: true,
                sources: [PhysicalQuerySourceKind.PrimaryCanonicalJson]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-QUERY-008" &&
            diagnostic.Message.Contains("lead", StringComparison.OrdinalIgnoreCase));
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

    private static PlanningFixture CreateIntrinsicMutationFixture(
        bool linked,
        BoundedMutationAction action,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var path = linked ? "id" : "schemaVersion";
        var index = new LogicalIndexDeclaration(
            $"by-{path}",
            [new IndexField(path)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            $"list-by-{path}",
            index.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing);
        var physicalIndex = new PhysicalIndexDefinition(
            index.Identity,
            linked
                ?
                [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("id_lookup_key", 1),
                    new PhysicalIndexColumnDefinition("id_comparison_key", 2)
                ]
                :
                [
                    new PhysicalIndexColumnDefinition("storage_scope", 0),
                    new PhysicalIndexColumnDefinition("schema_version", 1)
                ],
            target: linked
                ? PhysicalIndexStorageTarget.LinkedIndexStorage
                : PhysicalIndexStorageTarget.PrimaryStorage);
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "intrinsic_documents",
            indexes: [physicalIndex],
            linkedProjectedColumns: linked
                ? [new ProjectedColumnDefinition("unused", "unused", PortablePhysicalType.String)]
                : null,
            linkedProjectionLogicalName: linked ? "intrinsic_index" : null);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            [query],
            boundedMutations: [new BoundedMutationDeclaration("mutate-intrinsic", query.Identity, action)]);
        return Resolve(storage, null, identityCasePolicy: identityCasePolicy);
    }

    private static PlanningFixture CreateIdentityQueryFixture(
        IReadOnlySet<PortableQueryOperation> operations,
        QuerySortSupport sortSupport = QuerySortSupport.None,
        IReadOnlyList<BoundedQuerySortField>? sortFields = null,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
        PhysicalQuerySourceKind source = PhysicalQuerySourceKind.PrimaryEnvelope,
        IdentityIndexLayout? indexLayout = null)
    {
        var index = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-id",
            index.Identity,
            operations,
            sortSupport,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary,
            sortFields: sortFields);
        var layout = indexLayout ?? (operations.All(IsExactIdentityOperation)
            ? IdentityIndexLayout.Exact
            : IdentityIndexLayout.Ordered);
        var identityColumns = layout switch
        {
            IdentityIndexLayout.Exact => new[] { "id_lookup_key", "id_comparison_key" },
            IdentityIndexLayout.Ordered => ["id_comparison_key"],
            IdentityIndexLayout.Original => ["id"],
            _ => throw new ArgumentOutOfRangeException(nameof(indexLayout), indexLayout, null)
        };
        var physicalColumns = new List<PhysicalIndexColumnDefinition>
        {
            new("storage_scope", 0)
        };
        physicalColumns.AddRange(identityColumns.Select((column, order) =>
            new PhysicalIndexColumnDefinition(column, order + 1)));
        var linked = source == PhysicalQuerySourceKind.LinkedIndex;
        var definition = PhysicalTableDefinition.DedicatedDocumentTable(
            "identity_documents",
            indexes:
            [
                new PhysicalIndexDefinition(
                    index.Identity,
                    physicalColumns,
                    target: linked
                        ? PhysicalIndexStorageTarget.LinkedIndexStorage
                        : PhysicalIndexStorageTarget.PrimaryStorage)
            ],
            linkedProjectedColumns: linked
                ? [new ProjectedColumnDefinition("unused", "unused", PortablePhysicalType.String)]
                : null,
            linkedProjectionLogicalName: linked ? "identity_index" : null);
        var storage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(definition),
            [index],
            [query]);
        return Resolve(storage, null, identityCasePolicy: identityCasePolicy);
    }

    private static bool IsExactIdentityOperation(PortableQueryOperation operation) => operation is
        PortableQueryOperation.Equal or
        PortableQueryOperation.In or
        PortableQueryOperation.NotEqual;

    private enum IdentityIndexLayout
    {
        Exact,
        Ordered,
        Original
    }

    private static PlanningFixture CreateEntityFixture(
        LogicalIndexDeclaration logicalIndex,
        BoundedQueryDeclaration query)
    {
        var projections = logicalIndex.Fields
            .Select(field => new ProjectedColumnDefinition(
                field.Path,
                field.Path,
                ToPortableType(logicalIndex.GetValueKind(field))))
            .Concat(query.ResidualPredicateFields.Select(field => new ProjectedColumnDefinition(
                field.Path,
                field.Path,
                ToPortableType(field.ValueKind))))
            .GroupBy(column => column.Path, StringComparer.Ordinal)
            .Select(group => group.First())
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
        if (query.PagingSupport == QueryPagingSupport.Cursor &&
            logicalIndex.Fields.All(field => field.Path != PhysicalDocumentFieldPaths.Id))
        {
            columns.Add(new PhysicalIndexColumnDefinition(
                new DocumentEnvelopeDefinition().IdLookupKeyColumn,
                columns.Count));
        }
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

    private static PlanningFixture Resolve(
        StorageUnitPhysicalStorage storage,
        SharedStorageBinding? binding,
        TenancyPolicy? tenancy = null,
        StringIdentityCasePolicy identityCasePolicy = StringIdentityCasePolicy.Ordinal)
    {
        var template = SampleManifests.MetadataManifest();
        var unit = template.StorageUnits.Single() with
        {
            Identity = new StorageUnitIdentity("workflowTriggerBinding"),
            IdentityPolicy = IdentityPolicy.StringId(stringCasePolicy: identityCasePolicy),
            Tenancy = tenancy ?? template.StorageUnits.Single().Tenancy,
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
        Capabilities(new ProviderIdentity("test-provider", "1.0.0"), sources);

    private static PhysicalQueryPlannerCapabilities Capabilities(
        ProviderIdentity provider,
        params PhysicalQuerySourceKind[] sources) =>
        CapabilitiesWithPaging(provider, true, true, sources);

    private static PhysicalQueryPlannerCapabilities CapabilitiesWithPaging(
        bool supportsKeysetPaging,
        bool supportsLatestPerKey,
        params PhysicalQuerySourceKind[] sources) =>
        CapabilitiesWithPaging(
            new ProviderIdentity("test-provider", "1.0.0"),
            supportsKeysetPaging,
            supportsLatestPerKey,
            sources);

    private static PhysicalQueryPlannerCapabilities CapabilitiesWithPaging(
        ProviderIdentity provider,
        bool supportsKeysetPaging,
        bool supportsLatestPerKey,
        params PhysicalQuerySourceKind[] sources) =>
        new(
            provider,
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
        plan.RequiredFields
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

    private sealed class RecordingMutationHandler(
        string identity,
        PhysicalQuerySourceKind source,
        IReadOnlyList<PhysicalMutationHandlerCertification> certifications) : IPhysicalDocumentMutationHandler
    {
        public string Identity { get; } = identity;
        public PhysicalQuerySourceKind Source { get; } = source;
        public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; } =
            Enum.GetValues<PortableQueryOperation>().ToHashSet();
        public IReadOnlySet<BoundedMutationActionKind> SupportedActions { get; } =
            Enum.GetValues<BoundedMutationActionKind>().ToHashSet();
        public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; } =
            new Dictionary<string, string>();
        public IReadOnlyList<PhysicalMutationHandlerCertification> Certifications { get; } = certifications;
        public bool SupportsCompoundPredicates => true;
        public bool SupportsDisjunction => true;
        public int ExecutionCount { get; private set; }

        public Task<BoundedMutationResult> ExecuteAsync(
            DocumentMutation mutation,
            PhysicalMutationPlan plan,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(BoundedMutationResult.Completed(3));
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
