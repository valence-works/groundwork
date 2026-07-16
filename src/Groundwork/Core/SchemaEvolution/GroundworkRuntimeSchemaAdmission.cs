using System.Diagnostics;
using Groundwork.Core.Validation;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>Controls runtime physical-schema admission.</summary>
public sealed class GroundworkRuntimeSchemaAdmissionOptions
{
    /// <summary>
    /// Applies a pending plan at startup only when the complete plan satisfies Groundwork's safe
    /// apply policy. Disabled by default.
    /// </summary>
    public bool AutoApplyOnStartup { get; set; }
}

public enum GroundworkRuntimeSchemaAdmissionLogLevel
{
    Information,
    Warning
}

public sealed record GroundworkRuntimeSchemaAdmissionLogEntry(
    GroundworkRuntimeSchemaAdmissionLogLevel Level,
    string Message);

/// <summary>The result of inspecting, and optionally safely applying, a runtime schema target.</summary>
public sealed record GroundworkRuntimeSchemaAdmissionResult(
    PhysicalSchemaInspectionResult Inspection,
    PhysicalSchemaDiffPlan Plan,
    PhysicalSchemaApplicationResult? Application = null)
{
    public bool IsReady =>
        Inspection.IsAppliedSchemaValid &&
        ((Plan.IsApplicable && Plan.Operations.Count == 0) ||
         Application?.Outcome is PhysicalSchemaApplicationOutcome.Applied or
             PhysicalSchemaApplicationOutcome.NoChanges);

    public IReadOnlyList<PhysicalSchemaOperation> PendingOperations => IsReady ? [] : Plan.Operations;

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics =>
        Plan.Diagnostics.Concat(Application?.AuthorizationDiagnostics ?? []).ToArray();

    internal string DiagnosticMessage => string.Join("; ", Diagnostics.Select(diagnostic =>
        $"{diagnostic.Code}: {diagnostic.Message}"));

    public int AppliedOperationCount =>
        Application?.Outcome == PhysicalSchemaApplicationOutcome.Applied
            ? GroundworkRuntimeSchemaAdmission.CountExecutableOperations(Application.Plan.Operations)
            : 0;

    public GroundworkRuntimeSchemaAdmissionResult EnsureReady()
    {
        if (!IsReady)
            throw new GroundworkRuntimeSchemaAdmissionException(this);
        return this;
    }
}

public sealed class GroundworkRuntimeSchemaAdmissionException : InvalidOperationException
{
    public GroundworkRuntimeSchemaAdmissionException(GroundworkRuntimeSchemaAdmissionResult result)
        : base(CreateMessage(result)) => Result = result;

    public GroundworkRuntimeSchemaAdmissionResult Result { get; }

    private static string CreateMessage(GroundworkRuntimeSchemaAdmissionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var reason = !result.Inspection.IsAppliedSchemaValid
            ? "found drift in the applied schema"
            : result.Application?.Outcome == PhysicalSchemaApplicationOutcome.AuthorizationRequired
                ? "found pending operations that are not eligible for safe startup auto-apply"
                : "requires the exact target to be applied before startup can continue";
        var diagnostics = result.DiagnosticMessage;
        return $"Groundwork runtime schema admission {reason}." +
               (diagnostics.Length == 0
                   ? string.Empty
                   : $"{Environment.NewLine}{diagnostics}");
    }
}

/// <summary>Provider-neutral runtime schema inspection and opt-in safe application.</summary>
public static class GroundworkRuntimeSchemaAdmission
{
    public static async Task<GroundworkRuntimeSchemaAdmissionResult> InspectRuntimeAdmissionAsync(
        this IPhysicalSchemaExecutor executor,
        PhysicalSchemaTarget target,
        GroundworkRuntimeSchemaAdmissionOptions? options = null,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new GroundworkRuntimeSchemaAdmissionOptions();
        log ??= LogToTrace;
        var inspector = executor as IPhysicalSchemaHistoryInspector
            ?? throw new ArgumentException(
                "Runtime schema admission requires a non-mutating physical-schema inspector.",
                nameof(executor));
        var inspection = await inspector.InspectHistoryAsync(target, cancellationToken);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, inspection.History, DateTimeOffset.UtcNow);
        if (options.AutoApplyOnStartup &&
            inspection.IsAppliedSchemaValid &&
            plan.IsApplicable &&
            plan.Operations.Count != 0)
        {
            var application = await PhysicalSchemaApplication.ApplyAsync(
                target,
                executor,
                planAuthorization: currentPlan => AuthorizeSafePlan(currentPlan, target.Identity, log),
                cancellationToken: cancellationToken);
            var finalInspection = application.Outcome is PhysicalSchemaApplicationOutcome.Applied or
                PhysicalSchemaApplicationOutcome.NoChanges && application.AppliedState is not null
                    ? new PhysicalSchemaInspectionResult(
                        PhysicalSchemaHistoryState.FromApplied(application.AppliedState),
                        IsAppliedSchemaValid: true)
                    : inspection;
            var result = new GroundworkRuntimeSchemaAdmissionResult(
                finalInspection,
                application.Plan,
                application);
            if (result.IsReady)
            {
                log?.Invoke(new GroundworkRuntimeSchemaAdmissionLogEntry(
                    GroundworkRuntimeSchemaAdmissionLogLevel.Information,
                    $"Groundwork runtime schema auto-apply completed for {target.Identity}; " +
                    $"{result.AppliedOperationCount} operations were applied."));
            }
            else
            {
                log?.Invoke(new GroundworkRuntimeSchemaAdmissionLogEntry(
                    GroundworkRuntimeSchemaAdmissionLogLevel.Warning,
                    $"Groundwork runtime schema auto-apply was blocked for {target.Identity}: " +
                    result.DiagnosticMessage));
            }
            return result;
        }
        return new GroundworkRuntimeSchemaAdmissionResult(inspection, plan);
    }

    private static PhysicalSchemaPlanAuthorization AuthorizeSafePlan(
        PhysicalSchemaDiffPlan plan,
        PhysicalSchemaTargetIdentity targetIdentity,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? log)
    {
        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        if (protection.IsSafe)
        {
            log?.Invoke(new GroundworkRuntimeSchemaAdmissionLogEntry(
                GroundworkRuntimeSchemaAdmissionLogLevel.Information,
                $"Groundwork runtime schema auto-apply is executing " +
                $"{CountExecutableOperations(plan.Operations)} " +
                $"pending operations for {targetIdentity}."));
            return PhysicalSchemaPlanAuthorization.Allow;
        }

        var protectedWork = protection.DestructiveOperationIdentities
            .Select(identity => $"destructive operation '{identity}'")
            .Concat(protection.SemanticMigrationIdentities.Select(identity =>
                $"semantic migration '{identity}'"));
        return PhysicalSchemaPlanAuthorization.Deny(
        [
            GroundworkDiagnostic.Error(
                "GW-RUNTIME-002",
                $"Startup auto-apply is safe-only and requires explicit operator approval for {string.Join(", ", protectedWork)}.",
                "runtime-schema-admission")
        ]);
    }

    internal static int CountExecutableOperations(IReadOnlyList<PhysicalSchemaOperation> operations) =>
        operations.Count(operation => operation is not RecordPhysicalSchemaAppliedStateOperation);

    private static void LogToTrace(GroundworkRuntimeSchemaAdmissionLogEntry entry)
    {
        if (entry.Level == GroundworkRuntimeSchemaAdmissionLogLevel.Information)
            Trace.TraceInformation("{0}", entry.Message);
        else
            Trace.TraceWarning("{0}", entry.Message);
    }
}
