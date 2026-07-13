using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.PostgreSql.Documents;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.Documents;
using Groundwork.Relational.PhysicalStorage;
using Groundwork.SqlServer.Documents;
using Groundwork.SqlServer.PhysicalStorage;
using Groundwork.SqlServer;
using Groundwork.PostgreSql;
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

    private static string ReadConstraintName(string createTableSql)
    {
        const string marker = "CONSTRAINT [";
        var start = createTableSql.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = createTableSql.IndexOf(']', start);
        return createTableSql[start..end];
    }
}
