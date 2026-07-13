using Groundwork.Core.PhysicalStorage;
using Groundwork.PostgreSql.PhysicalStorage;
using Groundwork.Relational.PhysicalStorage;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class RelationalPhysicalProviderHardeningTests
{
    [Theory]
    [InlineData(RelationalEnvelopeColumnKind.DocumentKind)]
    [InlineData(RelationalEnvelopeColumnKind.StorageScope)]
    [InlineData(RelationalEnvelopeColumnKind.Id)]
    [InlineData(RelationalEnvelopeColumnKind.SchemaVersion)]
    [InlineData(RelationalEnvelopeColumnKind.Timestamp)]
    public void Postgre_sql_text_envelope_columns_use_explicit_ordinal_collation(
        RelationalEnvelopeColumnKind kind)
    {
        var dialect = new PostgreSqlPhysicalSchemaDialect();

        Assert.Equal("C", dialect.EnvelopeCollation(kind));
        Assert.Contains("COLLATE \"pg_catalog\".\"C\"", dialect.EnvelopeColumn("value", kind), StringComparison.Ordinal);
    }

    [Fact]
    public void Postgre_sql_default_string_projection_uses_explicit_ordinal_collation()
    {
        var dialect = new PostgreSqlPhysicalSchemaDialect();
        var projected = new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String);

        Assert.Equal("C", dialect.ProjectedCollation(projected));
    }

    [Fact]
    public void Postgre_sql_only_accepts_pg_catalog_C_as_the_ordinal_collation_identity()
    {
        var dialect = new PostgreSqlPhysicalSchemaDialect();

        Assert.Equal("C", dialect.NormalizeCollationIdentity("pg_catalog:C"));
        Assert.NotEqual(
            dialect.NormalizeCollationIdentity("pg_catalog:C"),
            dialect.NormalizeCollationIdentity("tenant_schema:C"));
    }

    [Fact]
    public void Sql_server_identity_hash_column_must_be_persisted_computed_sha256()
    {
        var dialect = new SqlServer.PhysicalStorage.SqlServerPhysicalSchemaDialect();
        var expected = dialect.IdentityLayout(
            [new RelationalPhysicalIdentityColumn("document_kind", RelationalEnvelopeColumnKind.DocumentKind)],
            ["document_kind"]).ProviderColumns.Single();
        var plainBinary = new RelationalPhysicalColumnMetadata(
            expected.Name,
            expected.Type,
            false,
            null,
            null,
            1,
            IsComputed: false,
            IsPersisted: false,
            ComputedDefinition: null);

        Assert.False(dialect.IsProviderOwnedColumnCompatible(expected, plainBinary));
    }
}
