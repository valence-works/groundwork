using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Groundwork.SchemaTool;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class GroundworkSchemaCliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"groundwork-schema-tool-{Guid.NewGuid():N}");
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public GroundworkSchemaCliTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Validate_emits_deterministic_json_without_touching_the_database()
    {
        var database = Path.Combine(directory, "must-not-exist.db");
        var arguments = Arguments("validate", database);

        var firstExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        var first = output.ToString();
        output.GetStringBuilder().Clear();
        var secondExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);

        Assert.Equal(SchemaToolExitCodes.Success, firstExit);
        Assert.Equal(firstExit, secondExit);
        Assert.Equal(first, output.ToString());
        Assert.False(File.Exists(database));
        Assert.Equal(string.Empty, error.ToString());

        using var report = JsonDocument.Parse(first);
        var root = report.RootElement;
        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("validate", root.GetProperty("command").GetString());
        Assert.Equal("ready", root.GetProperty("outcome").GetString());
        Assert.Equal("live", root.GetProperty("inspectionMode").GetString());
        Assert.Equal("groundwork-sqlite", root.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("schema-tool-tests", root.GetProperty("target").GetProperty("manifestIdentity").GetString());
        Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength());
        Assert.True(root.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.Contains(
            root.GetProperty("resolvedNames").EnumerateArray(),
            name => name.GetProperty("kind").GetString() == "envelopeField" &&
                    name.GetProperty("logicalName").GetString() == "id" &&
                    name.GetProperty("identifier").GetString() == "id");
    }

    [Fact]
    public async Task Offline_validation_is_explicit_and_does_not_require_provider_connectivity()
    {
        var database = Path.Combine(directory, "offline-must-not-exist.db");
        var arguments = Arguments("validate", database).ToList();
        var connection = arguments.IndexOf("--connection");
        arguments.RemoveRange(connection, 2);
        arguments.Add("--offline");

        var exit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.Success, exit);
        Assert.Equal("offline", report.RootElement.GetProperty("inspectionMode").GetString());
        Assert.False(File.Exists(database));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Offline_validation_rejects_all_provider_connection_input()
    {
        var database = Path.Combine(directory, "offline-database-input.db");
        var arguments = Arguments("validate", database).ToList();
        var connection = arguments.IndexOf("--connection");
        arguments.RemoveRange(connection, 2);
        var outputOption = arguments.IndexOf("--output");
        arguments.RemoveRange(outputOption, 2);
        arguments.AddRange(["--database", "provider-database", "--offline"]);

        var exit = await GroundworkSchemaCli.RunAsync(arguments, output, error);

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exit);
        Assert.Contains("Offline validation cannot accept connection input.", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(database));
    }

    [Theory]
    [InlineData("sqlite", "groundwork-sqlite")]
    [InlineData("sqlserver", "groundwork-sqlserver")]
    [InlineData("postgresql", "groundwork-postgresql")]
    [InlineData("mongodb", "groundwork-mongodb")]
    public async Task Offline_validate_composes_every_available_provider_without_provider_types_in_the_manifest_source(
        string provider,
        string expectedIdentity)
    {
        var database = Path.Combine(directory, $"{provider}-must-not-exist.db");
        var arguments = Arguments("validate", database, provider: provider).ToList();
        var connection = arguments.IndexOf("--connection");
        arguments.RemoveRange(connection, 2);
        arguments.Add("--offline");

        var exit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.Success, exit);
        Assert.Equal(expectedIdentity, report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.False(File.Exists(database));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Unknown_provider_is_an_invalid_invocation()
    {
        var database = Path.Combine(directory, "unknown-must-not-exist.db");
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Groundwork.SchemaTool",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity
        };
        ActivitySource.AddActivityListener(listener);

        var exit = await GroundworkSchemaCli.RunAsync(
            Arguments("validate", database, provider: $"rejected-{Guid.NewGuid():N}"),
            output,
            error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exit);
        Assert.Equal("invalid", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "GW-CLI-002",
            Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(File.Exists(database));
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(stopped);
        Assert.Equal("unknown", stopped.GetTagItem("groundwork.provider"));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("status")]
    public async Task Read_commands_report_pending_sqlite_work_with_pipeline_exit_code(string command)
    {
        var database = Path.Combine(directory, $"{command}.db");
        var arguments = Arguments(command, database);

        var firstExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        var first = output.ToString();
        output.GetStringBuilder().Clear();
        var secondExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);

        Assert.Equal(SchemaToolExitCodes.PendingChanges, firstExit);
        Assert.Equal(firstExit, secondExit);
        Assert.Equal(first, output.ToString());
        Assert.True(File.Exists(database));
        Assert.Equal(string.Empty, error.ToString());

        using var report = JsonDocument.Parse(first);
        var root = report.RootElement;
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.Equal("pending", root.GetProperty("outcome").GetString());
        Assert.NotEqual(string.Empty, root.GetProperty("planFingerprint").GetString());
        Assert.Equal(0, root.GetProperty("appliedOperations").GetArrayLength());
        Assert.True(root.GetProperty("pendingOperations").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Apply_materializes_sqlite_and_restart_reports_no_changes()
    {
        var database = Path.Combine(directory, "apply.db");
        var arguments = Arguments("apply", database).Concat(["--safe"]).ToArray();

        var firstExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        using var first = ParseOutput();
        var validateExit = await GroundworkSchemaCli.RunAsync(Arguments("validate", database), output, error);
        using var validate = ParseOutput();
        var secondExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        using var second = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.Success, firstExit);
        Assert.Equal("applied", first.RootElement.GetProperty("outcome").GetString());
        Assert.True(first.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Equal(0, first.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(first.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);
        Assert.Equal(SchemaToolExitCodes.Success, validateExit);
        Assert.Equal("live", validate.RootElement.GetProperty("inspectionMode").GetString());
        Assert.Equal(0, validate.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(validate.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);
        Assert.False(validate.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Equal(SchemaToolExitCodes.Success, secondExit);
        Assert.Equal("ready", second.RootElement.GetProperty("outcome").GetString());
        Assert.False(second.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Equal(first.RootElement.GetProperty("appliedTargetFingerprint").GetString(),
            second.RootElement.GetProperty("appliedTargetFingerprint").GetString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Live_validate_reports_physical_drift_without_repairing_it()
    {
        var database = Path.Combine(directory, "drift.db");
        var applyExit = await GroundworkSchemaCli.RunAsync(
            Arguments("apply", database).Concat(["--safe"]).ToArray(),
            output,
            error);
        using var applied = ParseOutput();
        Assert.Equal(SchemaToolExitCodes.Success, applyExit);

        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX \"by-category\";";
            await command.ExecuteNonQueryAsync();
        }

        var validateExit = await GroundworkSchemaCli.RunAsync(
            Arguments("validate", database),
            output,
            error);
        using var validation = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.ValidationFailed, validateExit);
        Assert.Equal("blocked", validation.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "GW-CLI-012",
            Assert.Single(validation.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(await IndexExistsAsync(database, "by-category"));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Apply_requires_exact_destructive_and_semantic_authorization_before_target_operations()
    {
        var database = Path.Combine(directory, "authorization.db");
        var safe = Arguments("apply", database, typeof(AuthorizationManifestSource))
            .Concat(["--safe"])
            .ToArray();

        var blockedExit = await GroundworkSchemaCli.RunAsync(safe, output, error);
        using var blocked = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.AuthorizationRequired, blockedExit);
        Assert.Equal("authorization-required", blocked.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, blocked.RootElement.GetProperty("diagnostics").GetArrayLength());
        Assert.False(await TableExistsAsync(database, "schema_tool_documents"));

        var planExit = await GroundworkSchemaCli.RunAsync(
            Arguments("plan", database, typeof(AuthorizationManifestSource)),
            output,
            error);
        using var plan = ParseOutput();
        var planFingerprint = plan.RootElement.GetProperty("planFingerprint").GetString()!;
        var destructiveOperation = plan.RootElement.GetProperty("pendingOperations")
            .EnumerateArray()
            .Single(operation => operation.GetProperty("kind").GetString() == "CreatePhysicalEntityStorage")
            .GetProperty("identity")
            .GetString()!;
        Assert.Equal(SchemaToolExitCodes.PendingChanges, planExit);

        var stale = Arguments("apply", database, typeof(AuthorizationManifestSource))
            .Concat([
                "--expected-plan", new string('0', 64),
                "--allow-destructive", destructiveOperation,
                "--allow-semantic", "reclassify-v2"
            ])
            .ToArray();
        var staleExit = await GroundworkSchemaCli.RunAsync(stale, output, error);
        using var staleReport = ParseOutput();
        Assert.Equal(SchemaToolExitCodes.AuthorizationRequired, staleExit);
        Assert.Contains(
            staleReport.RootElement.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "GW-CLI-011");
        Assert.False(await TableExistsAsync(database, "schema_tool_documents"));

        var authorized = Arguments("apply", database, typeof(AuthorizationManifestSource))
            .Concat([
                "--expected-plan", planFingerprint,
                "--allow-destructive", destructiveOperation,
                "--allow-semantic", "reclassify-v2"
            ])
            .ToArray();
        var appliedExit = await GroundworkSchemaCli.RunAsync(authorized, output, error);
        using var applied = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.Success, appliedExit);
        Assert.Equal("applied", applied.RootElement.GetProperty("outcome").GetString());
        Assert.True(await TableExistsAsync(database, "schema_tool_documents"));

        var restartExit = await GroundworkSchemaCli.RunAsync(safe, output, error);
        using var restart = ParseOutput();
        Assert.Equal(SchemaToolExitCodes.Success, restartExit);
        Assert.Equal("ready", restart.RootElement.GetProperty("outcome").GetString());
        Assert.False(restart.RootElement.GetProperty("authorization").GetProperty("destructiveRequired").GetBoolean());
        Assert.Equal(0, restart.RootElement.GetProperty("authorization").GetProperty("semanticRequired").GetArrayLength());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Apply_requires_safe_or_exact_plan_bound_authorization_mode()
    {
        var database = Path.Combine(directory, "mode-required.db");

        var exit = await GroundworkSchemaCli.RunAsync(Arguments("apply", database), output, error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exit);
        Assert.Equal("GW-CLI-001", Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task Execution_failures_and_telemetry_never_expose_connection_secrets()
    {
        const string secret = "do-not-leak-49";
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Groundwork.SchemaTool",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity
        };
        ActivitySource.AddActivityListener(listener);
        var arguments = Arguments("plan", "unused", provider: "sqlserver");
        var connectionIndex = Array.IndexOf(arguments, "--connection") + 1;
        arguments[connectionIndex] = $"Server=127.0.0.1,1;Database=none;User Id=none;Password={secret};Connect Timeout=1;Encrypt=false";

        var exit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.ExecutionFailed, exit);
        Assert.Equal("failed", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("GW-CLI-010", Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.DoesNotContain(secret, report.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.NotNull(stopped);
        Assert.Equal(
            ["groundwork.command", "groundwork.exit_code", "groundwork.outcome", "groundwork.provider"],
            stopped.TagObjects.Select(tag => tag.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.DoesNotContain(stopped.TagObjects, tag => tag.Value?.ToString()?.Contains(secret, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Cancellation_has_a_stable_pipeline_exit_and_never_applies_target_state()
    {
        var database = Path.Combine(directory, "cancelled.db");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exit = await GroundworkSchemaCli.RunAsync(
            Arguments("apply", database).Concat(["--safe"]).ToArray(),
            output,
            error,
            cancellation.Token);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.Cancelled, exit);
        Assert.Equal("cancelled", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("GW-CLI-009", Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(File.Exists(database));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Apply_has_no_implicit_connection_or_startup_fallback()
    {
        var database = Path.Combine(directory, "must-not-fallback.db");
        var arguments = Arguments("apply", database).Concat(["--safe"]).ToList();
        var option = arguments.IndexOf("--connection");
        arguments.RemoveRange(option, 2);

        var exit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exit);
        Assert.Equal("invalid", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("GW-CLI-006", Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(File.Exists(database));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Validate_reports_blocking_schema_diagnostics_without_authorizing_an_unsupported_transform()
    {
        var database = Path.Combine(directory, "blocked-validation.db");

        var exit = await GroundworkSchemaCli.RunAsync(
            Arguments("validate", database, typeof(UnsupportedSemanticManifestSource)),
            output,
            error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.ValidationFailed, exit);
        Assert.Equal("blocked", report.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(
            report.RootElement.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "GW-SCHEMA-005");
        Assert.False(File.Exists(database));
    }

    [Theory]
    [InlineData("--help", "Usage: groundwork <plan|validate|status|apply>")]
    [InlineData("--version", "Groundwork.Tool ")]
    public async Task Tool_exposes_installation_smoke_commands(string argument, string expected)
    {
        var exit = await GroundworkSchemaCli.RunAsync([argument], output, error);

        Assert.Equal(SchemaToolExitCodes.Success, exit);
        Assert.StartsWith(expected, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Version_reports_the_exact_package_informational_version()
    {
        var expected = typeof(GroundworkSchemaCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+', 2)[0];

        var exit = await GroundworkSchemaCli.RunAsync(["--version"], output, error);

        Assert.Equal(SchemaToolExitCodes.Success, exit);
        Assert.Equal($"Groundwork.Tool {expected}{Environment.NewLine}", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Json_invocation_errors_are_machine_readable_and_do_not_echo_unparsed_values()
    {
        const string secret = "misplaced-do-not-leak";

        var exit = await GroundworkSchemaCli.RunAsync(
            ["apply", secret, "--output", "json"],
            output,
            error);
        using var report = ParseOutput();

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, exit);
        Assert.Equal("invalid", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("GW-CLI-001", Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
        Assert.DoesNotContain(secret, report.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Human_output_is_stable_and_carries_the_same_target_summary()
    {
        var arguments = Arguments("validate", Path.Combine(directory, "human.db"));
        arguments[Array.IndexOf(arguments, "--output") + 1] = "human";

        var firstExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);
        var first = output.ToString();
        output.GetStringBuilder().Clear();
        var secondExit = await GroundworkSchemaCli.RunAsync(arguments, output, error);

        Assert.Equal(SchemaToolExitCodes.Success, firstExit);
        Assert.Equal(firstExit, secondExit);
        Assert.Equal(first, output.ToString());
        Assert.Contains("Groundwork schema validate: ready", first, StringComparison.Ordinal);
        Assert.Contains("Provider: groundwork-sqlite@1.0.0", first, StringComparison.Ordinal);
        Assert.Contains("Manifest: schema-tool-tests@1", first, StringComparison.Ordinal);
        Assert.Contains("Plan fingerprint: ", first, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private static string[] Arguments(
        string command,
        string database,
        Type? sourceType = null,
        string provider = "sqlite") =>
    [
        command,
        "--manifest-assembly", typeof(TestManifestSource).Assembly.Location,
        "--manifest-type", (sourceType ?? typeof(TestManifestSource)).FullName!,
        "--provider", provider,
        "--connection", $"Data Source={database}",
        "--output", "json"
    ];

    private JsonDocument ParseOutput()
    {
        var document = JsonDocument.Parse(output.ToString());
        output.GetStringBuilder().Clear();
        return document;
    }

    private static async Task<bool> TableExistsAsync(string database, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> IndexExistsAsync(string database, string index)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name;";
        command.Parameters.AddWithValue("@name", index);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    public sealed class TestManifestSource : IPhysicalSchemaManifestSource
    {
        public StorageManifest CreateManifest() => CreateTestManifest();
    }

    public sealed class AuthorizationManifestSource : IPhysicalSchemaManifestSource
    {
        public StorageManifest CreateManifest() => CreateTestManifest(
            new PhysicalEvolutionMetadata(
                IsDestructive: true,
                SemanticMigrationIdentity: "reclassify-v2"));
    }

    public sealed class UnsupportedSemanticManifestSource : IPhysicalSchemaManifestSource
    {
        public StorageManifest CreateManifest() => CreateTestManifest(
            rebuildMode: ProjectionRebuildMode.SemanticMigrationRequired);
    }

    private static StorageManifest CreateTestManifest(
        PhysicalEvolutionMetadata? evolution = null,
        ProjectionRebuildMode rebuildMode = ProjectionRebuildMode.FromCanonicalJson)
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "schema_tool_documents",
            [new ProjectedColumnDefinition(
                    "category",
                    "category",
                    PortablePhysicalType.String,
                    IsNullable: false,
                    RebuildMode: rebuildMode)],
            indexes:
            [
                new PhysicalIndexDefinition(
                        "by-category",
                        [new PhysicalIndexColumnDefinition("storage_scope", 0), new PhysicalIndexColumnDefinition("category", 1)],
                        missingValueBehavior: MissingValueBehavior.Excluded)
            ],
            evolution: evolution);
        var unit = new StorageUnit(
            new StorageUnitIdentity("documents"),
            "document",
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
        return new StorageManifest(
                new StorageManifestIdentity("schema-tool-tests"),
                new StorageManifestOwner("tests"),
                new StorageManifestVersion("1"),
                [unit],
                new HashSet<string>(),
                []);
    }
}
