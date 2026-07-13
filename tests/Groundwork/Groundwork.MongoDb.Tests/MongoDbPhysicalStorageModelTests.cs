using System.Text;
using System.Reflection;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.MongoDb.Documents;
using Groundwork.MongoDb.Materialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoDbPhysicalStorageModelTests
{
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
            _ => Task.FromResult(session));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.BeginAsync(DocumentCommitScope.Of("workItem")));

        Assert.Equal("transaction startup failed", exception.Message);
        Assert.True(proxy.Disposed);
    }

    [Fact]
    public void Transaction_topology_probe_distinguishes_standalone_replica_set_and_sharded_hello_evidence()
    {
        Assert.False(MongoDbTransactionTopology.IsHelloTransactionCapable(new BsonDocument("ok", 1)));
        Assert.True(MongoDbTransactionTopology.IsHelloTransactionCapable(new BsonDocument("setName", "rs0")));
        Assert.True(MongoDbTransactionTopology.IsHelloTransactionCapable(new BsonDocument("msg", "isdbgrid")));
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
}
