using System.Diagnostics;
using System.Reflection;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;
using Groundwork.DiagnosticRecords;

namespace Groundwork.SchemaTool;

public static class GroundworkSchemaCli
{
    private static readonly ActivitySource ActivitySource = new("Groundwork.SchemaTool", "1.0.0");

    public static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(
            arguments,
            output,
            error,
            DiagnosticRecordDeploymentCoordinator.ApplyAsync,
            cancellationToken);

    internal static Task<int> RunWithDiagnosticApplyAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<string, string, string?, DiagnosticRecordDeploymentManifest, CancellationToken, Task> diagnosticApplyAsync,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(arguments, output, error, diagnosticApplyAsync, cancellationToken);

    private static async Task<int> RunCoreAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<string, string, string?, DiagnosticRecordDeploymentManifest, CancellationToken, Task> diagnosticApplyAsync,
        CancellationToken cancellationToken)
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
            var version = typeof(GroundworkSchemaCli).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+', 2)[0]
                ?? "unknown";
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
            var diagnosticSource = source as IDiagnosticRecordDeploymentManifestSource;
            var deployment = diagnosticSource?.CreateDeploymentManifest()
                ?? new DiagnosticRecordDeploymentManifest(manifest);
            if (!manifest.HasSameDefinitionAs(deployment.Storage))
            {
                throw new SchemaToolConfigurationException(
                    "GW-CLI-013",
                    "The diagnostic deployment source must return the same document manifest from both source contracts.");
            }
            var namePolicy = source.CreateNamePolicy()
                ?? throw new SchemaToolConfigurationException("GW-CLI-004", "Manifest source returned a null naming policy.");
            var compilation = SchemaToolTargetCompiler.Compile(deployment.Storage, namePolicy, parsedOptions.Provider);
            if (!compilation.IsValid)
            {
                var invalid = SchemaToolReport.Validate(
                    compilation.Target,
                    compilation.Diagnostics,
                    PhysicalSchemaHistoryState.Empty,
                    parsedOptions.Command == SchemaToolCommand.Validate
                        ? parsedOptions.Offline ? "offline" : "live"
                        : "offline") with
                {
                    Command = parsedOptions.Command.ToString().ToLowerInvariant(),
                    InspectionMode = parsedOptions.Command == SchemaToolCommand.Validate
                        ? parsedOptions.Offline ? "offline" : "live"
                        : null
                };
                await SchemaToolReportWriter.WriteAsync(invalid, parsedOptions.Output, output);
                return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
            }

            if (parsedOptions.Command == SchemaToolCommand.Validate && parsedOptions.Offline)
            {
                var offline = SchemaToolReport.Validate(
                    compilation.Target,
                    compilation.Diagnostics,
                    PhysicalSchemaHistoryState.Empty,
                    "offline");
                if (diagnosticSource is not null)
                {
                    var admission = await DiagnosticRecordDeploymentCoordinator.AdmitAsync(
                        parsedOptions.Provider,
                        connectionString: null,
                        parsedOptions.Database,
                        deployment,
                        includeTopology: false,
                        cancellationToken);
                    offline = offline.WithDiagnosticRecords(
                        admission,
                        inspectionWasLive: false);
                }
                await SchemaToolReportWriter.WriteAsync(offline, parsedOptions.Output, output);
                return offline.Diagnostics.Any(item => item.IsError)
                    ? Finish(SchemaToolExitCodes.ValidationFailed, "blocked")
                    : Finish(SchemaToolExitCodes.Success, "ready");
            }

            var connectionString = ResolveConnection(parsedOptions);
            if (diagnosticSource is not null)
            {
                var admission = await DiagnosticRecordDeploymentCoordinator.AdmitAsync(
                    parsedOptions.Provider,
                    connectionString,
                    parsedOptions.Database,
                    deployment,
                    includeTopology: true,
                    cancellationToken);
                if (admission.Diagnostics.Any(item => item.IsError))
                {
                    var blocked = SchemaToolReport.Validate(
                        compilation.Target,
                        [],
                        PhysicalSchemaHistoryState.Empty,
                        "live") with
                    {
                        Command = parsedOptions.Command.ToString().ToLowerInvariant(),
                        InspectionMode = parsedOptions.Command == SchemaToolCommand.Validate ? "live" : null
                    };
                    blocked = blocked.WithDiagnosticRecords(admission, inspectionWasLive: true);
                    await SchemaToolReportWriter.WriteAsync(blocked, parsedOptions.Output, output);
                    return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
                }
            }
            await using var provider = SchemaToolProviderSession.Create(
                parsedOptions.Provider,
                connectionString,
                parsedOptions.Database);
            if (parsedOptions.Command == SchemaToolCommand.Validate)
            {
                PhysicalSchemaInspectionResult inspection;
                try
                {
                    inspection = await provider.Inspector.InspectHistoryAsync(
                        compilation.Target!,
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    var drift = SchemaToolReport.Validate(
                        compilation.Target,
                        compilation.Diagnostics.Concat(
                        [
                            AppliedSchemaDriftDiagnostic()
                        ]).ToArray(),
                        PhysicalSchemaHistoryState.Empty,
                        "live");
                    await SchemaToolReportWriter.WriteAsync(drift, parsedOptions.Output, output);
                    return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
                }
                var inspectionDiagnostics = inspection.IsAppliedSchemaValid
                    ? compilation.Diagnostics
                    : compilation.Diagnostics.Concat(
                    [
                        AppliedSchemaDriftDiagnostic()
                    ]).ToArray();
                var validation = SchemaToolReport.Validate(
                    compilation.Target,
                    inspectionDiagnostics,
                    inspection.History,
                    "live");
                if (diagnosticSource is not null)
                {
                    var diagnostics = await DiagnosticRecordDeploymentCoordinator.InspectAsync(
                        parsedOptions.Provider,
                        connectionString,
                        parsedOptions.Database,
                        deployment,
                        cancellationToken);
                    validation = validation.WithDiagnosticRecords(diagnostics, inspectionWasLive: true);
                }
                await SchemaToolReportWriter.WriteAsync(validation, parsedOptions.Output, output);
                return validation.Diagnostics.Any(item => item.IsError) || validation.Outcome != "ready"
                    ? Finish(SchemaToolExitCodes.ValidationFailed, "blocked")
                    : Finish(SchemaToolExitCodes.Success, "ready");
            }

            if (parsedOptions.Command == SchemaToolCommand.Apply)
            {
                DiagnosticRecordDeploymentStatus? diagnosticPreflight = null;
                if (diagnosticSource is not null)
                {
                    diagnosticPreflight = await DiagnosticRecordDeploymentCoordinator.InspectAsync(
                        parsedOptions.Provider,
                        connectionString,
                        parsedOptions.Database,
                        deployment,
                        cancellationToken);
                    if (diagnosticPreflight.Diagnostics.Any(item => item.IsError))
                    {
                        var blocked = SchemaToolReport.Validate(
                            compilation.Target,
                            [],
                            PhysicalSchemaHistoryState.Empty,
                            "live") with
                        {
                            Command = "apply",
                            InspectionMode = null
                        };
                        blocked = blocked.WithDiagnosticRecords(diagnosticPreflight, inspectionWasLive: true);
                        await SchemaToolReportWriter.WriteAsync(blocked, parsedOptions.Output, output);
                        return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
                    }
                }
                DiagnosticRecordDeploymentStatus? lockedDiagnosticStatus = null;
                var application = await PhysicalSchemaApplication.ApplyAsync(
                    compilation.Target!,
                    provider.Executor,
                    cancellationToken: cancellationToken,
                    planAuthorizationAsync: async (plan, applicationToken) =>
                    {
                        DiagnosticRecordDeploymentStatus? lockedDiagnostics = null;
                        if (diagnosticSource is not null)
                        {
                            var admission = await DiagnosticRecordDeploymentCoordinator.AdmitAsync(
                                parsedOptions.Provider,
                                connectionString,
                                parsedOptions.Database,
                                deployment,
                                includeTopology: true,
                                applicationToken);
                            if (admission.Diagnostics.Any(item => item.IsError))
                                return PhysicalSchemaPlanAuthorization.Deny(admission.Diagnostics);
                            lockedDiagnostics = await DiagnosticRecordDeploymentCoordinator.InspectAsync(
                                parsedOptions.Provider,
                                connectionString,
                                parsedOptions.Database,
                                deployment,
                                applicationToken);
                            lockedDiagnosticStatus = lockedDiagnostics;
                            if (lockedDiagnostics.Diagnostics.Any(item => item.IsError))
                                return PhysicalSchemaPlanAuthorization.Deny(lockedDiagnostics.Diagnostics);
                        }
                        return SchemaToolAuthorization.Evaluate(
                            compilation.Target!,
                            plan,
                            parsedOptions,
                            lockedDiagnostics);
                    });
                var applied = SchemaToolReport.FromApplication(application);
                var diagnosticBaseline = lockedDiagnosticStatus ?? diagnosticPreflight;
                if (diagnosticSource is not null &&
                    (application.Outcome is PhysicalSchemaApplicationOutcome.Rejected or
                        PhysicalSchemaApplicationOutcome.AuthorizationRequired))
                {
                    applied = applied.WithDiagnosticRecords(
                        diagnosticBaseline!,
                        inspectionWasLive: true);
                }
                if (application.Outcome is not PhysicalSchemaApplicationOutcome.Rejected and
                    not PhysicalSchemaApplicationOutcome.AuthorizationRequired && diagnosticSource is not null)
                {
                    if (!diagnosticBaseline!.IsApplied)
                    {
                        try
                        {
                            await diagnosticApplyAsync(
                                parsedOptions.Provider,
                                connectionString,
                                parsedOptions.Database,
                                deployment,
                                cancellationToken);
                        }
                        catch (Exception)
                        {
                            DiagnosticRecordDeploymentStatus incomplete;
                            try
                            {
                                incomplete = await DiagnosticRecordDeploymentCoordinator.InspectAsync(
                                    parsedOptions.Provider,
                                    connectionString,
                                    parsedOptions.Database,
                                    deployment,
                                    cancellationToken);
                            }
                            catch
                            {
                                incomplete = diagnosticBaseline;
                            }
                            applied = applied.WithIncompleteDiagnosticDeployment(
                                incomplete,
                                diagnosticBaseline);
                            await SchemaToolReportWriter.WriteAsync(applied, parsedOptions.Output, output);
                            return cancellationToken.IsCancellationRequested
                                ? Finish(SchemaToolExitCodes.Cancelled, "incomplete")
                                : Finish(SchemaToolExitCodes.ExecutionFailed, "incomplete");
                        }
                    }
                    var diagnostics = await DiagnosticRecordDeploymentCoordinator.InspectAsync(
                        parsedOptions.Provider,
                        connectionString,
                        parsedOptions.Database,
                        deployment,
                        cancellationToken);
                    applied = applied.WithDiagnosticRecords(
                        diagnostics,
                        inspectionWasLive: true,
                        diagnosticRecordsMutated: !diagnosticBaseline.IsApplied,
                        planStatus: diagnosticBaseline);
                }
                await SchemaToolReportWriter.WriteAsync(applied, parsedOptions.Output, output);
                return application.Outcome switch
                {
                    PhysicalSchemaApplicationOutcome.Rejected =>
                        Finish(SchemaToolExitCodes.ValidationFailed, "blocked"),
                    PhysicalSchemaApplicationOutcome.AuthorizationRequired =>
                        Finish(SchemaToolExitCodes.AuthorizationRequired, "authorization-required"),
                    _ when applied.Diagnostics.Any(item => item.IsError) || applied.Outcome == "blocked" =>
                        Finish(SchemaToolExitCodes.ValidationFailed, "blocked"),
                    _ => Finish(SchemaToolExitCodes.Success, applied.Outcome)
                };
            }

            var (planInspection, plan) = await ReadPlanAsync(
                compilation.Target!,
                provider.Inspector,
                cancellationToken);

            var report = SchemaToolReport.FromPlan(
                parsedOptions.Command.ToString().ToLowerInvariant(),
                compilation.Target!,
                planInspection.History,
                plan,
                planInspection.IsAppliedSchemaValid ? [] : [AppliedSchemaDriftDiagnostic()]);
            if (diagnosticSource is not null)
            {
                var diagnostics = await DiagnosticRecordDeploymentCoordinator.InspectAsync(
                    parsedOptions.Provider,
                    connectionString,
                    parsedOptions.Database,
                    deployment,
                    cancellationToken);
                report = report.WithDiagnosticRecords(diagnostics, inspectionWasLive: true);
            }
            await SchemaToolReportWriter.WriteAsync(report, parsedOptions.Output, output);
            if (report.Diagnostics.Any(item => item.IsError))
                return Finish(SchemaToolExitCodes.ValidationFailed, "blocked");
            return report.Outcome == "ready"
                ? Finish(SchemaToolExitCodes.Success, "ready")
                : Finish(SchemaToolExitCodes.PendingChanges, "pending");
        }
        catch (DiagnosticRecordDeploymentManifestException)
        {
            var report = SchemaToolReport.Error(
                parsedOptions.Command.ToString().ToLowerInvariant(),
                "invalid",
                "GW-CLI-013",
                "Diagnostic-record deployment declarations are invalid.");
            await SchemaToolReportWriter.WriteAsync(report, parsedOptions.Output, output);
            return Finish(SchemaToolExitCodes.InvalidInvocation, "invalid");
        }
        catch (DiagnosticRecordValidationException)
        {
            var report = SchemaToolReport.Error(
                parsedOptions.Command.ToString().ToLowerInvariant(),
                "invalid",
                "GW-CLI-013",
                "Diagnostic-record deployment declarations are invalid.");
            await SchemaToolReportWriter.WriteAsync(report, parsedOptions.Output, output);
            return Finish(SchemaToolExitCodes.InvalidInvocation, "invalid");
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
          --offline                   Validate manifest/routes without provider inspection
          --safe                      Apply only additive/approved-backfill work
          --expected-plan <hash>      Bind authorized apply to one exact locked plan
          --allow-destructive <op>    Approve one exact destructive operation identity
          --allow-semantic <id>       Approve one semantic migration identity

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

    private static GroundworkDiagnostic AppliedSchemaDriftDiagnostic() => GroundworkDiagnostic.Error(
        "GW-CLI-012",
        "Live provider state is incompatible with the recorded physical-schema target.",
        "providerState");

    private static async Task<(PhysicalSchemaInspectionResult Inspection, PhysicalSchemaDiffPlan Plan)> ReadPlanAsync(
        PhysicalSchemaTarget target,
        IPhysicalSchemaHistoryInspector inspector,
        CancellationToken cancellationToken)
    {
        var inspection = await inspector.InspectHistoryAsync(target, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (
            inspection,
            PhysicalSchemaDiffPlanner.Plan(target, inspection.History, DateTimeOffset.UnixEpoch));
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
