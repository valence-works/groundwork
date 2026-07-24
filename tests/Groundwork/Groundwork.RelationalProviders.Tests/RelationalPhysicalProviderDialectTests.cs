using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.PostgreSql.Documents;
using Groundwork.Provider.Relational;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.Relational.PhysicalStorage;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.SqlServer;
using Groundwork.PostgreSql;
using Groundwork.TestInfrastructure;
using System.Text;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class RelationalPhysicalProviderDialectTests
{
    [Fact]
    public void Provider_query_capabilities_are_derived_from_executable_relational_handlers()
    {
        var provider = new ProviderIdentity("provider", "1");

        var capabilities = RelationalPhysicalQueryRuntime.Capabilities(provider, "provider");

        Assert.Equal(
            new[]
            {
                PhysicalQuerySourceKind.LinkedIndex,
                PhysicalQuerySourceKind.PrimaryEnvelope,
                PhysicalQuerySourceKind.PrimaryCanonicalJson,
                PhysicalQuerySourceKind.PrimaryProjectedColumns
            },
            capabilities.HandlerIdentities.Keys.Order());
        Assert.All(capabilities.HandlerIdentities, registration =>
            Assert.Equal($"provider:{registration.Key}", registration.Value));
        Assert.DoesNotContain(
            IndexValueKind.Number,
            capabilities.SourceValueKinds[PhysicalQuerySourceKind.PrimaryCanonicalJson]);
        Assert.DoesNotContain(
            IndexValueKind.DateTime,
            capabilities.SourceValueKinds[PhysicalQuerySourceKind.PrimaryCanonicalJson]);
    }

    [Fact]
    public void Sql_server_reports_the_logical_index_kinds_served_by_its_bounded_key_validator()
    {
        var valueKinds = SqlServerGroundworkCapabilities.Runtime().Indexes.SupportedValueKinds;

        Assert.Equal(
            new[]
            {
                IndexValueKind.String,
                IndexValueKind.Number,
                IndexValueKind.Boolean,
                IndexValueKind.DateTime,
                IndexValueKind.Keyword
            },
            valueKinds.Order());
    }

    [Fact]
    public void Sql_server_uses_binary_identity_and_projection_collation()
    {
        var dialect = new SqlServerPhysicalSchemaDialect();
        var projected = new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String);

        Assert.Equal("nvarchar(450)", dialect.EnvelopeType(RelationalEnvelopeColumnKind.DocumentKind));
        Assert.Equal("nvarchar(128)", dialect.EnvelopeType(RelationalEnvelopeColumnKind.StorageScope));
        Assert.Equal("nvarchar(450)", dialect.EnvelopeType(RelationalEnvelopeColumnKind.Id));
        Assert.Equal("Latin1_General_100_BIN2", dialect.EnvelopeCollation(RelationalEnvelopeColumnKind.Id));
        Assert.Equal("Latin1_General_100_BIN2", dialect.ProjectedCollation(projected));
        Assert.Contains("COLLATE Latin1_General_100_BIN2", dialect.EnvelopeColumn("id", RelationalEnvelopeColumnKind.Id));
        Assert.Contains("PRIMARY KEY NONCLUSTERED", dialect.CreateTableSql("records", ["[id] nvarchar(450) NOT NULL"], ["id"]));
    }

    [Fact]
    public void Sql_server_uses_lossless_bounded_binary_document_identity_evidence()
    {
        var dialect = new SqlServerPhysicalSchemaDialect();

        Assert.Equal("varbinary(1350)", dialect.EnvelopeType(RelationalEnvelopeColumnKind.IdentityComparison));
        Assert.Equal("binary(32)", dialect.EnvelopeType(RelationalEnvelopeColumnKind.IdentityLookup));
        Assert.Null(dialect.EnvelopeCollation(RelationalEnvelopeColumnKind.IdentityComparison));
        Assert.Null(dialect.EnvelopeCollation(RelationalEnvelopeColumnKind.IdentityLookup));
        Assert.Contains(
            "[id_comparison_key] varbinary(1350) NOT NULL",
            dialect.EnvelopeColumn("id_comparison_key", RelationalEnvelopeColumnKind.IdentityComparison),
            StringComparison.Ordinal);
        Assert.Contains(
            "[id_lookup_key] binary(32) NOT NULL",
            dialect.EnvelopeColumn("id_lookup_key", RelationalEnvelopeColumnKind.IdentityLookup),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void Collection_element_ddl_preserves_the_compiled_owner_key_and_value_contract(string providerName)
    {
        var (provider, normalizer, dialect) = providerName switch
        {
            "postgresql" => (
                PostgreSqlGroundworkCapabilities.Provider,
                PostgreSqlGroundworkCapabilities.PhysicalNames,
                (RelationalServerPhysicalSchemaDialect)new PostgreSqlPhysicalSchemaDialect()),
            "sqlserver" => (
                SqlServerGroundworkCapabilities.Provider,
                SqlServerGroundworkCapabilities.PhysicalNames,
                (RelationalServerPhysicalSchemaDialect)new SqlServerPhysicalSchemaDialect()),
            _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, null)
        };
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            provider,
            includePriority: false,
            instance: $"collection_{providerName}",
            normalizer: normalizer,
            includeCollection: true);
        var storage = Assert.Single(Assert.Single(model.Target.Routes).CollectionElementStorages);

        var sql = dialect.CreateCollectionElementTableSql(storage);
        var expectedValue = storage.Value.Definition with { IsNullable = false };
        var primaryKey = string.Join(", ", storage.OwnerOrdinalKey.Columns.Select(column => dialect.Q(column.Column.Identifier)));

        Assert.Contains(dialect.ProjectedColumnSql(storage.Value.Column.Identifier, expectedValue), sql, StringComparison.Ordinal);
        Assert.Contains(
            providerName == "sqlserver"
                ? $"PRIMARY KEY NONCLUSTERED ({primaryKey})"
                : $"PRIMARY KEY ({primaryKey})",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(dialect.EnvelopeColumn(storage.DocumentKind.Column.Identifier, RelationalEnvelopeColumnKind.DocumentKind), sql, StringComparison.Ordinal);
        Assert.Contains(dialect.EnvelopeColumn(storage.StorageScope.Column.Identifier, RelationalEnvelopeColumnKind.StorageScope), sql, StringComparison.Ordinal);
        Assert.Contains(dialect.EnvelopeColumn(storage.IdComparisonKey.Column.Identifier, RelationalEnvelopeColumnKind.IdentityComparison), sql, StringComparison.Ordinal);
        Assert.Contains(dialect.EnvelopeColumn(storage.IdLookupKey.Column.Identifier, RelationalEnvelopeColumnKind.IdentityLookup), sql, StringComparison.Ordinal);
        Assert.Contains(dialect.ProjectedColumnSql(storage.Ordinal.Column.Identifier,
            new ProjectedColumnDefinition("ordinal", "ordinal", PortablePhysicalType.Int32, IsNullable: false)), sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(" DEFAULT ", sql, StringComparison.OrdinalIgnoreCase);

        if (providerName == "sqlserver")
        {
            var keyClause = sql[sql.IndexOf("PRIMARY KEY", StringComparison.Ordinal)..];
            Assert.DoesNotContain("max", keyClause, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PRIMARY KEY NONCLUSTERED", keyClause, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(DocumentIdentityAcceptanceSurface.Exact)]
    [InlineData(DocumentIdentityAcceptanceSurface.OrderedRange)]
    public void Sql_server_accepts_lossless_identity_indexes_within_its_native_key_limit(
        DocumentIdentityAcceptanceSurface surface)
    {
        var route = SqlServerIdentityAcceptanceRoute(surface).Route;

        new SqlServerPhysicalSchemaDialect().ValidateRoute(route);
    }

    [Fact]
    public async Task Sql_server_rejects_overlong_document_identity_before_opening_a_session()
    {
        var (manifest, route) = SqlServerIdentityAcceptanceRoute(DocumentIdentityAcceptanceSurface.Exact);
        var opened = false;
        var sessions = RelationalSessionFactory.Concurrent(() =>
        {
            opened = true;
            return new Microsoft.Data.SqlClient.SqlConnection();
        });
        var documents = new SqlServerPhysicalDocumentStore(
            sessions,
            manifest,
            [route],
            DocumentStoreAccess.Scoped(new("tenant-a")));

        await Assert.ThrowsAsync<ArgumentException>(() => documents.LoadAsync(
            DocumentIdentityAcceptanceModel.DocumentKind,
            new string('x', 451)));
        var queries = SqlServerPhysicalQueryRuntime.Create(
            documents,
            manifest,
            route,
            SqlServerGroundworkCapabilities.Provider);
        await Assert.ThrowsAsync<ArgumentException>(() => queries.QueryAsync(
            DocumentIdentityAcceptanceModel.ExactQuery(DocumentQueryComparison.Equal(
                PhysicalDocumentFieldPaths.Id,
                new string('x', 451)))));

        Assert.False(opened);

        var (mutationManifest, mutationRoute) = SqlServerIdentityAcceptanceRoute(
            DocumentIdentityAcceptanceSurface.Mutation);
        var mutationOpened = false;
        var mutationSessions = RelationalSessionFactory.Concurrent(() =>
        {
            mutationOpened = true;
            return new Microsoft.Data.SqlClient.SqlConnection();
        });
        var mutationDocuments = new SqlServerPhysicalDocumentStore(
            mutationSessions,
            mutationManifest,
            [mutationRoute],
            DocumentStoreAccess.Scoped(new("tenant-a")));
        var mutations = SqlServerPhysicalMutationRuntime.Create(
            mutationDocuments,
            mutationManifest,
            mutationRoute,
            SqlServerGroundworkCapabilities.Provider);

        await Assert.ThrowsAsync<ArgumentException>(() => mutations.ExecuteAsync(
            DocumentIdentityAcceptanceModel.Delete("overlong", new string('x', 451))));

        Assert.False(mutationOpened);
    }

    [Fact]
    public void Sql_server_document_identity_encoding_preserves_portable_order_and_native_bounds()
    {
        const string lower = "000041";
        const string upper = "000042";
        var longest = string.Concat(Enumerable.Repeat("10FFFF", 450));

        var lowerBytes = SqlServerDocumentIdentityEncoding.Comparison(lower);
        var upperBytes = SqlServerDocumentIdentityEncoding.Comparison(upper);
        var longestBytes = SqlServerDocumentIdentityEncoding.Comparison(longest);
        var lookup = SqlServerDocumentIdentityEncoding.Lookup(new string('a', 64));
        var prefixUpper = SqlServerDocumentIdentityEncoding.ComparisonPrefixUpperBound("00FF");
        var dialect = new SqlServerPhysicalDocumentDialect();
        var selectionSql = dialect.CreateMutationSelectionTable(
            "#selection",
            "kind",
            "scope",
            "id",
            "id_comparison",
            "id_lookup",
            "version",
            "incarnation");

        Assert.True(lowerBytes.AsSpan().SequenceCompareTo(upperBytes) < 0);
        Assert.Equal(1350, longestBytes.Length);
        Assert.Equal(32, lookup.Length);
        Assert.Equal(new byte[] { 1 }, prefixUpper);
        Assert.Null(SqlServerDocumentIdentityEncoding.ComparisonPrefixUpperBound(string.Empty));
        Assert.Contains("[id_comparison] varbinary(1350) NOT NULL", selectionSql, StringComparison.Ordinal);
        Assert.Contains("[id_lookup] binary(32) NOT NULL", selectionSql, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SqlServerDocumentIdentityEncoding.Original(new string('x', 451)));
        Assert.Equal(450, SqlServerDocumentIdentityEncoding.Original(new string('x', 450)).Length);
    }

    [Fact]
    public void Sql_server_primary_key_constraint_names_do_not_collide_for_long_table_names()
    {
        var dialect = new SqlServerPhysicalSchemaDialect();
        var commonPrefix = new string('x', 127);

        var first = ReadConstraintName(dialect.CreateTableSql(
            commonPrefix + "a", ["[id] int NOT NULL"], ["id"]));
        var second = ReadConstraintName(dialect.CreateTableSql(
            commonPrefix + "b", ["[id] int NOT NULL"], ["id"]));

        Assert.True(first.Length <= 128);
        Assert.True(second.Length <= 128);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Sql_server_maps_logical_identity_to_persisted_sha256_columns()
    {
        var dialect = new SqlServerPhysicalSchemaDialect();

        var layout = dialect.IdentityLayout(
        [
            new RelationalPhysicalIdentityColumn("document_kind", RelationalEnvelopeColumnKind.DocumentKind),
            new RelationalPhysicalIdentityColumn("storage_scope", RelationalEnvelopeColumnKind.StorageScope),
            new RelationalPhysicalIdentityColumn("id", RelationalEnvelopeColumnKind.Id)
        ], ["document_kind", "storage_scope", "id"]);

        Assert.Equal(["document_kind_key", "storage_scope_key", "id_key"], layout.PrimaryKey);
        Assert.Equal(["document_kind_key", "storage_scope_key", "id_key"], layout.ProviderColumns.Select(column => column.Name));
        Assert.All(layout.ProviderColumns, column =>
        {
            Assert.Equal("binary(32)", column.Type);
            Assert.False(column.IsNullable);
            Assert.Contains("HASHBYTES('SHA2_256'", column.Definition, StringComparison.Ordinal);
            Assert.Contains("PERSISTED NOT NULL", column.Definition, StringComparison.Ordinal);
        });

        var longName = new string('x', 128);
        var otherLongName = longName[..^1] + "y";
        var longLayout = dialect.IdentityLayout(
        [
            new RelationalPhysicalIdentityColumn(longName, RelationalEnvelopeColumnKind.Id),
            new RelationalPhysicalIdentityColumn(otherLongName, RelationalEnvelopeColumnKind.Id)
        ], [longName, otherLongName]);
        Assert.All(longLayout.ProviderColumns, column => Assert.True(column.Name.Length <= 128));
        Assert.NotEqual(longLayout.ProviderColumns[0].Name, longLayout.ProviderColumns[1].Name);
    }

    [Fact]
    public void Sql_server_rejects_visible_columns_that_collide_with_hidden_identity_keys()
    {
        const string instance = "hidden_collision";
        var normalizer = new DelegateProviderPhysicalNameNormalizer(
            context => context.ObjectKind == PhysicalObjectKind.ProjectedField &&
                       context.LogicalName.EndsWith("_category", StringComparison.Ordinal)
                ? $"gw_{instance}_id_key"
                : SqlServerGroundworkCapabilities.PhysicalNames.Normalize(context),
            context => SqlServerGroundworkCapabilities.PhysicalNames.GetCollisionScope(context));
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);

        var exception = Assert.Throws<InvalidOperationException>(() => new SqlServerPhysicalDocumentStore(
            "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global));

        Assert.Contains("gw_hidden_collision_id_key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider-owned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sql_server_rejects_case_only_visible_columns_that_collide_with_hidden_identity_keys(
        bool linked)
    {
        var instance = linked ? "linked_case_visible_collision" : "primary_case_visible_collision";
        var original = linked
            ? $"gw_{instance}_document_id"
            : $"gw_{instance}_id";
        var hidden = $"{original}_key";
        var projectedKind = linked
            ? PhysicalObjectKind.LinkedProjectedField
            : PhysicalObjectKind.ProjectedField;
        var normalizer = new DelegateProviderPhysicalNameNormalizer(
            context => context.ObjectKind == projectedKind &&
                       context.LogicalName.EndsWith("_category", StringComparison.Ordinal)
                ? hidden.ToUpperInvariant()
                : SqlServerGroundworkCapabilities.PhysicalNames.Normalize(context),
            context => SqlServerGroundworkCapabilities.PhysicalNames.GetCollisionScope(context));
        var model = RelationalPhysicalStorageTestModels.Create(
            linked ? PhysicalStorageForm.SharedDocuments : PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);

        var exception = Assert.Throws<InvalidOperationException>(() => new SqlServerPhysicalDocumentStore(
            "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global));

        Assert.Contains(hidden, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider-owned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sql_server_rejects_retained_identity_columns_that_collide_with_hidden_identity_keys(
        bool linked)
    {
        var instance = linked ? "linked_identity_collision" : "primary_identity_collision";
        var original = linked
            ? $"gw_{instance}_document_id"
            : $"gw_{instance}_id";
        var comparisonSuffix = linked ? "_document_id_comparison_key" : "_id_comparison_key";
        var normalizer = new DelegateProviderPhysicalNameNormalizer(
            context => context.LogicalName.EndsWith(comparisonSuffix, StringComparison.Ordinal)
                ? SqlServerPhysicalIdentity.HiddenColumn(original)
                : SqlServerGroundworkCapabilities.PhysicalNames.Normalize(context),
            context => SqlServerGroundworkCapabilities.PhysicalNames.GetCollisionScope(context));
        var model = RelationalPhysicalStorageTestModels.Create(
            linked ? PhysicalStorageForm.SharedDocuments : PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);

        var exception = Assert.Throws<InvalidOperationException>(() => new SqlServerPhysicalDocumentStore(
            "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global));

        Assert.Contains(SqlServerPhysicalIdentity.HiddenColumn(original), exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider-owned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sql_server_rejects_case_only_provider_owned_identity_column_collisions(bool linked)
    {
        var instance = linked ? "linked_case_hidden_collision" : "primary_case_hidden_collision";
        var original = linked
            ? $"gw_{instance}_document_id"
            : $"gw_{instance}_id";
        var comparisonSuffix = linked ? "_document_id_comparison_key" : "_id_comparison_key";
        var normalizer = new DelegateProviderPhysicalNameNormalizer(
            context => context.LogicalName.EndsWith(comparisonSuffix, StringComparison.Ordinal)
                ? original.ToUpperInvariant()
                : SqlServerGroundworkCapabilities.PhysicalNames.Normalize(context),
            context => SqlServerGroundworkCapabilities.PhysicalNames.GetCollisionScope(context));
        var model = RelationalPhysicalStorageTestModels.Create(
            linked ? PhysicalStorageForm.SharedDocuments : PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: instance,
            normalizer: normalizer);

        var exception = Assert.Throws<InvalidOperationException>(() => new SqlServerPhysicalDocumentStore(
            "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider-owned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_server_accepts_distinct_long_retained_identity_names_after_hidden_name_normalization()
    {
        var prefix = new string('x', 126);
        var normalizer = new DelegateProviderPhysicalNameNormalizer(
            context => (context.ObjectKind, context.LogicalName) switch
            {
                (PhysicalObjectKind.EnvelopeField, var name) when name.EndsWith("_id_comparison_key", StringComparison.Ordinal) => prefix + "pc",
                (PhysicalObjectKind.EnvelopeField, var name) when name.EndsWith("_id_lookup_key", StringComparison.Ordinal) => prefix + "pl",
                (PhysicalObjectKind.LinkedIndexField, var name) when name.EndsWith("_document_id_comparison_key", StringComparison.Ordinal) => prefix + "lc",
                (PhysicalObjectKind.LinkedIndexField, var name) when name.EndsWith("_document_id_lookup_key", StringComparison.Ordinal) => prefix + "ll",
                _ => SqlServerGroundworkCapabilities.PhysicalNames.Normalize(context)
            },
            context => SqlServerGroundworkCapabilities.PhysicalNames.GetCollisionScope(context));
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.SharedDocuments,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false,
            instance: "long_retained_identity",
            normalizer: normalizer);
        var route = model.Target.Routes.Single();

        _ = new SqlServerPhysicalDocumentStore(
            "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            model.Manifest,
            model.Target.Routes,
            DocumentStoreAccess.Global);

        var primaryHidden = IdentityColumns(route.Envelope.Identity, route.Envelope.DocumentKind, route.Envelope.StorageScope)
            .Select(SqlServerPhysicalIdentity.HiddenColumn)
            .ToArray();
        var linkedHidden = IdentityColumns(
                route.LinkedRelationship!.Identity,
                route.LinkedRelationship.DocumentKind,
                route.LinkedRelationship.StorageScope)
            .Select(SqlServerPhysicalIdentity.HiddenColumn)
            .ToArray();
        Assert.All(primaryHidden.Concat(linkedHidden), name => Assert.True(name.Length <= 128));
        Assert.Equal(primaryHidden.Length, primaryHidden.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(linkedHidden.Length, linkedHidden.Distinct(StringComparer.Ordinal).Count());

        static string[] IdentityColumns(
            ExecutableDocumentIdentityRoute identity,
            ExecutableColumnRoute documentKind,
            ExecutableColumnRoute storageScope) =>
        [
            documentKind.Identifier,
            storageScope.Identifier,
            identity.OriginalId.Identifier,
            identity.ComparisonKey.Identifier,
            identity.LookupKey.Identifier
        ];
    }

    [Theory]
    [InlineData(PortablePhysicalType.String)]
    [InlineData(PortablePhysicalType.Binary)]
    public void Sql_server_rejects_max_length_physical_index_keys_before_ddl(PortablePhysicalType type)
    {
        var route = SqlServerRouteWithIndexedCategory(
            new ProjectedColumnDefinition("category", "category", type));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SqlServerPhysicalSchemaDialect().ValidateRoute(route));

        Assert.Contains("by-category", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"bounded {type}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(850, true)]
    [InlineData(851, false)]
    public void Sql_server_enforces_the_1700_byte_physical_index_key_boundary_for_unicode_strings(
        int length,
        bool supported)
    {
        var route = SqlServerRouteWithIndexedCategory(
            new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String, Length: length));
        var dialect = new SqlServerPhysicalSchemaDialect();

        if (supported)
            dialect.ValidateRoute(route);
        else
            Assert.Throws<InvalidOperationException>(() => dialect.ValidateRoute(route));
    }

    [Theory]
    [InlineData(722, true)]
    [InlineData(723, false)]
    public void Sql_server_counts_the_physical_storage_scope_prefix_toward_index_key_width(
        int stringLength,
        bool supported)
    {
        var route = SqlServerRouteWithIndexedCategory(
            new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String, Length: stringLength),
            includeStorageScope: true);
        var dialect = new SqlServerPhysicalSchemaDialect();

        if (supported)
            dialect.ValidateRoute(route);
        else
            Assert.Throws<InvalidOperationException>(() => dialect.ValidateRoute(route));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, false)]
    public void Sql_server_counts_a_physical_discriminator_prefix_toward_index_key_width(
        int stringLength,
        bool supported)
    {
        var category = new ProjectedColumnDefinition(
            "category", "category", PortablePhysicalType.String, Length: stringLength);
        var route = SqlServerRouteWithIndex([category], ["document_kind", "category"]);
        var dialect = new SqlServerPhysicalSchemaDialect();

        if (supported)
            dialect.ValidateRoute(route);
        else
            Assert.Throws<InvalidOperationException>(() => dialect.ValidateRoute(route));
    }

    [Theory]
    [InlineData(1700, true)]
    [InlineData(1701, false)]
    public void Sql_server_supports_only_bounded_binary_keys_within_the_1700_byte_limit(
        int length,
        bool supported)
    {
        var route = SqlServerRouteWithIndexedCategory(
            new ProjectedColumnDefinition("category", "category", PortablePhysicalType.Binary, Length: length));
        var dialect = new SqlServerPhysicalSchemaDialect();

        if (supported)
            dialect.ValidateRoute(route);
        else
            Assert.Throws<InvalidOperationException>(() => dialect.ValidateRoute(route));
    }

    [Fact]
    public void Sql_server_rejects_json_physical_index_keys_before_ddl()
    {
        var route = SqlServerRouteWithIndexedCategory(
            new ProjectedColumnDefinition("category", "category", PortablePhysicalType.Json));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SqlServerPhysicalSchemaDialect().ValidateRoute(route));

        Assert.Contains("by-category", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Json", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortablePhysicalType.String)]
    [InlineData(PortablePhysicalType.Int32)]
    [InlineData(PortablePhysicalType.Int64)]
    [InlineData(PortablePhysicalType.Decimal)]
    [InlineData(PortablePhysicalType.Boolean)]
    [InlineData(PortablePhysicalType.DateTime)]
    [InlineData(PortablePhysicalType.Guid)]
    [InlineData(PortablePhysicalType.Binary)]
    public void Sql_server_accepts_each_supported_bounded_physical_index_key_type(PortablePhysicalType type)
    {
        var definition = type switch
        {
            PortablePhysicalType.String or PortablePhysicalType.Binary =>
                new ProjectedColumnDefinition("category", "category", type, Length: 1),
            PortablePhysicalType.Decimal =>
                new ProjectedColumnDefinition("category", "category", type, Precision: 28, Scale: 4),
            _ => new ProjectedColumnDefinition("category", "category", type)
        };
        var route = SqlServerRouteWithIndexedCategory(definition);

        new SqlServerPhysicalSchemaDialect().ValidateRoute(route);
    }

    [Theory]
    [InlineData(848, true)]
    [InlineData(849, false)]
    public void Sql_server_counts_fixed_width_projected_columns_toward_compound_index_keys(
        int stringLength,
        bool supported)
    {
        var definitions = new ProjectedColumnDefinition[]
        {
            new("category", "category", PortablePhysicalType.String, Length: stringLength),
            new("rank", "rank", PortablePhysicalType.Int32)
        };
        var route = SqlServerRouteWithIndex(definitions, ["category", "rank"]);
        var dialect = new SqlServerPhysicalSchemaDialect();

        if (supported)
            dialect.ValidateRoute(route);
        else
            Assert.Throws<InvalidOperationException>(() => dialect.ValidateRoute(route));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Sql_server_enforces_the_32_column_physical_index_key_limit(int columnCount, bool supported)
    {
        var definitions = Enumerable.Range(0, columnCount)
            .Select(index => index == 0
                ? new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String, Length: 1)
                : new ProjectedColumnDefinition($"key-{index}", $"key{index}", PortablePhysicalType.Int32))
            .ToArray();
        var route = SqlServerRouteWithIndex(definitions, definitions.Select(x => x.LogicalName).ToArray());
        var dialect = new SqlServerPhysicalSchemaDialect();

        if (supported)
            dialect.ValidateRoute(route);
        else
            Assert.Throws<InvalidOperationException>(() => dialect.ValidateRoute(route));
    }

    [Fact]
    public void Sql_server_exact_identity_predicates_and_joins_verify_hash_and_original_values()
    {
        var dialect = new SqlServerPhysicalDocumentDialect();

        var predicate = dialect.ExactIdentityPredicate(
        [
            new RelationalPhysicalIdentityPredicatePart("document_kind", "p", "@kind"),
            new RelationalPhysicalIdentityPredicatePart("storage_scope", "p", "@scope"),
            new RelationalPhysicalIdentityPredicatePart("id", "p", "@id")
        ]);
        var hashOnly = dialect.HashOnlyIdentityPredicate(
        [
            new RelationalPhysicalIdentityPredicatePart("document_kind", "p", "@kind"),
            new RelationalPhysicalIdentityPredicatePart("storage_scope", "p", "@scope"),
            new RelationalPhysicalIdentityPredicatePart("id", "p", "@id")
        ]);
        var join = dialect.ExactIdentityJoin(
        [
            new RelationalPhysicalIdentityJoinPart("document_kind", "p", "kind_fk", "l"),
            new RelationalPhysicalIdentityJoinPart("storage_scope", "p", "scope_fk", "l"),
            new RelationalPhysicalIdentityJoinPart("id", "p", "document_fk", "l")
        ]);

        Assert.Contains("p.[document_kind_key] = CONVERT(binary(32), HASHBYTES('SHA2_256'", predicate, StringComparison.Ordinal);
        Assert.Contains("p.[document_kind] = @kind", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("p.[document_kind] = @kind", hashOnly, StringComparison.Ordinal);
        Assert.Contains("p.[document_kind_key] = l.[kind_fk_key]", join, StringComparison.Ordinal);
        Assert.Contains("p.[document_kind] = l.[kind_fk]", join, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(28, true)]
    [InlineData(29, false)]
    public void Server_relational_decimal_precision_matches_the_portable_exact_decimal_boundary(
        int precision,
        bool supported)
    {
        var definition = new ProjectedColumnDefinition(
            "amount", "amount", PortablePhysicalType.Decimal, Precision: precision, Scale: 4);
        var dialects = new Groundwork.Relational.PhysicalStorage.RelationalServerPhysicalSchemaDialect[]
        {
            new SqlServerPhysicalSchemaDialect(),
            new PostgreSqlPhysicalSchemaDialect()
        };

        foreach (var dialect in dialects)
        {
            if (supported)
                dialect.Validate(definition);
            else
                Assert.Throws<InvalidOperationException>(() => dialect.Validate(definition));
        }
    }

    [Fact]
    public void Postgre_sql_preserves_portable_datetime_ticks_instead_of_rounding_to_microseconds()
    {
        var definition = new ProjectedColumnDefinition("at", "at", PortablePhysicalType.DateTime);
        var value = DateTimeOffset.Parse("2026-01-01T00:00:00.0000001+01:00");
        var schema = new PostgreSqlPhysicalSchemaDialect();
        var documents = new PostgreSqlPhysicalDocumentDialect();

        Assert.Equal("bigint", schema.ProjectedType(definition));
        Assert.Equal(value.UtcDateTime.Ticks, schema.ConvertStorageValue(value, definition));
        Assert.Equal(value.UtcDateTime.Ticks, documents.ConvertProjectionValue(value, definition));
    }

    [Fact]
    public void Sql_server_normalizes_datetimeoffset_values_to_utc_without_losing_ticks()
    {
        var definition = new ProjectedColumnDefinition("at", "at", PortablePhysicalType.DateTime);
        var value = DateTimeOffset.Parse("2026-01-01T00:00:00.0000001+01:00");
        var schema = new SqlServerPhysicalSchemaDialect();
        var documents = new SqlServerPhysicalDocumentDialect();

        Assert.Equal("datetimeoffset(7)", schema.ProjectedType(definition));
        Assert.Equal(value.ToUniversalTime(), schema.ConvertStorageValue(value, definition));
        Assert.Equal(value.ToUniversalTime(), documents.ConvertProjectionValue(value, definition));
    }

    [Fact]
    public void Provider_name_normalizers_apply_native_limits_without_colliding_long_names()
    {
        var context = new ProviderPhysicalNameContext(
            new Groundwork.Core.Manifests.StorageUnitIdentity("unit"),
            PhysicalObjectKind.PrimaryStorage,
            new string('x', 140));
        var other = context with { LogicalName = context.LogicalName + "y" };
        var sqlName = SqlServerGroundworkCapabilities.PhysicalNames.Normalize(context);
        var sqlOther = SqlServerGroundworkCapabilities.PhysicalNames.Normalize(other);
        Assert.True(sqlName.Length <= 128);
        Assert.NotEqual(sqlName, sqlOther);

        var unicode = context with { LogicalName = string.Concat(Enumerable.Repeat("é", 80)) };
        var pgName = PostgreSqlGroundworkCapabilities.PhysicalNames.Normalize(unicode);
        var pgOther = PostgreSqlGroundworkCapabilities.PhysicalNames.Normalize(unicode with { LogicalName = unicode.LogicalName + "x" });
        Assert.True(Encoding.UTF8.GetByteCount(pgName) <= 63);
        Assert.NotEqual(pgName, pgOther);
    }

    [Fact]
    public void Postgre_sql_json_paths_bind_each_stable_segment_without_array_literal_ambiguity()
    {
        var dialect = new PostgreSqlPhysicalDocumentDialect();

        Assert.Equal(
            "jsonb_extract_path_text((p.canonical_json)::jsonb, 'a,b', 'quote''s')",
            dialect.JsonValue("p.canonical_json", "a,b.quote's"));
    }

    private static ExecutableStorageRoute SqlServerRouteWithIndexedCategory(
        ProjectedColumnDefinition definition,
        bool includeStorageScope = false)
        => SqlServerRouteWithIndex([definition], [definition.LogicalName], includeStorageScope);

    private static ExecutableStorageRoute SqlServerRouteWithIndex(
        IReadOnlyList<ProjectedColumnDefinition> definitions,
        IReadOnlyList<string> indexColumns,
        bool includeStorageScope = false)
    {
        var model = RelationalPhysicalStorageTestModels.Create(
            PhysicalStorageForm.PhysicalEntityTable,
            SqlServerGroundworkCapabilities.Provider,
            includePriority: false);
        var unit = model.Manifest.StorageUnits.Single();
        var physicalStorage = unit.PhysicalStorage!;
        var table = PhysicalTableDefinition.PhysicalEntityTable(
            "configuration_entities",
            definitions,
            indexes:
            [
                new PhysicalIndexDefinition(
                    "by-category",
                    (includeStorageScope ? new[] { "storage_scope" } : [])
                    .Concat(indexColumns)
                    .Select((column, order) => new PhysicalIndexColumnDefinition(column, order))
                    .ToArray())
            ]);
        var manifest = model.Manifest with
        {
            StorageUnits =
            [
                unit with
                {
                    PhysicalStorage = new StorageUnitPhysicalStorage(
                        physicalStorage.ProvisioningMode,
                        PhysicalStoragePolicy.Explicit(table),
                        logicalIndexes: [],
                        boundedQueries: [],
                        nameOverrides: physicalStorage.NameOverrides)
                }
            ]
        };
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            new DelegatePhysicalNamePolicy(context => $"gw_key_test_{context.FeatureDefaultLogicalName}"),
            SqlServerGroundworkCapabilities.PhysicalNames);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(x => x.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(x => x.Message)));
        return compilation.Routes.Single();
    }

    private static (StorageManifest Manifest, ExecutableStorageRoute Route) SqlServerIdentityAcceptanceRoute(
        DocumentIdentityAcceptanceSurface surface)
    {
        var manifest = DocumentIdentityAcceptanceModel.Manifest(
            PhysicalStorageForm.PhysicalEntityTable,
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
            surface,
            Guid.NewGuid().ToString("N")[..8]);
        var resolution = PhysicalStorageResolver.Resolve(
            manifest,
            PhysicalNamePolicy.Identity,
            SqlServerGroundworkCapabilities.PhysicalNames);
        Assert.True(resolution.IsValid, string.Join("; ", resolution.Diagnostics.Select(item => item.Message)));
        var compilation = ExecutableStorageRouteCompiler.Compile(resolution.Definitions);
        Assert.True(compilation.IsValid, string.Join("; ", compilation.Diagnostics.Select(item => item.Message)));
        return (manifest, Assert.Single(compilation.Routes));
    }

    private static string ReadConstraintName(string createTableSql)
    {
        const string marker = "CONSTRAINT [";
        var start = createTableSql.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = createTableSql.IndexOf(']', start);
        return createTableSql[start..end];
    }
}
