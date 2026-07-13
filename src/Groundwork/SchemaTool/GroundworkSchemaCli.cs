using System.Diagnostics;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.SchemaTool;

public static class GroundworkSchemaCli
{
    private static readonly ActivitySource ActivitySource = new("Groundwork.SchemaTool", "1.0.0");

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (arguments.Count == 1 && arguments[0] is "--help" or "-h")
        {
            await output.WriteAsync(HelpText);
            return SchemaToolExitCodes.Success;
        }
        if (arguments.Count == 1 && arguments[0] == "--version")
        {
            var version = typeof(GroundworkSchemaCli).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            await output.WriteLineAsync($"Groundwork.Tool {version}");
            return SchemaToolExitCodes.Success;
        }
        if (!SchemaToolOptions.TryParse(arguments, out var options, out var parseDiagnostic))
        {
            if (RequestsJson(arguments))
            {
                var command = arguments.Count > 0 &&
                              Enum.TryParse<SchemaToolCommand>(arguments[0], ignoreCase: true, out var parsedCommand)
                    ? parsedCommand.ToString().ToLowerInvariant()
                    : "unknown";
                var report = SchemaToolReport.Error(
                    command,
                    "invalid",
                    "GW-CLI-001",
                    "Command options are invalid. Run '--help' for the supported syntax.");
                await SchemaToolReportWriter.WriteAsync(report, SchemaToolOutput.Json, output);
            }
            else
            {
                await error.WriteLineAsync($"GW-CLI-001: {parseDiagnostic}");
            }
            return SchemaToolExitCodes.InvalidInvocation;
        }

        var parsedOptions = options!;
        using var activity = ActivitySource.StartActivity("groundwork.schema");
        activity?.SetTag("groundwork.command", parsedOptions.Command.ToString().ToLowerInvariant());
        activity?.SetTag(
            "groundwork.provider",
            ProviderDescriptor.Find(parsedOptions.Provider)?.Alias ?? "unknown");

        int Finish(int exitCode, string outcome)
        {
            activity?.SetTag("groundwork.outcome", outcome);
            activity?.SetTag("groundwork.exit_code", exitCode);
            return exitCode;
        }

        try
        {
            var source = ManifestSourceLoader.Load(parsedOptions.ManifestAssembly, parsedOptions.ManifestType);
            var manifest = source.CreateManifest()
                ?? throw new SchemaToolConfigurationException("GW-CLI-004", "Manifest source returned null.");
            var namePolicy = source.CreateNamePolicy()
                ?? throw new SchemaToolConfigurationException("GW-CLI-004", "Manifest source returned a null naming policy.");
            var compilation = SchemaToolTargetCompiler.Compile(manifest, namePolicy, parsedOptions.Provider);
            if (parsedOptions.Command == SchemaToolCommand.Validate)
            {
                var validation = SchemaToolReport.Validate(compilation.Target, compilation.Diagnostics);
                await SchemaToolReportWriter.WriteAsync(validation, parsedOptions.Output, output);
                return validation.Diagnostics.Any(item => item.IsError)
                    ? Finish(SchemaToolExitCodes.ValidationFailed, "blocked")
                    : Finish(SchemaToolExitCodes.Success, "ready");
            }

            if (!compilation.IsValid)
            {
                var invalid = SchemaToolReport.Validate(compilation.Target, compilation.Diagnostics) with
                {
                    Command = parsedOptions.Command.ToString().ToLowerInvariant()
                };
                await SchemaToolReportWriter.WriteAsync(invalid, parsedOptions.Output, output);
                return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
            }

            var connectionString = ResolveConnection(parsedOptions);
            await using var provider = SchemaToolProviderSession.Create(
                parsedOptions.Provider,
                connectionString,
                parsedOptions.Database);
            var (history, plan) = await ReadPlanAsync(
                compilation.Target!,
                provider.Executor,
                cancellationToken);
            if (parsedOptions.Command == SchemaToolCommand.Apply)
            {
                if (!plan.IsApplicable)
                {
                    var blocked = SchemaToolReport.FromPlan("apply", compilation.Target!, history, plan);
                    await SchemaToolReportWriter.WriteAsync(blocked, parsedOptions.Output, output);
                    return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
                }

                var missingAuthorization = SchemaToolAuthorization.FindMissing(plan.Operations, parsedOptions);
                if (missingAuthorization.Count != 0)
                {
                    var unauthorized = SchemaToolAuthorization.Report(
                        compilation.Target!,
                        history,
                        plan,
                        missingAuthorization);
                    await SchemaToolReportWriter.WriteAsync(unauthorized, parsedOptions.Output, output);
                    return Finish(SchemaToolExitCodes.AuthorizationRequired, "authorization-required");
                }

                var application = await PhysicalSchemaApplication.ApplyAsync(
                    compilation.Target!,
                    provider.Executor,
                    cancellationToken: cancellationToken);
                var applied = SchemaToolReport.FromApplication(application);
                await SchemaToolReportWriter.WriteAsync(applied, parsedOptions.Output, output);
                return application.Outcome == PhysicalSchemaApplicationOutcome.Rejected
                    ? Finish(SchemaToolExitCodes.ValidationFailed, "blocked")
                    : Finish(SchemaToolExitCodes.Success, applied.Outcome);
            }

            var report = SchemaToolReport.FromPlan(
                parsedOptions.Command.ToString().ToLowerInvariant(),
                compilation.Target!,
                history,
                plan);
            await SchemaToolReportWriter.WriteAsync(report, parsedOptions.Output, output);
            if (!plan.IsApplicable)
                return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
            return plan.Operations.Count == 0
                ? Finish(SchemaToolExitCodes.Success, "ready")
                : Finish(SchemaToolExitCodes.PendingChanges, "pending");
        }
        catch (SchemaToolConfigurationException exception)
        {
            var report = SchemaToolReport.Error(
                parsedOptions.Command.ToString().ToLowerInvariant(),
                "invalid",
                exception.Code,
                exception.Message);
            await SchemaToolReportWriter.WriteAsync(report, parsedOptions.Output, output);
            return Finish(SchemaToolExitCodes.InvalidInvocation, "invalid");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var report = SchemaToolReport.Error(
                parsedOptions.Command.ToString().ToLowerInvariant(),
                "cancelled",
                "GW-CLI-009",
                "Schema tool execution was cancelled; unapplied target state was not recorded.");
            await SchemaToolReportWriter.WriteAsync(report, parsedOptions.Output, output);
            return Finish(SchemaToolExitCodes.Cancelled, "cancelled");
        }
        catch (Exception)
        {
            const string message = "Schema tool execution failed. Exception details were suppressed to protect secrets.";
            var report = SchemaToolReport.Error(
                parsedOptions.Command.ToString().ToLowerInvariant(),
                "failed",
                "GW-CLI-010",
                message);
            await SchemaToolReportWriter.WriteAsync(report, parsedOptions.Output, output);
            return Finish(SchemaToolExitCodes.ExecutionFailed, "failed");
        }
    }

    private static bool RequestsJson(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] == "--output" &&
                string.Equals(arguments[index + 1], "json", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private const string HelpText = """
        Usage: groundwork <plan|validate|status|apply> [options]

        Required options:
          --manifest-assembly <path>  Assembly containing IPhysicalSchemaManifestSource
          --provider <provider>       mongodb | postgresql | sqlite | sqlserver

        Live command options:
          --connection <value>        Explicit provider connection input
          --connection-env <name>     Read connection input from an environment variable
          --database <name>           Required for MongoDB when absent from its URI

        Output and authorization:
          --output <human|json>       Human output is the default
          --authorize-destructive     Approve declared destructive work
          --authorize-semantic <id>   Approve one declared semantic migration identity

        Source selection:
          --manifest-type <name>      Required when the assembly contains multiple sources

        """;

    private static string ResolveConnection(SchemaToolOptions options)
    {
        if (options.Connection is not null)
            return options.Connection;
        if (options.ConnectionEnvironmentVariable is not null)
        {
            var value = Environment.GetEnvironmentVariable(options.ConnectionEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            throw new SchemaToolConfigurationException(
                "GW-CLI-006",
                "The configured connection environment variable is missing or empty.");
        }
        throw new SchemaToolConfigurationException(
            "GW-CLI-006",
            "This command requires explicit '--connection' or '--connection-env' input.");
    }

    private static async Task<(PhysicalSchemaHistoryState History, PhysicalSchemaDiffPlan Plan)> ReadPlanAsync(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        CancellationToken cancellationToken)
    {
        await using var applicationLock = await executor.AcquireApplicationLockAsync(target.Identity, cancellationToken);
        if (applicationLock.Target != target.Identity)
            throw new InvalidOperationException("Provider returned a physical schema lock for another target.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            applicationLock.OwnershipLost);
        var token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();
        var history = await executor.ReadHistoryAsync(target.Identity, applicationLock, token);
        token.ThrowIfCancellationRequested();
        return (
            history,
            PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UnixEpoch));
    }
}

public static class SchemaToolExitCodes
{
    public const int Success = 0;
    public const int PendingChanges = 2;
    public const int ValidationFailed = 3;
    public const int AuthorizationRequired = 4;
    public const int InvalidInvocation = 5;
    public const int ExecutionFailed = 10;
    public const int Cancelled = 130;
}
