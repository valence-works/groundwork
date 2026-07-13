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
}
