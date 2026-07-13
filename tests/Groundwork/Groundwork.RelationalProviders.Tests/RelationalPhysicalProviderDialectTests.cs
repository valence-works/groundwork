using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
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

        Assert.Equal("Latin1_General_100_BIN2", dialect.EnvelopeCollation(RelationalEnvelopeColumnKind.Id));
        Assert.Equal("Latin1_General_100_BIN2", dialect.ProjectedCollation(projected));
        Assert.Contains("COLLATE Latin1_General_100_BIN2", dialect.EnvelopeColumn("id", RelationalEnvelopeColumnKind.Id));
        Assert.Contains("PRIMARY KEY NONCLUSTERED", dialect.CreateTableSql("records", ["[id] nvarchar(450) NOT NULL"], ["id"]));
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
