using Groundwork.DiagnosticRecords;
using Groundwork.DiagnosticRecords.Tests;
using Groundwork.PostgreSql.DiagnosticRecords;
using Groundwork.SqlServer.DiagnosticRecords;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticRecordProviderApiCollection
{
    public const string Name = "Diagnostic record provider API";
}

[Collection(DiagnosticRecordProviderApiCollection.Name)]
public sealed class DiagnosticRecordProviderApiTests
{
    private static readonly DiagnosticRecordStreamDefinition Definition = new(
        new("logs"),
        1,
        "diagnostic_logs",
        [],
        new(MaxRecordIdBytes: 128),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(1));

    [Fact]
    public void Sql_server_exposes_a_provider_native_diagnostic_store()
    {
        var store = new SqlServerDiagnosticRecordStore(
            "Server=localhost;Database=groundwork;User ID=sa;Password=NotARealPassword1!;TrustServerCertificate=True",
            Definition);

        Assert.NotNull(store.Handlers);
        var exactBudget = Definition with
        {
            Fields =
            [
                new(
                    "key",
                    DiagnosticFieldType.String,
                    DiagnosticFieldCardinality.Scalar,
                    new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.In },
                    IsOrderable: true,
                    SupportsLatestPerKey: true,
                    MaxStringBytes: 1)
            ],
            Limits = new(MaxRecordIdBytes: 128, MaxPredicateNodes: 1, MaxPredicateValues: 1_043)
        };
        var exactStore = new SqlServerDiagnosticRecordStore(
            "Server=localhost;Database=groundwork;User ID=sa;Password=NotARealPassword1!;TrustServerCertificate=True",
            exactBudget);
        Assert.NotNull(exactStore.Handlers);
        var values = Enumerable.Repeat(DiagnosticFieldValue.String("a"), 1_043).ToArray();
        var query = new DiagnosticRecordQuery(
            new("tenant-a", "scope-a"),
            exactBudget.Stream,
            1,
            Order: new("key"),
            Predicate: DiagnosticRecordPredicate.In("key", values),
            LatestPerKeyField: "key");
        var continuation = new DiagnosticRecordContinuation(
            new("1"),
            new("1"),
            DiagnosticRequestFingerprint.ForQuery(query, exactBudget),
            DiagnosticFieldValue.String("a"));
        Assert.Equal(2_100, exactStore.Inner.BuildQueryCommand(query with { Continuation = continuation }, 1).Parameters.Count);
        var overBudget = exactBudget with { Limits = new(MaxRecordIdBytes: 128, MaxPredicateNodes: 1, MaxPredicateValues: 1_044) };
        var exception = Assert.Throws<DiagnosticRecordValidationException>(() => new SqlServerDiagnosticRecordStore(
            "Server=localhost;Database=groundwork;User ID=sa;Password=NotARealPassword1!;TrustServerCertificate=True",
            overBudget));
        Assert.Contains(exception.Errors, error => error.Code == "provider.sql_server.parameter_budget.exceeded");
    }

    [Fact]
    public void Postgre_sql_exposes_a_provider_native_diagnostic_store()
    {
        var store = new PostgreSqlDiagnosticRecordStore(
            "Host=localhost;Database=groundwork;Username=groundwork;Password=not-real",
            Definition);

        Assert.NotNull(store.Handlers);
        var exactBudget = Definition with { Limits = new(MaxRecordIdBytes: 128, MaxPredicateNodes: 1, MaxPredicateValues: 32_760) };
        Assert.NotNull(new PostgreSqlDiagnosticRecordStore(
            "Host=localhost;Database=groundwork;Username=groundwork;Password=not-real",
            exactBudget).Handlers);
        var overBudget = exactBudget with { Limits = new(MaxRecordIdBytes: 128, MaxPredicateNodes: 1, MaxPredicateValues: 32_761) };
        var exception = Assert.Throws<DiagnosticRecordValidationException>(() => new PostgreSqlDiagnosticRecordStore(
            "Host=localhost;Database=groundwork;Username=groundwork;Password=not-real",
            overBudget));
        Assert.Contains(exception.Errors, error => error.Code == "provider.postgresql.parameter_budget.exceeded");
    }

    [Fact]
    public void Relational_providers_expose_admission_gated_native_plan_inspectors()
    {
        var sqlServer = SqlServerDiagnosticRecordStoreFactory.CreatePlanInspector(
            "Server=localhost;Database=groundwork;User ID=sa;Password=NotARealPassword1!;TrustServerCertificate=True");
        var postgreSql = PostgreSqlDiagnosticRecordStoreFactory.CreatePlanInspector(
            "Host=localhost;Database=groundwork;Username=groundwork;Password=not-real");

        Assert.Equal("sqlserver", sqlServer.Provider);
        Assert.Equal("postgresql", postgreSql.Provider);
        Assert.IsAssignableFrom<IDiagnosticRecordPlanInspector>(sqlServer);
        Assert.IsAssignableFrom<IDiagnosticRecordPlanInspector>(postgreSql);
    }

    [Fact]
    public void Relational_providers_reject_declared_string_bounds_beyond_adapter_capabilities_before_io()
    {
        var oversized = Definition with
        {
            Fields =
            [
                new("message", DiagnosticFieldType.String, DiagnosticFieldCardinality.Scalar,
                    new HashSet<DiagnosticPredicateOperator> { DiagnosticPredicateOperator.Contains },
                    MaxStringBytes: 65_537)
            ]
        };

        var sqlServer = Assert.Throws<DiagnosticRecordValidationException>(() =>
            new SqlServerDiagnosticRecordStore(
                "Server=unreachable;Database=groundwork;User ID=sa;Password=not-used",
                oversized));
        var postgreSql = Assert.Throws<DiagnosticRecordValidationException>(() =>
            new PostgreSqlDiagnosticRecordStore(
                "Host=unreachable;Database=groundwork;Username=groundwork;Password=not-used",
                oversized));

        Assert.Contains(sqlServer.Errors, error => error.Code == "provider.sql_server.string_bound.too_large");
        Assert.Contains(postgreSql.Errors, error => error.Code == "provider.postgresql.string_bound.too_large");
    }

    [Fact]
    public async Task Sql_server_uses_shared_instrumentation_once_on_every_public_route()
    {
        var store = new SqlServerDiagnosticRecordStore(
            "Server=localhost;Database=groundwork;User ID=sa;Password=NotARealPassword1!;TrustServerCertificate=True",
            Definition);

        await DiagnosticRecordInstrumentationAssertions.AssertProviderRoutesAsync(
            store,
            "sqlserver",
            new(
                request => store.AppendAsync(request),
                request => store.QueryAsync(request),
                request => store.InspectAsync(request),
                request => store.TrimAsync(request)));
    }

    [Fact]
    public async Task Postgre_sql_uses_shared_instrumentation_once_on_every_public_route()
    {
        var store = new PostgreSqlDiagnosticRecordStore(
            "Host=localhost;Database=groundwork;Username=groundwork;Password=not-real",
            Definition);

        await DiagnosticRecordInstrumentationAssertions.AssertProviderRoutesAsync(
            store,
            "postgresql",
            new(
                request => store.AppendAsync(request),
                request => store.QueryAsync(request),
                request => store.InspectAsync(request),
                request => store.TrimAsync(request)));
    }

}
