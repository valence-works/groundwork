using System.Text;
using System.Reflection;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Transactions;
using Groundwork.Core.Text;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbPhysicalStorageModelTests
{
    [Fact]
    public void ResidualPredicateCompilesIntoTheNativeMongoFilterWithoutBecomingIndexKeyEvidence()
    {
        var model = ResidualHistoryModel();
        var route = Assert.Single(model.Routes);
        var storage = Assert.Single(model.Manifest.StorageUnits).PhysicalStorage!;
        var capabilities = MongoDbPhysicalMutationCapabilities.Create(
            route,
            storage,
            model.Provider,
            MongoDbPhysicalQueryHandler.Operations);
        var compilation = PhysicalQueryPlanCompiler.Compile(route, storage, capabilities);
        Assert.True(
            compilation.IsValid,
            string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var plan = Assert.Single(compilation.Plans);
        var query = new DocumentQuery(
            "configurationDocument",
            "history-by-created-at",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "ready"))],
            [new DocumentQueryOrder("createdAt", PhysicalSortDirection.Descending)],
            take: 10);

        var filter = Render(MongoDbPhysicalQueryHandler.BuildFilter(
            query,
            plan,
            DocumentScopeSelection.Global,
            storage,
            route));

        var residual = plan.Predicates.Single(predicate => predicate.Path == "status");
        var version = plan.Predicates.Single(predicate =>
            predicate.Path == PhysicalDocumentFieldPaths.Version);
        Assert.True(residual.IsResidual);
        Assert.Equal(route.Envelope.Version.Identifier, version.Field.Identifier);
        Assert.NotNull(plan.IndexName);
        Assert.Contains(residual.Field.Identifier, filter.ToJson(), StringComparison.Ordinal);
        Assert.Contains("ready", filter.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("status", plan.LogicalIndexPaths);
    }

    [Fact]
    public void Exact_identity_filter_binds_equivalent_unicode_spelling_to_the_same_plan_evidence()
    {
        var model = IdentityModel();
        var route = Assert.Single(model.Routes);
        var storage = Assert.Single(model.Manifest.StorageUnits).PhysicalStorage!;
        var capabilities = MongoDbPhysicalMutationCapabilities.Create(
            route,
            storage,
            model.Provider,
            MongoDbPhysicalQueryHandler.Operations);
        var plan = Assert.Single(PhysicalQueryPlanCompiler.Compile(route, storage, capabilities).Plans);
        var scope = DocumentScopeSelection.Global;

        var lower = Render(MongoDbPhysicalQueryHandler.BuildFilter(
            IdentityQuery("metric-\U00010428-\u00e9"), plan, scope, storage, route));
        var upper = Render(MongoDbPhysicalQueryHandler.BuildFilter(
            IdentityQuery("METRIC-\U00010400-\u00c9"), plan, scope, storage, route));

        Assert.Equal(lower, upper);
        Assert.Contains("61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181", lower.ToJson(), StringComparison.Ordinal);
        Assert.Contains("00004D00004500005400005200004900004300002D01040000002D0000C9", lower.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_identity_mutation_filter_consumes_the_same_plan_evidence_as_query_execution()
    {
        var model = IdentityModel(includeMutation: true);
        var plan = Assert.Single(model.MutationBindingsByStorageUnit.Values.SelectMany(bindings => bindings)).Plan;

        var lower = Render(MongoDbPhysicalDocumentMutationHandler.BuildIdentityMutationFilter(
            Assert.Single(Assert.Single(IdentityQuery("metric-\U00010428-\u00e9").Clauses).Comparisons),
            plan));
        var upper = Render(MongoDbPhysicalDocumentMutationHandler.BuildIdentityMutationFilter(
            Assert.Single(Assert.Single(IdentityQuery("METRIC-\U00010400-\u00c9").Clauses).Comparisons),
            plan));

        Assert.Equal(lower, upper);
        Assert.Contains("61c4070c8bb733ab75c6a4366219266bcf058446787a62365c57dd598de56181", lower.ToJson(), StringComparison.Ordinal);
        Assert.Contains("00004D00004500005400005200004900004300002D01040000002D0000C9", lower.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_identity_mutation_selector_certifies_and_indexes_the_executed_projected_evidence()
    {
        var model = IdentityModel(includeMutation: true);
        var route = Assert.Single(model.Routes);
        var binding = Assert.Single(model.MutationBindingsByStorageUnit.Values.SelectMany(bindings => bindings));

        Assert.Equal(
            [
                route.Envelope.DocumentKind.Identifier,
                route.Envelope.StorageScope.Identifier,
                route.Envelope.Identity.LookupKey.Identifier,
                route.Envelope.Identity.ComparisonKey.Identifier
            ],
            binding.Schema.Primary.IndexKeys.Names);
        Assert.Equal(
            route.Envelope.Identity.LookupKey.Identifier,
            binding.Certification.Primary.FieldIdentifiers[PhysicalDocumentIdentityFieldPaths.Lookup]);
        Assert.Equal(
            route.Envelope.Identity.ComparisonKey.Identifier,
            binding.Certification.Primary.FieldIdentifiers[PhysicalDocumentIdentityFieldPaths.Comparison]);
        Assert.DoesNotContain(PhysicalDocumentFieldPaths.Id, binding.Certification.Primary.FieldIdentifiers.Keys);
        Assert.DoesNotContain(route.Envelope.Identity.OriginalId.Identifier, binding.Schema.Primary.IndexKeys.Names);
    }

    [Theory]
    [InlineData("wrong-target")]
    [InlineData("wrong-index")]
    [InlineData("collection-scan")]
    public void Native_mutation_plan_inspector_fails_closed_on_target_or_index_drift(string drift)
    {
        var binding = Assert.Single(
            IdentityModel(includeMutation: true).MutationBindingsByStorageUnit.Values.SelectMany(bindings => bindings));
        var selector = binding.Schema.Primary;
        var stage = drift == "collection-scan"
            ? new BsonDocument("stage", "COLLSCAN")
            : new BsonDocument
            {
                ["stage"] = "IXSCAN",
                ["indexName"] = drift == "wrong-index" ? "wrong_index" : selector.Index.Identifier
            };
        var explanation = new BsonDocument
        {
            ["queryPlanner"] = new BsonDocument
            {
                ["namespace"] = $"groundwork.{(drift == "wrong-target" ? "wrong_collection" : selector.StorageObject.Identifier)}",
                ["winningPlan"] = stage
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            MongoDbNativeMutationPlanInspector.Inspect(
                explanation,
                "groundwork",
                selector,
                new BsonDocument()));
    }

    [Theory]
    [InlineData(PortableQueryOperation.GreaterThan)]
    [InlineData(PortableQueryOperation.StartsWith)]
    public void Ordered_identity_mutation_selector_certifies_only_the_comparison_evidence(
        PortableQueryOperation operation)
    {
        var model = IdentityModel(operation, includeMutation: true);
        var route = Assert.Single(model.Routes);
        var binding = Assert.Single(model.MutationBindingsByStorageUnit.Values.SelectMany(bindings => bindings));

        Assert.Equal(
            [
                route.Envelope.DocumentKind.Identifier,
                route.Envelope.StorageScope.Identifier,
                route.Envelope.Identity.ComparisonKey.Identifier
            ],
            binding.Schema.Primary.IndexKeys.Names);
        Assert.Equal(
            route.Envelope.Identity.ComparisonKey.Identifier,
            binding.Certification.Primary.FieldIdentifiers[PhysicalDocumentIdentityFieldPaths.Comparison]);
        Assert.DoesNotContain(PhysicalDocumentIdentityFieldPaths.Lookup, binding.Certification.Primary.FieldIdentifiers.Keys);
        Assert.DoesNotContain(PhysicalDocumentFieldPaths.Id, binding.Certification.Primary.FieldIdentifiers.Keys);
        Assert.DoesNotContain(route.Envelope.Identity.OriginalId.Identifier, binding.Schema.Primary.IndexKeys.Names);
    }

    [Fact]
    public void Identity_contains_surfaces_the_core_identity_diagnostic_before_provider_execution()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            IdentityModel(PortableQueryOperation.Contains, includeMutation: true));

        Assert.Contains("GW-QUERY-011", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GW-QUERY-003", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compound_identity_contains_surfaces_the_core_diagnostic_for_queries_and_mutations(
        bool includeMutation)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var model = CompoundIdentityContainsModel(includeMutation);
            if (!includeMutation)
            {
                _ = new MongoDbPhysicalDocumentStore(
                    new MongoClient("mongodb://localhost").GetDatabase("groundwork_identity_diagnostic"),
                    model,
                    DocumentStoreAccess.Global);
            }
        });

        Assert.Contains("GW-QUERY-011", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Contains", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GW-QUERY-003", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("regular-expression semantics", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortableQueryOperation.GreaterThan)]
    [InlineData(PortableQueryOperation.StartsWith)]
    public void Ordered_identity_filter_binds_equivalent_unicode_spelling_to_the_same_comparison_key(
        PortableQueryOperation operation)
    {
        var model = IdentityModel(operation == PortableQueryOperation.StartsWith
            ? PortableQueryOperation.GreaterThan
            : operation);
        var route = Assert.Single(model.Routes);
        var storage = Assert.Single(model.Manifest.StorageUnits).PhysicalStorage!;
        var capabilities = MongoDbPhysicalMutationCapabilities.Create(
            route,
            storage,
            model.Provider,
            MongoDbPhysicalQueryHandler.Operations);
        var plan = Assert.Single(PhysicalQueryPlanCompiler.Compile(route, storage, capabilities).Plans);

        var lower = Render(MongoDbPhysicalQueryHandler.BuildFilter(
            IdentityQuery("metric-\U00010428-\u00e9", operation),
            plan,
            DocumentScopeSelection.Global,
            storage,
            route));
        var upper = Render(MongoDbPhysicalQueryHandler.BuildFilter(
            IdentityQuery("METRIC-\U00010400-\u00c9", operation),
            plan,
            DocumentScopeSelection.Global,
            storage,
            route));

        Assert.Equal(lower, upper);
        Assert.Contains("00004D00004500005400005200004900004300002D01040000002D0000C9", lower.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(plan.DocumentIdentity.Lookup.Identifier, lower.ToJson(), StringComparison.Ordinal);
        if (operation == PortableQueryOperation.StartsWith)
        {
            Assert.Contains("$gte", lower.ToJson(), StringComparison.Ordinal);
            Assert.Contains("$lt", lower.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("$regularExpression", lower.ToJson(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Empty_identity_prefix_uses_a_lower_only_indexed_range_for_queries_and_mutations()
    {
        var model = IdentityModel(PortableQueryOperation.StartsWith, includeMutation: true);
        var route = Assert.Single(model.Routes);
        var storage = Assert.Single(model.Manifest.StorageUnits).PhysicalStorage!;
        var capabilities = MongoDbPhysicalMutationCapabilities.Create(
            route,
            storage,
            model.Provider,
            MongoDbPhysicalQueryHandler.Operations);
        var queryPlan = Assert.Single(PhysicalQueryPlanCompiler.Compile(route, storage, capabilities).Plans);
        var comparison = Assert.Single(Assert.Single(
            IdentityQuery(string.Empty, PortableQueryOperation.StartsWith).Clauses).Comparisons);

        var queryFilter = Render(MongoDbPhysicalQueryHandler.BuildFilter(
            IdentityQuery(string.Empty, PortableQueryOperation.StartsWith),
            queryPlan,
            DocumentScopeSelection.Global,
            storage,
            route));
        var mutationFilter = Render(MongoDbPhysicalDocumentMutationHandler.BuildIdentityMutationFilter(
            comparison,
            Assert.Single(model.MutationBindingsByStorageUnit.Values.SelectMany(bindings => bindings)).Plan));

        foreach (var filter in new[] { queryFilter, mutationFilter })
        {
            var json = filter.ToJson();
            Assert.Contains("id_comparison_key", json, StringComparison.Ordinal);
            Assert.Contains("$gte", json, StringComparison.Ordinal);
            Assert.DoesNotContain("$lt", json, StringComparison.Ordinal);
            Assert.DoesNotContain("$regularExpression", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Compilation_preserves_all_three_forms_and_uses_resolved_collection_names()
    {
        var manifest = Manifest();
        var names = new DelegatePhysicalNamePolicy(context => $"app_{context.FeatureDefaultLogicalName}");

        var model = MongoDbPhysicalStorageModel.Compile(manifest, MongoDbTestManifests.Provider, names);

        Assert.Equal(3, model.Routes.Count);
        Assert.Collection(
            model.Routes.OrderBy(route => route.StorageUnit.Value),
            route =>
            {
                Assert.Equal(PhysicalStorageForm.DedicatedDocumentTable, route.Form);
                Assert.Equal("app_orders", route.PrimaryStorage.Name.Identifier);
            },
            route =>
            {
                Assert.Equal(PhysicalStorageForm.SharedDocuments, route.Form);
                Assert.Equal("app_documents", route.PrimaryStorage.Name.Identifier);
                Assert.Equal("app_profiles_lookup", route.LinkedIndexStorage!.Name.Identifier);
            },
            route =>
            {
                Assert.Equal(PhysicalStorageForm.PhysicalEntityTable, route.Form);
                Assert.Equal("app_tickets", route.PrimaryStorage.Name.Identifier);
            });
        Assert.Equal(model.Routes, model.Target.Routes);
    }

    [Fact]
    public void Compilation_rejects_resolved_fields_that_collide_with_provider_owned_storage()
    {
        var names = new DelegatePhysicalNamePolicy(context =>
            context.ObjectKind == PhysicalObjectKind.EnvelopeField && context.FeatureDefaultLogicalName == "id"
                ? "_id"
                : context.FeatureDefaultLogicalName);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalStorageModel.Compile(Manifest(), MongoDbTestManifests.Provider, names));

        Assert.Contains("_id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider-owned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compilation_rejects_resolved_collections_that_collide_with_provider_infrastructure()
    {
        var names = new DelegatePhysicalNamePolicy(context =>
            context.ObjectKind == PhysicalObjectKind.PrimaryStorage &&
            context.FeatureDefaultLogicalName == "orders"
                ? "groundwork_physical_schema_state"
                : context.FeatureDefaultLogicalName);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalStorageModel.Compile(Manifest(), MongoDbTestManifests.Provider, names));

        Assert.Contains("groundwork_physical_schema_state", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider-owned infrastructure", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("logical-path")]
    [InlineData("collection-override")]
    [InlineData("provider-field")]
    [InlineData("provider-index")]
    public void Compilation_reserves_mutation_binding_storage_across_the_complete_naming_matrix(
        string surface)
    {
        const string reserved = "_groundwork_mutation_bindings";
        if (surface == "logical-path")
        {
            var logicalPathException = Assert.Throws<InvalidOperationException>(() => MutationModel(
                PortablePhysicalType.String,
                IndexValueKind.Keyword,
                $"details.{reserved}"));
            Assert.Contains(reserved, logicalPathException.Message, StringComparison.Ordinal);
            Assert.Contains("reserved", logicalPathException.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }
        var template = MutationModel(
            PortablePhysicalType.String,
            IndexValueKind.Keyword,
            "status");
        var manifest = template.Manifest;
        IPhysicalNamePolicy? names = null;
        if (surface == "collection-override")
        {
            var unit = Assert.Single(manifest.StorageUnits);
            var storage = unit.PhysicalStorage!;
            manifest = manifest with
            {
                StorageUnits =
                [
                    unit with
                    {
                        PhysicalStorage = new StorageUnitPhysicalStorage(
                            storage.ProvisioningMode,
                            storage.Policy,
                            storage.LogicalIndexes,
                            storage.BoundedQueries,
                            storage.NameOverrides.Append(new PhysicalObjectNameOverride(
                                PhysicalObjectKind.PrimaryStorage,
                                "work_items",
                                reserved)).ToArray(),
                            storage.BoundedMutations)
                    }
                ]
            };
        }
        else if (surface is "provider-field" or "provider-index")
        {
            var target = surface == "provider-field"
                ? PhysicalObjectKind.ProjectedField
                : PhysicalObjectKind.PhysicalIndex;
            names = new DelegatePhysicalNamePolicy(context =>
                context.ObjectKind == target ? reserved : context.FeatureDefaultLogicalName);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDbPhysicalStorageModel.Compile(manifest, namePolicy: names));

        Assert.Contains(reserved, exception.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PortableQueryOperation.NotContains)]
    [InlineData(PortableQueryOperation.StartsWith)]
    public void Query_compilation_preserves_provider_diagnostics_for_case_insensitive_regex_operations(
        PortableQueryOperation operation)
    {
        var model = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            operations: new HashSet<PortableQueryOperation> { operation });

        var exception = Assert.Throws<InvalidOperationException>(() => new MongoDbPhysicalDocumentStore(
            new MongoClient("mongodb://localhost").GetDatabase("groundwork_provider_diagnostic"),
            model,
            DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains(operation.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("regular-expression semantics", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GW-QUERY-011", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortableQueryOperation.Contains)]
    [InlineData(PortableQueryOperation.NotContains)]
    [InlineData(PortableQueryOperation.StartsWith)]
    public void Mutation_compilation_rejects_scale_bearing_case_insensitive_regex_operations(
        PortableQueryOperation operation)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => MutationModel(
            PortablePhysicalType.String,
            IndexValueKind.String,
            "status",
            new HashSet<PortableQueryOperation> { operation }));

        Assert.Contains(operation.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("GW-QUERY-003", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equivalent_mutation_selectors_emit_one_provider_schema_definition()
    {
        var model = MongoDbBoundedMutationTests.Model(PhysicalStorageForm.PhysicalEntityTable);

        Assert.Equal(2, model.MutationBindingsByStorageUnit["workItem"].Count);
        Assert.Single(model.Target.ProviderDefinitions.Where(definition =>
            definition.Kind.Contains("selector", StringComparison.Ordinal)));
    }

    [Fact]
    public void Provider_normalization_applies_MongoDB_byte_limits_to_unicode_names()
    {
        var logicalName = string.Concat(Enumerable.Repeat("😀", 60));

        var normalized = MongoDbPhysicalNameNormalizer.Instance.Normalize(new ProviderPhysicalNameContext(
            new StorageUnitIdentity("workItem"),
            PhysicalObjectKind.PhysicalIndex,
            logicalName));

        Assert.NotEqual(logicalName, normalized);
        Assert.InRange(Encoding.UTF8.GetByteCount(normalized), 1, 120);
    }

    [Fact]
    public void Provider_normalization_truncates_unicode_letters_by_UTF8_bytes()
    {
        var logicalName = string.Concat(Enumerable.Repeat("界", 100));

        var normalized = MongoDbPhysicalNameNormalizer.Instance.Normalize(new ProviderPhysicalNameContext(
            new StorageUnitIdentity("workItem"),
            PhysicalObjectKind.PhysicalIndex,
            logicalName));

        Assert.NotEqual(logicalName, normalized);
        Assert.InRange(Encoding.UTF8.GetByteCount(normalized), 1, 120);
        Assert.EndsWith("_57c42d71b2a0", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_normalization_is_stable_and_collision_resistant_for_mixed_and_combining_unicode()
    {
        var names = new[]
        {
            string.Concat(Enumerable.Repeat("界A", 80)),
            string.Concat(Enumerable.Repeat("e\u0301", 100)),
            string.Concat(Enumerable.Repeat("界A", 79)) + "B"
        };
        var normalized = names.Select(name => MongoDbPhysicalNameNormalizer.Instance.Normalize(
            new ProviderPhysicalNameContext(new StorageUnitIdentity("workItem"), PhysicalObjectKind.PhysicalIndex, name)))
            .ToArray();

        Assert.All(normalized, name => Assert.InRange(Encoding.UTF8.GetByteCount(name), 1, 120));
        Assert.Equal(normalized, names.Select(name => MongoDbPhysicalNameNormalizer.Instance.Normalize(
            new ProviderPhysicalNameContext(new StorageUnitIdentity("workItem"), PhysicalObjectKind.PhysicalIndex, name))));
        Assert.Equal(normalized.Length, normalized.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TimeSpan.TicksPerSecond - 1)]
    public void Physical_schema_executor_rejects_lease_durations_below_the_provider_minimum(long ticks)
    {
        var database = new MongoClient("mongodb://localhost").GetDatabase("groundwork_options");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MongoDbPhysicalSchemaExecutor(database, leaseDuration: TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void Physical_schema_lease_cadence_preserves_the_safe_minimum_and_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), MongoDbPhysicalSchemaExecutor.MinimumLeaseDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), MongoDbPhysicalSchemaExecutor.DefaultLeaseDuration);
        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.FromSeconds(1).Ticks / 3),
            MongoDbPhysicalSchemaExecutor.LeaseRenewalInterval(MongoDbPhysicalSchemaExecutor.MinimumLeaseDuration));
        Assert.Equal(
            TimeSpan.FromMinutes(5) / 3,
            MongoDbPhysicalSchemaExecutor.LeaseRenewalInterval(MongoDbPhysicalSchemaExecutor.DefaultLeaseDuration));
    }

    [Theory]
    [InlineData(nameof(MongoDbPhysicalDocumentStoreOptions.MaximumTransactionAttempts))]
    [InlineData(nameof(MongoDbPhysicalDocumentStoreOptions.TransactionRetryTimeout))]
    [InlineData(nameof(MongoDbPhysicalDocumentStoreOptions.MaximumCommitAttempts))]
    [InlineData(nameof(MongoDbPhysicalDocumentStoreOptions.CommitRetryTimeout))]
    public void Physical_document_store_rejects_invalid_retry_options(string option)
    {
        var database = new MongoClient("mongodb://localhost").GetDatabase("groundwork_options");
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var options = option switch
        {
            nameof(MongoDbPhysicalDocumentStoreOptions.MaximumTransactionAttempts) =>
                new MongoDbPhysicalDocumentStoreOptions { MaximumTransactionAttempts = 0 },
            nameof(MongoDbPhysicalDocumentStoreOptions.TransactionRetryTimeout) =>
                new MongoDbPhysicalDocumentStoreOptions { TransactionRetryTimeout = TimeSpan.FromTicks(-1) },
            nameof(MongoDbPhysicalDocumentStoreOptions.MaximumCommitAttempts) =>
                new MongoDbPhysicalDocumentStoreOptions { MaximumCommitAttempts = 0 },
            nameof(MongoDbPhysicalDocumentStoreOptions.CommitRetryTimeout) =>
                new MongoDbPhysicalDocumentStoreOptions { CommitRetryTimeout = TimeSpan.FromTicks(-1) },
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            options: options));
    }

    [Fact]
    public void Compilation_rejects_an_invalid_typed_projection_default()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            MongoDbPhysicalStorageConformanceTests.Model(
                PhysicalStorageForm.PhysicalEntityTable,
                projectedType: PortablePhysicalType.Int32,
                valueKind: IndexValueKind.Number,
                path: "rank",
                defaultValue: "not-an-integer"));

        Assert.Contains("invalid default", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortablePhysicalType.Int32, IndexValueKind.Number, "1e2", BsonType.Int32)]
    [InlineData(PortablePhysicalType.Int64, IndexValueKind.Number, "42000000000", BsonType.Int64)]
    [InlineData(PortablePhysicalType.Decimal, IndexValueKind.Number, "1.25e2", BsonType.Decimal128)]
    [InlineData(PortablePhysicalType.DateTime, IndexValueKind.DateTime, "2026-01-01T01:00:00+01:00", BsonType.Int64)]
    [InlineData(PortablePhysicalType.Guid, IndexValueKind.Keyword, "20b7b527-8799-45b2-8f43-aa742308da8c", BsonType.String)]
    [InlineData(PortablePhysicalType.Binary, IndexValueKind.Keyword, "AQID", BsonType.Binary)]
    [InlineData(PortablePhysicalType.Json, IndexValueKind.Keyword, "{\"nested\":[1,2]}", BsonType.Document)]
    public void Projected_query_values_bind_the_declared_native_physical_type(
        PortablePhysicalType type,
        IndexValueKind valueKind,
        string value,
        BsonType expectedType)
    {
        var model = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            projectedType: type,
            valueKind: valueKind,
            path: "value");
        var projection = Assert.Single(model.Routes).ProjectedColumns.Single(column =>
            column.Definition.Path == "value");

        var converted = MongoDbPhysicalProjectionValues.ParseQueryValue(projection, value);

        Assert.Equal(expectedType, converted.BsonType);
        if (type == PortablePhysicalType.Int32)
            Assert.Equal(100, converted.AsInt32);
        if (type == PortablePhysicalType.DateTime)
            Assert.Equal(
                DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcTicks,
                converted.AsInt64);
    }

    [Fact]
    public void Canonical_and_query_numeric_conversion_rejects_rounding_and_declared_shape_drift()
    {
        var model = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            projectedType: PortablePhysicalType.Decimal,
            valueKind: IndexValueKind.Number,
            path: "value");
        var projection = Assert.Single(model.Routes).ProjectedColumns.Single(column =>
            column.Definition.Path == "value");

        Assert.Throws<InvalidDataException>(() => MongoDbPhysicalProjectionValues.ResolveAll(
            """{"value":99999999999999.99990000000000001}""",
            [projection]));
        Assert.Throws<InvalidDataException>(() =>
            MongoDbPhysicalProjectionValues.ParseQueryValue(
                projection,
                "99999999999999.99990000000000001"));

        var canonical = MongoDbPhysicalProjectionValues.ResolveAll(
            """{"value":9.99999999999999999e13}""",
            [projection])[projection].Value;
        var query = MongoDbPhysicalProjectionValues.ParseQueryValue(
            projection,
            "99999999999999.9999");
        Assert.Equal(query, canonical);
    }

    [Fact]
    public void Canonical_projection_conversion_preserves_adjacent_utc_ticks()
    {
        var model = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            projectedType: PortablePhysicalType.DateTime,
            valueKind: IndexValueKind.DateTime,
            path: "occurredAt");
        var projection = Assert.Single(model.Routes).ProjectedColumns.Single(column =>
            column.Definition.Path == "occurredAt");

        var first = MongoDbPhysicalProjectionValues.ResolveAll(
            """{"occurredAt":"2026-01-01T00:00:00.0000000Z"}""",
            [projection])[projection].Value.AsInt64;
        var second = MongoDbPhysicalProjectionValues.ResolveAll(
            """{"occurredAt":"2025-12-31T19:00:00.0000001-05:00"}""",
            [projection])[projection].Value.AsInt64;

        Assert.Equal(first + 1, second);
    }

    [Fact]
    public void Projection_boundary_exposes_no_BSON_only_canonical_fallback()
    {
        var publicStatic = typeof(MongoDbPhysicalProjectionValues)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        var resolveAll = Assert.Single(publicStatic, method => method.Name == "ResolveAll");
        Assert.Equal(typeof(string), resolveAll.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(publicStatic, method => method.Name is "Resolve" or "Normalize");
    }

    [Theory]
    [InlineData(QueryComparisonOperator.Equal)]
    [InlineData(QueryComparisonOperator.In)]
    public async Task Native_boolean_query_rejects_an_invalid_lexical_value_before_database_traffic(
        QueryComparisonOperator comparisonOperator)
    {
        var model = NativeBooleanModel();
        var database = new MongoClient("mongodb://localhost/?serverSelectionTimeoutMS=10")
            .GetDatabase("groundwork_invalid_boolean");
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")));

        var comparison = comparisonOperator == QueryComparisonOperator.In
            ? DocumentQueryComparison.In("enabled", ["true", "not-a-boolean"])
            : DocumentQueryComparison.Equal("enabled", "not-a-boolean");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.QueryAsync(new DocumentQuery(
            "workItem", "list-by-enabled", [DocumentQueryClause.Of(comparison)])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_execute_and_explain_convert_typed_values_before_provider_traffic(
        bool explain)
    {
        var model = MutationModel(
            PortablePhysicalType.Boolean,
            IndexValueKind.Boolean,
            "enabled");
        var (store, traffic) = TrafficObservedStore(
            DocumentStoreAccess.Scoped(new("tenant-a")),
            model);
        var mutation = new DocumentMutation(
            "workItem",
            "prune-by-enabled",
            $"invalid-boolean-{explain}",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("enabled", "not-a-boolean"))]);
        var route = Assert.Single(model.Routes);

        if (explain)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => MongoDbPhysicalMutationRuntime.ExplainAsync(
                store,
                model.Manifest,
                route,
                model.Provider,
                mutation));
        }
        else
        {
            var mutations = MongoDbPhysicalMutationRuntime.Create(
                store,
                model.Manifest,
                route,
                model.Provider);
            await Assert.ThrowsAsync<InvalidDataException>(() => mutations.ExecuteAsync(mutation));
        }

        traffic.AssertNone();
    }

    [Theory]
    [InlineData("missing-required-clause", false, "requires exactly one clause")]
    [InlineData("missing-required-clause", true, "requires exactly one clause")]
    [InlineData("wrong-document-kind", false, "not bound to storage unit")]
    [InlineData("wrong-document-kind", true, "not bound to storage unit")]
    [InlineData("illegal-operator", false, "not bound to mutation")]
    [InlineData("illegal-operator", true, "not bound to mutation")]
    [InlineData("caller-transition-path", false, "is not caller supplied")]
    [InlineData("caller-transition-path", true, "is not caller supplied")]
    public async Task Mutation_execute_and_explain_admit_the_same_closed_shape_before_provider_traffic(
        string shape,
        bool explain,
        string expectedMessage)
    {
        var model = shape == "caller-transition-path"
            ? MongoDbBoundedMutationTests.Model(PhysicalStorageForm.PhysicalEntityTable)
            : MutationModel(PortablePhysicalType.String, IndexValueKind.Keyword, "status");
        var (store, traffic) = TrafficObservedStore(
            DocumentStoreAccess.Scoped(new("tenant-a")),
            model);
        var mutation = shape switch
        {
            "missing-required-clause" => new DocumentMutation(
                "workItem",
                "prune-by-status",
                $"{shape}-{explain}"),
            "wrong-document-kind" => new DocumentMutation(
                "other",
                "prune-by-status",
                $"{shape}-{explain}",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "stale"))]),
            "illegal-operator" => new DocumentMutation(
                "workItem",
                "prune-by-status",
                $"{shape}-{explain}",
                [DocumentQueryClause.Of(DocumentQueryComparison.GreaterThan("status", "stale"))]),
            "caller-transition-path" => new DocumentMutation(
                "workItem",
                "revoke-pending",
                $"{shape}-{explain}",
                [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "pending"))]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
        var route = Assert.Single(model.Routes);

        async Task InvokeAsync()
        {
            if (explain)
            {
                await MongoDbPhysicalMutationRuntime.ExplainAsync(
                    store,
                    model.Manifest,
                    route,
                    model.Provider,
                    mutation);
                return;
            }

            await MongoDbPhysicalMutationRuntime.Create(
                    store,
                    model.Manifest,
                    route,
                    model.Provider)
                .ExecuteAsync(mutation);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(InvokeAsync);

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        traffic.AssertNone();
    }

    [Fact]
    public async Task Unit_of_work_disposes_an_owned_session_when_transaction_startup_fails()
    {
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var database = new MongoClient("mongodb://localhost").GetDatabase("groundwork_session_disposal");
        var session = DispatchProxy.Create<IClientSessionHandle, FailingSessionProxy>();
        var proxy = (FailingSessionProxy)(object)session;
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            options: null,
            TimeProvider.System,
            hooks: null,
            _ => Task.FromResult(session),
            new MongoDbTransactionCapability(_ => Task.FromResult(true), knownSupport: true));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.BeginAsync(DocumentCommitScope.Of("workItem")));

        Assert.Equal("transaction startup failed", exception.Message);
        Assert.True(proxy.Disposed);
    }

    [Fact]
    public async Task Conventional_unit_of_work_disposes_an_owned_session_when_transaction_startup_fails()
    {
        var database = new MongoClient("mongodb://localhost").GetDatabase("groundwork_conventional_session_disposal");
        var session = DispatchProxy.Create<IClientSessionHandle, FailingSessionProxy>();
        var proxy = (FailingSessionProxy)(object)session;
        var store = new MongoDbDocumentStore(
            database,
            Manifest(),
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            _ => Task.FromResult(true),
            _ => Task.FromResult(session));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.BeginAsync(DocumentCommitScope.Of("orders")));

        Assert.Equal("transaction startup failed", exception.Message);
        Assert.True(proxy.Disposed);
    }

    [Fact]
    public async Task Conventional_unit_of_work_rejects_out_of_scope_kinds_without_becoming_terminal()
    {
        var database = new MongoClient("mongodb://localhost").GetDatabase("groundwork_conventional_commit_scope");
        var session = DispatchProxy.Create<IClientSessionHandle, ActiveSessionProxy>();
        var proxy = (ActiveSessionProxy)(object)session;
        var store = new MongoDbDocumentStore(
            database,
            Manifest(),
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            _ => Task.FromResult(true),
            _ => Task.FromResult(session));
        await using var transaction = await store.BeginAsync(DocumentCommitScope.Of("orders"));

        await Assert.ThrowsAsync<ArgumentException>(() => transaction.SaveAsync(new SaveDocumentRequest(
            "undeclared-kind", "outside-save", "1", "{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => transaction.DeleteAsync(new DeleteDocumentRequest(
            "undeclared-kind", "outside-delete")));
        await Assert.ThrowsAsync<ArgumentException>(() => transaction.LoadAsync(
            "undeclared-kind", "outside-load"));

        await transaction.RollbackAsync();
        Assert.True(proxy.Aborted);
        Assert.True(proxy.Disposed);
    }

    [Fact]
    public void Transaction_topology_probe_distinguishes_standalone_replica_set_and_sharded_hello_evidence()
    {
        Assert.False(MongoDbTransactionTopology.IsHelloTransactionCapable(new BsonDocument("ok", 1)));
        Assert.True(MongoDbTransactionTopology.IsHelloTransactionCapable(new BsonDocument("setName", "rs0")));
        Assert.True(MongoDbTransactionTopology.IsHelloTransactionCapable(new BsonDocument("msg", "isdbgrid")));
    }

    [Fact]
    public async Task Transaction_capability_probe_is_cached_only_after_a_conclusive_result()
    {
        var probes = 0;
        var capability = new MongoDbTransactionCapability(_ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(true);
        });

        Assert.False(capability.IsKnownSupported);
        Assert.True(await capability.SupportsTransactionsAsync(CancellationToken.None));
        Assert.True(await capability.SupportsTransactionsAsync(CancellationToken.None));

        Assert.True(capability.IsKnownSupported);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Direct_physical_store_rejects_every_transactional_entry_before_database_traffic()
    {
        var probes = 0;
        var sessions = 0;
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var database = new MongoClient("mongodb://localhost/?serverSelectionTimeoutMS=10")
            .GetDatabase("groundwork_direct_topology_gate");
        var capability = new MongoDbTransactionCapability(_ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(false);
        });
        var store = new MongoDbPhysicalDocumentStore(
            database,
            model,
            DocumentStoreAccess.Scoped(new("tenant-a")),
            scopeObserver: null,
            options: null,
            TimeProvider.System,
            hooks: null,
            _ =>
            {
                Interlocked.Increment(ref sessions);
                throw new InvalidOperationException("Session creation must not be reached.");
            },
            capability);

        Assert.Equal(TransactionBoundary.PerOperation, store.TransactionBoundary);
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.SaveAsync(new SaveDocumentRequest(
            "workItem", "save", "1", "{}")));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.DeleteAsync(new DeleteDocumentRequest(
            "workItem", "delete")));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.LoadAsync(
            "workItem", "load"));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.BeginAsync(DocumentCommitScope.Of("workItem")));
        var query = new DocumentQuery(
            "workItem",
            "list-by-status",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "open"))]);
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.QueryAsync(query));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.CountAsync(
            query.Select(BoundedQueryResultOperation.Count)));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.AnyAsync(
            query.Select(BoundedQueryResultOperation.Any)));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.FirstOrDefaultAsync(
            query.Select(BoundedQueryResultOperation.First)));
        await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() => store.ExplainAsync(query));

        Assert.Equal(TransactionBoundary.PerOperation, store.TransactionBoundary);
        Assert.Equal(1, probes);
        Assert.Equal(0, sessions);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("begin")]
    public async Task Direct_physical_store_rejects_invalid_access_before_provider_traffic(string operation)
    {
        var (store, traffic) = TrafficObservedStore(DocumentStoreAccess.Global);

        await Assert.ThrowsAsync<InvalidStorageScopeAccessException>(() =>
            InvokePhysicalOperationAsync(store, operation, "workItem"));

        traffic.AssertNone();
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("begin")]
    public async Task Direct_physical_store_rejects_unknown_kinds_before_provider_traffic(string operation)
    {
        var (store, traffic) = TrafficObservedStore(DocumentStoreAccess.Scoped(new("tenant-a")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokePhysicalOperationAsync(store, operation, "unknown"));

        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
        traffic.AssertNone();
    }

    [Fact]
    public async Task Physical_factory_validates_the_compiled_model_before_probing_an_unreachable_server()
    {
        var manifest = Manifest();
        var invalid = manifest with
        {
            StorageUnits = manifest.StorageUnits.Select(unit => unit.Identity.Value == "orders"
                ? unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        StorageUnitProvisioningMode.Declared,
                        PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.DedicatedDocumentTable(
                            "groundwork_physical_schema_state")))
                }
                : unit).ToArray()
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MongoDbDocumentStoreFactory.CreatePhysicalAsync(
                "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=10",
                "groundwork_invalid_model_before_topology",
                invalid,
                MongoDbGroundworkCapabilities.Provider,
                DocumentStoreAccess.Scoped(new("tenant-a"))));

        Assert.Contains("reserved collection", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("groundwork_physical_schema_state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_physical_materializer_rejects_unsupported_topology_before_schema_application()
    {
        var probes = 0;
        var model = MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable);
        var database = new MongoClient("mongodb://localhost").GetDatabase("groundwork_direct_materializer_topology");
        var capability = new MongoDbTransactionCapability(_ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(false);
        });

        var exception = await Assert.ThrowsAsync<UnsupportedAtomicCommitException>(() =>
            new MongoDbGroundworkMaterializer(database).MaterializeAsync(model, capability));

        Assert.Equal(["workItem"], exception.Units);
        Assert.Equal(1, probes);
    }

    private static StorageManifest Manifest()
    {
        var shared = new SharedStorageBinding("runtime");
        return new StorageManifest(
            new StorageManifestIdentity("mongo.forms"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [
                Unit("orders", PhysicalTableDefinition.DedicatedDocumentTable("orders")),
                Unit("tickets", PhysicalTableDefinition.PhysicalEntityTable(
                    "tickets",
                    [new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)])),
                Unit("profiles", PhysicalTableDefinition.SharedDocuments(
                    shared,
                    [new ProjectedColumnDefinition("email", "email", PortablePhysicalType.String)],
                    linkedProjectionLogicalName: "profiles_lookup"))
            ],
            new HashSet<string>(),
            [])
        {
            SharedDocumentStorages =
            [new SharedDocumentStorageDefinition(shared, "documents", new DocumentEnvelopeDefinition())]
        };
    }

    private static StorageUnit Unit(string kind, PhysicalTableDefinition definition) =>
        new(
            new StorageUnitIdentity(kind),
            kind,
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition))
        };

    private static async Task InvokePhysicalOperationAsync(
        MongoDbPhysicalDocumentStore store,
        string operation,
        string documentKind)
    {
        switch (operation)
        {
            case "save":
                await store.SaveAsync(new SaveDocumentRequest(documentKind, "id", "1", "{}"));
                break;
            case "delete":
                await store.DeleteAsync(new DeleteDocumentRequest(documentKind, "id"));
                break;
            case "begin":
                await using (await store.BeginAsync(DocumentCommitScope.Of(documentKind)))
                {
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static (MongoDbPhysicalDocumentStore Store, ProviderTrafficProbe Traffic) TrafficObservedStore(
        DocumentStoreAccess access,
        MongoDbPhysicalStorageModel? model = null)
    {
        var traffic = new ProviderTrafficProbe();
        var settings = MongoClientSettings.FromConnectionString(
            "mongodb://localhost/?serverSelectionTimeoutMS=10");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(_ => traffic.RecordCommand());
        var capability = new MongoDbTransactionCapability(_ =>
        {
            traffic.RecordProbe();
            return Task.FromResult(false);
        });
        var store = new MongoDbPhysicalDocumentStore(
            new MongoClient(settings).GetDatabase("groundwork_local_validation_before_provider_traffic"),
            model ?? MongoDbPhysicalStorageConformanceTests.Model(PhysicalStorageForm.PhysicalEntityTable),
            access,
            scopeObserver: null,
            options: null,
            TimeProvider.System,
            hooks: null,
            _ =>
            {
                traffic.RecordSession();
                throw new InvalidOperationException("Session creation must not be reached.");
            },
            capability);
        return (store, traffic);
    }

    private static MongoDbPhysicalStorageModel MutationModel(
        PortablePhysicalType projectedType,
        IndexValueKind valueKind,
        string path,
        IReadOnlySet<PortableQueryOperation>? operations = null)
    {
        var template = MongoDbPhysicalStorageConformanceTests.Model(
            PhysicalStorageForm.PhysicalEntityTable,
            operations,
            projectedType: projectedType,
            valueKind: valueKind,
            path: path);
        var unit = Assert.Single(template.Manifest.StorageUnits);
        var storage = unit.PhysicalStorage!;
        return MongoDbPhysicalStorageModel.Compile(template.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        storage.ProvisioningMode,
                        storage.Policy,
                        storage.LogicalIndexes,
                        storage.BoundedQueries,
                        storage.NameOverrides,
                        [new BoundedMutationDeclaration(
                            $"prune-by-{path}",
                            $"list-by-{path}",
                            BoundedMutationAction.Delete())])
                }
            ]
        });
    }

    private static MongoDbPhysicalStorageModel NativeBooleanModel()
    {
        var logical = new LogicalIndexDeclaration(
            "by-enabled",
            [new IndexField("enabled")],
            IndexValueKind.Boolean,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "list-by-enabled",
            logical.Identity,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.In },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.Ordinary);
        var unit = Unit("workItem", PhysicalTableDefinition.DedicatedDocumentTable("work_items")) with
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.DedicatedDocumentTable("work_items")),
                [logical],
                [query])
        };
        return MongoDbPhysicalStorageModel.Compile(new StorageManifest(
            new StorageManifestIdentity("mongo.native-boolean"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []));
    }

    private static MongoDbPhysicalStorageModel ResidualHistoryModel()
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "history-order",
            [new IndexField("createdAt", IndexValueKind.DateTime)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "history-by-created-at",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Equal,
                PortableQueryOperation.In
            },
            QuerySortSupport.Descending,
            QueryPagingSupport.Cursor,
            BoundedQueryExecutionClass.ScaleBearing,
            sortFields:
            [
                new BoundedQuerySortField("createdAt", PhysicalSortDirection.Descending)
            ],
            residualPredicateFields:
            [
                new BoundedQueryResidualPredicateField(
                    "status",
                    IndexValueKind.Keyword,
                    new HashSet<PortableQueryOperation>
                    {
                        PortableQueryOperation.Equal,
                        PortableQueryOperation.In
                    }),
                new BoundedQueryResidualPredicateField(
                    PhysicalDocumentFieldPaths.Version,
                    IndexValueKind.Number,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal })
            ]);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "workflow_execution_history",
            [
                new ProjectedColumnDefinition("created_at", "createdAt", PortablePhysicalType.DateTime),
                new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)
            ],
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition(
                            "created_at",
                            0,
                            PhysicalSortDirection.Descending),
                        new PhysicalIndexColumnDefinition("id_lookup_key", 1)
                    ])
            ]);
        var unit = Unit("configurationDocument", definition) with
        {
            Tenancy = TenancyPolicy.Global,
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query])
        };
        return MongoDbPhysicalStorageModel.Compile(new StorageManifest(
            new StorageManifestIdentity("mongo.residual-history"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []));
    }

    private static MongoDbPhysicalStorageModel IdentityModel(
        PortableQueryOperation operation = PortableQueryOperation.Equal,
        bool includeMutation = false)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "find-by-id",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation> { operation },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            includeMutation ? BoundedQueryExecutionClass.ScaleBearing : BoundedQueryExecutionClass.Ordinary);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "identity_entities",
            [new ProjectedColumnDefinition("unused", "unused", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        ..(operation == PortableQueryOperation.Equal
                            ? new[]
                            {
                                new PhysicalIndexColumnDefinition("id_lookup_key", 0),
                                new PhysicalIndexColumnDefinition("id_comparison_key", 1)
                            }
                            : [new PhysicalIndexColumnDefinition("id_comparison_key", 0)])
                    ])
            ]);
        var unit = Unit("configurationDocument", definition) with
        {
            IdentityPolicy = IdentityPolicy.StringId(
                stringCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase),
            Tenancy = TenancyPolicy.Global,
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query],
                boundedMutations: includeMutation
                    ? [new BoundedMutationDeclaration("delete-by-id", query.Identity, BoundedMutationAction.Delete())]
                    : [])
        };
        return MongoDbPhysicalStorageModel.Compile(new StorageManifest(
            new StorageManifestIdentity("mongo.identity-query"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []));
    }

    private static MongoDbPhysicalStorageModel CompoundIdentityContainsModel(bool includeMutation)
    {
        var logicalIndex = new LogicalIndexDeclaration(
            "by-status-id",
            [new IndexField("status"), new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var query = new BoundedQueryDeclaration(
            "find-by-status-id",
            logicalIndex.Identity,
            new HashSet<PortableQueryOperation>
            {
                PortableQueryOperation.Contains,
                PortableQueryOperation.StartsWith
            },
            QuerySortSupport.None,
            QueryPagingSupport.None,
            BoundedQueryExecutionClass.ScaleBearing,
            predicateFields:
            [
                new BoundedQueryPredicateField(
                    "status",
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.StartsWith }),
                new BoundedQueryPredicateField(
                    PhysicalDocumentFieldPaths.Id,
                    new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains })
            ]);
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "compound_identity_entities",
            [new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)],
            indexes:
            [
                new PhysicalIndexDefinition(
                    logicalIndex.Identity,
                    [
                        new PhysicalIndexColumnDefinition("status", 0),
                        new PhysicalIndexColumnDefinition("id_comparison_key", 1)
                    ])
            ]);
        var unit = Unit("configurationDocument", definition) with
        {
            IdentityPolicy = IdentityPolicy.StringId(
                stringCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase),
            Tenancy = TenancyPolicy.Global,
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition),
                [logicalIndex],
                [query],
                boundedMutations: includeMutation
                    ? [new BoundedMutationDeclaration("delete-by-status-id", query.Identity, BoundedMutationAction.Delete())]
                    : [])
        };
        return MongoDbPhysicalStorageModel.Compile(new StorageManifest(
            new StorageManifestIdentity("mongo.compound-identity-diagnostic"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []));
    }

    private static DocumentQuery IdentityQuery(
        string id,
        PortableQueryOperation operation = PortableQueryOperation.Equal) => new(
        "configurationDocument",
        "find-by-id",
        [DocumentQueryClause.Of(new DocumentQueryComparison(
            PhysicalDocumentFieldPaths.Id,
            operation switch
            {
                PortableQueryOperation.Equal => QueryComparisonOperator.Equal,
                PortableQueryOperation.GreaterThan => QueryComparisonOperator.GreaterThan,
                PortableQueryOperation.StartsWith => QueryComparisonOperator.StartsWith,
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            },
            [id]))]);

    private static BsonDocument Render(FilterDefinition<BsonDocument> filter) => filter.Render(
        new RenderArgs<BsonDocument>(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry));

    private class FailingSessionProxy : DispatchProxy
    {
        public bool Disposed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDisposable.Dispose))
            {
                Disposed = true;
                return null;
            }
            if (targetMethod?.Name == nameof(IClientSessionHandle.StartTransaction))
                throw new InvalidOperationException("transaction startup failed");
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class ActiveSessionProxy : DispatchProxy
    {
        public bool Aborted { get; private set; }
        public bool Disposed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IClientSessionHandle.StartTransaction))
                return null;
            if (targetMethod?.Name == $"get_{nameof(IClientSessionHandle.IsInTransaction)}")
                return true;
            if (targetMethod?.Name == nameof(IClientSessionHandle.AbortTransactionAsync))
            {
                Aborted = true;
                return Task.CompletedTask;
            }
            if (targetMethod?.Name == nameof(IDisposable.Dispose))
            {
                Disposed = true;
                return null;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class ProviderTrafficProbe
    {
        private int probes;
        private int sessions;
        private int commands;

        public void RecordProbe() => Interlocked.Increment(ref probes);
        public void RecordSession() => Interlocked.Increment(ref sessions);
        public void RecordCommand() => Interlocked.Increment(ref commands);

        public void AssertNone()
        {
            Assert.Equal(0, probes);
            Assert.Equal(0, sessions);
            Assert.Equal(0, commands);
        }
    }
}
