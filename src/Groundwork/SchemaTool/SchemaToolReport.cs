using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Validation;

namespace Groundwork.SchemaTool;

internal sealed record SchemaToolReport(
    string Command,
    string Outcome,
    string? InspectionMode,
    PhysicalSchemaTarget? Target,
    string? PlanFingerprint,
    string? AppliedTargetFingerprint,
    IReadOnlyList<SchemaToolOperation> PendingOperations,
    IReadOnlyList<SchemaToolOperation> AppliedOperations,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics,
    bool Mutated,
    DiagnosticRecordDeploymentStatus? DiagnosticRecords = null)
{
    public SchemaToolReport WithDiagnosticRecords(
        DiagnosticRecordDeploymentStatus status,
        bool inspectionWasLive,
        bool diagnosticRecordsMutated = false,
        DiagnosticRecordDeploymentStatus? planStatus = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        var diagnostics = Diagnostics.Concat(status.Diagnostics).ToArray();
        var outcome = Outcome;
        if (diagnostics.Any(item => item.IsError))
            outcome = "blocked";
        else if (inspectionWasLive && !status.IsApplied)
        {
            outcome = Command switch
            {
                "validate" => "blocked",
                "apply" => "blocked",
                _ => "pending"
            };
        }
        else if (Command == "apply" && diagnosticRecordsMutated && outcome == "ready")
        {
            outcome = "applied";
        }

        return this with
        {
            Outcome = outcome,
            PlanFingerprint = CombinePlanFingerprint(PlanFingerprint, planStatus ?? status),
            PendingOperations = PendingOperations.Concat(
                status.PendingOperations.Select(SchemaToolOperation.FromDiagnostic)).ToArray(),
            AppliedOperations = AppliedOperations.Concat(
                status.AppliedOperations.Select(SchemaToolOperation.FromDiagnostic)).ToArray(),
            Diagnostics = diagnostics,
            Mutated = Mutated || diagnosticRecordsMutated,
            DiagnosticRecords = status
        };
    }

    public SchemaToolReport WithIncompleteDiagnosticDeployment(
        DiagnosticRecordDeploymentStatus status,
        DiagnosticRecordDeploymentStatus planStatus)
    {
        ArgumentNullException.ThrowIfNull(status);
        var updated = WithDiagnosticRecords(
            status,
            inspectionWasLive: true,
            planStatus: planStatus);
        return updated with
        {
            Outcome = "incomplete",
            Mutated = true,
            Diagnostics = updated.Diagnostics.Append(
                GroundworkDiagnostic.Error(
                    "GW-DIAG-DEPLOY-004",
                    "Document schema was applied, but diagnostic-record materialization did not complete; rerun apply to converge.",
                    "diagnosticRecords")).ToArray()
        };
    }

    public static SchemaToolReport Validate(
        PhysicalSchemaTarget? target,
        IReadOnlyList<GroundworkDiagnostic> diagnostics,
        PhysicalSchemaHistoryState history,
        string inspectionMode)
    {
        var plan = target is null
            ? null
            : PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UnixEpoch);
        var combined = diagnostics.Concat(plan?.Diagnostics ?? []).ToArray();
        return new SchemaToolReport(
            "validate",
            combined.Any(item => item.IsError) ? "blocked" : "ready",
            inspectionMode,
            target,
            plan is null ? null : Fingerprint(target!, history.AppliedState?.TargetFingerprint, plan.Operations),
            history.AppliedState?.TargetFingerprint,
            plan?.Operations.Select(SchemaToolOperation.FromPhysical).ToArray() ?? [],
            history.AppliedState?.AppliedOperations.Select(SchemaToolOperation.FromApplied).ToArray() ?? [],
            combined,
            Mutated: false);
    }

    public static SchemaToolReport FromPlan(
        string command,
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history,
        PhysicalSchemaDiffPlan plan,
        IReadOnlyList<GroundworkDiagnostic> inspectionDiagnostics)
    {
        var diagnostics = inspectionDiagnostics.Concat(plan.Diagnostics).ToArray();
        var outcome = diagnostics.Any(item => item.IsError)
            ? "blocked"
            : plan.Operations.Count == 0
                ? "ready"
                : "pending";
        return new SchemaToolReport(
            command,
            outcome,
            null,
            target,
            Fingerprint(target, history.AppliedState?.TargetFingerprint, plan.Operations),
            history.AppliedState?.TargetFingerprint,
            plan.Operations.Select(SchemaToolOperation.FromPhysical).ToArray(),
            history.AppliedState?.AppliedOperations.Select(SchemaToolOperation.FromApplied).ToArray() ?? [],
            diagnostics,
            Mutated: false);
    }

    public static SchemaToolReport FromApplication(PhysicalSchemaApplicationResult result)
    {
        var outcome = result.Outcome switch
        {
            PhysicalSchemaApplicationOutcome.Applied => "applied",
            PhysicalSchemaApplicationOutcome.NoChanges => "ready",
            PhysicalSchemaApplicationOutcome.Rejected => "blocked",
            PhysicalSchemaApplicationOutcome.AuthorizationRequired => "authorization-required",
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        var authorizationRequired = result.Outcome == PhysicalSchemaApplicationOutcome.AuthorizationRequired;
        return new SchemaToolReport(
            "apply",
            outcome,
            null,
            result.Plan.Target,
            Fingerprint(
                result.Plan.Target,
                result.Plan.ExpectedAppliedTargetFingerprint,
                result.Plan.Operations),
            result.AppliedState?.TargetFingerprint,
            authorizationRequired
                ? result.Plan.Operations.Select(SchemaToolOperation.FromPhysical).ToArray()
                : [],
            result.AppliedState?.AppliedOperations.Select(SchemaToolOperation.FromApplied).ToArray() ?? [],
            result.Plan.Diagnostics.Concat(result.AuthorizationDiagnostics).ToArray(),
            Mutated: result.Outcome == PhysicalSchemaApplicationOutcome.Applied);
    }

    public static SchemaToolReport Error(
        string command,
        string outcome,
        string code,
        string message) =>
        new(
            command,
            outcome,
            InspectionMode: null,
            Target: null,
            PlanFingerprint: null,
            AppliedTargetFingerprint: null,
            PendingOperations: [],
            AppliedOperations: [],
            Diagnostics: [GroundworkDiagnostic.Error(code, message, "command")],
            Mutated: false);

    public static string Fingerprint(
        PhysicalSchemaTarget target,
        string? appliedTargetFingerprint,
        IReadOnlyList<PhysicalSchemaOperation> operations)
    {
        var parts = new[] { target.Fingerprint, appliedTargetFingerprint ?? string.Empty }
            .Concat(operations.SelectMany(operation => new[] { operation.Identity, operation.Fingerprint }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))
            .ToLowerInvariant();
    }

    public static string? CombinePlanFingerprint(
        string? documentFingerprint,
        DiagnosticRecordDeploymentStatus status)
    {
        if (documentFingerprint is null)
            return null;
        var parts = new[]
            {
                "groundwork-combined-deployment-v2",
                documentFingerprint,
                status.Fingerprint
            }
            .Concat(status.PendingOperations.SelectMany(operation =>
                new[] { operation.Identity, operation.Fingerprint }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))
            .ToLowerInvariant();
    }
}

internal sealed record SchemaToolOperation(
    string Identity,
    string Fingerprint,
    string Kind,
    string? StorageUnit,
    string SubjectIdentity,
    bool IsDestructive,
    string? SemanticMigrationIdentity)
{
    public static SchemaToolOperation FromPhysical(PhysicalSchemaOperation operation)
    {
        var protection = PhysicalSchemaPlanProtection.Inspect([operation]);
        return new(
            operation.Identity,
            operation.Fingerprint,
            operation.Kind.ToString(),
            operation.StorageUnit?.Value,
            operation.SubjectIdentity,
            protection.DestructiveOperationIdentities.Count != 0,
            protection.SemanticMigrationIdentities.SingleOrDefault());
    }

    public static SchemaToolOperation FromApplied(PhysicalSchemaAppliedOperation operation) =>
        new(
            operation.Identity,
            operation.Fingerprint,
            operation.Kind.ToString(),
            operation.StorageUnit?.Value,
            operation.SubjectIdentity,
            false,
            null);

    public static SchemaToolOperation FromDiagnostic(DiagnosticRecordDeploymentOperation operation) =>
        new(
            operation.Identity,
            operation.Fingerprint,
            operation.Kind,
            null,
            operation.SubjectIdentity,
            false,
            null);
}

internal static class SchemaToolReportWriter
{
    public static async Task WriteAsync(
        SchemaToolReport report,
        SchemaToolOutput output,
        TextWriter writer)
    {
        if (output == SchemaToolOutput.Human)
        {
            await WriteHumanAsync(report, writer);
            return;
        }

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteJson(json, report);
        await writer.WriteLineAsync(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static async Task WriteHumanAsync(SchemaToolReport report, TextWriter writer)
    {
        await writer.WriteLineAsync($"Groundwork schema {report.Command}: {report.Outcome}");
        if (report.InspectionMode is not null)
            await writer.WriteLineAsync($"Inspection mode: {report.InspectionMode}");
        if (report.Target is not null)
        {
            await writer.WriteLineAsync($"Provider: {report.Target.Provider.Name}@{report.Target.Provider.Version}");
            await writer.WriteLineAsync($"Manifest: {report.Target.ManifestIdentity.Value}@{report.Target.ManifestVersion.Value}");
            await writer.WriteLineAsync($"Target fingerprint: {report.Target.Fingerprint}");
            await writer.WriteLineAsync($"Plan fingerprint: {report.PlanFingerprint}");
        }
        await writer.WriteLineAsync($"Pending operations: {report.PendingOperations.Count}");
        await writer.WriteLineAsync($"Applied operations: {report.AppliedOperations.Count}");
        if (report.DiagnosticRecords is not null)
        {
            await writer.WriteLineAsync($"Diagnostic streams: {report.DiagnosticRecords.DeclaredStreams.Count}");
            await writer.WriteLineAsync($"Pending diagnostic streams: {report.DiagnosticRecords.PendingStreams.Count}");
            await writer.WriteLineAsync($"Diagnostic deployment fingerprint: {report.DiagnosticRecords.Fingerprint}");
        }
        foreach (var diagnostic in report.Diagnostics)
            await writer.WriteLineAsync($"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message} ({diagnostic.Target ?? "target"})");
    }

    private static void WriteJson(Utf8JsonWriter writer, SchemaToolReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", "1");
        writer.WriteString("command", report.Command);
        writer.WriteString("outcome", report.Outcome);
        WriteNullable(writer, "inspectionMode", report.InspectionMode);
        writer.WritePropertyName("provider");
        writer.WriteStartObject();
        if (report.Target is null)
        {
            writer.WriteNull("name");
            writer.WriteNull("version");
        }
        else
        {
            writer.WriteString("name", report.Target.Provider.Name);
            writer.WriteString("version", report.Target.Provider.Version);
        }
        writer.WriteEndObject();
        writer.WritePropertyName("target");
        if (report.Target is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("manifestIdentity", report.Target.ManifestIdentity.Value);
            writer.WriteString("manifestVersion", report.Target.ManifestVersion.Value);
            writer.WriteString("fingerprint", report.Target.Fingerprint);
            writer.WriteEndObject();
        }
        WriteNullable(writer, "planFingerprint", report.PlanFingerprint);
        WriteNullable(writer, "appliedTargetFingerprint", report.AppliedTargetFingerprint);
        WriteResolvedNames(writer, report.Target);
        WritePending(writer, report.PendingOperations);
        WriteApplied(writer, report.AppliedOperations);
        WriteAuthorization(writer, report);
        WriteDiagnosticRecords(writer, report.DiagnosticRecords);
        WriteDiagnostics(writer, report.Diagnostics);
        writer.WriteBoolean("targetMutated", report.Mutated);
        writer.WriteEndObject();
    }

    private static void WriteResolvedNames(Utf8JsonWriter writer, PhysicalSchemaTarget? target)
    {
        writer.WritePropertyName("resolvedNames");
        writer.WriteStartArray();
        foreach (var item in (target?.Routes ?? [])
                     .SelectMany(ResolvedNames)
                     .OrderBy(item => item.StorageUnit, StringComparer.Ordinal)
                     .ThenBy(item => item.Kind, StringComparer.Ordinal)
                     .ThenBy(item => item.LogicalName, StringComparer.Ordinal)
                     .ThenBy(item => item.Target)
                     .ThenBy(item => item.Identifier, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("storageUnit", item.StorageUnit);
            writer.WriteString("kind", item.Kind);
            writer.WriteString("logicalName", item.LogicalName);
            writer.WriteString("identifier", item.Identifier);
            writer.WriteString("target", item.Target.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static IEnumerable<ResolvedNameReport> ResolvedNames(ExecutableStorageRoute route)
    {
        yield return Name(route, "primaryStorage", route.PrimaryStorage.Name, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.Id, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.Identity.ComparisonKey, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.Identity.LookupKey, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.DocumentKind, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.StorageScope, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.Version, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.SchemaVersion, ExecutableStorageObjectRole.PrimaryStorage);
        yield return Name(route, "envelopeField", route.Envelope.CanonicalJson, ExecutableStorageObjectRole.PrimaryStorage);
        if (route.LinkedIndexStorage is not null)
            yield return Name(route, "linkedIndexStorage", route.LinkedIndexStorage.Name, ExecutableStorageObjectRole.LinkedIndexStorage);
        if (route.LinkedRelationship is not null)
        {
            yield return Name(route, "linkedRelationshipField", route.LinkedRelationship.DocumentId, ExecutableStorageObjectRole.LinkedIndexStorage);
            yield return Name(route, "linkedRelationshipField", route.LinkedRelationship.Identity.ComparisonKey, ExecutableStorageObjectRole.LinkedIndexStorage);
            yield return Name(route, "linkedRelationshipField", route.LinkedRelationship.Identity.LookupKey, ExecutableStorageObjectRole.LinkedIndexStorage);
            yield return Name(route, "linkedRelationshipField", route.LinkedRelationship.DocumentKind, ExecutableStorageObjectRole.LinkedIndexStorage);
            yield return Name(route, "linkedRelationshipField", route.LinkedRelationship.StorageScope, ExecutableStorageObjectRole.LinkedIndexStorage);
        }
        foreach (var column in route.ProjectedColumns)
            yield return Name(route, "projectedColumn", column.Name, column.Target);
        foreach (var index in route.Indexes)
            yield return Name(route, "physicalIndex", index.Name, index.Target);
    }

    private static ResolvedNameReport Name(
        ExecutableStorageRoute route,
        string kind,
        ProviderPhysicalObjectName name,
        ExecutableStorageObjectRole target) =>
        new(route.StorageUnit.Value, kind, name.LogicalName, name.Identifier, target);

    private static ResolvedNameReport Name(
        ExecutableStorageRoute route,
        string kind,
        ExecutableColumnRoute column,
        ExecutableStorageObjectRole target) =>
        new(route.StorageUnit.Value, kind, column.LogicalName, column.Identifier, target);

    private static void WritePending(Utf8JsonWriter writer, IReadOnlyList<SchemaToolOperation> operations)
    {
        writer.WritePropertyName("pendingOperations");
        writer.WriteStartArray();
        foreach (var operation in operations)
        {
            writer.WriteStartObject();
            writer.WriteString("identity", operation.Identity);
            writer.WriteString("fingerprint", operation.Fingerprint);
            writer.WriteString("kind", operation.Kind);
            WriteNullable(writer, "storageUnit", operation.StorageUnit);
            writer.WriteString("subjectIdentity", operation.SubjectIdentity);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteApplied(Utf8JsonWriter writer, IReadOnlyList<SchemaToolOperation> operations)
    {
        writer.WritePropertyName("appliedOperations");
        writer.WriteStartArray();
        foreach (var operation in operations.OrderBy(item => item.Identity, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("identity", operation.Identity);
            writer.WriteString("fingerprint", operation.Fingerprint);
            writer.WriteString("kind", operation.Kind);
            WriteNullable(writer, "storageUnit", operation.StorageUnit);
            writer.WriteString("subjectIdentity", operation.SubjectIdentity);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteAuthorization(Utf8JsonWriter writer, SchemaToolReport report)
    {
        var destructive = report.PendingOperations
            .Where(operation => operation.IsDestructive)
            .Select(operation => operation.Identity)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        var semantic = report.PendingOperations
            .Select(operation => operation.SemanticMigrationIdentity)
            .Where(identity => identity is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        writer.WritePropertyName("authorization");
        writer.WriteStartObject();
        writer.WriteBoolean("destructiveRequired", destructive.Length != 0);
        writer.WritePropertyName("destructiveOperationsRequired");
        writer.WriteStartArray();
        foreach (var identity in destructive)
            writer.WriteStringValue(identity);
        writer.WriteEndArray();
        writer.WritePropertyName("semanticRequired");
        writer.WriteStartArray();
        foreach (var identity in semantic)
            writer.WriteStringValue(identity);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDiagnostics(Utf8JsonWriter writer, IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();
        foreach (var diagnostic in diagnostics
                     .OrderBy(item => item.Code, StringComparer.Ordinal)
                     .ThenBy(item => item.Target, StringComparer.Ordinal)
                     .ThenBy(item => item.Message, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("severity", diagnostic.Severity.ToString().ToLowerInvariant());
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("message", diagnostic.Message);
            WriteNullable(writer, "target", diagnostic.Target);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteDiagnosticRecords(Utf8JsonWriter writer, DiagnosticRecordDeploymentStatus? status)
    {
        writer.WritePropertyName("diagnosticRecords");
        if (status is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("fingerprint", status.Fingerprint);
        writer.WritePropertyName("declaredStreams");
        writer.WriteStartArray();
        foreach (var stream in status.DeclaredStreams.OrderBy(value => value, StringComparer.Ordinal))
            writer.WriteStringValue(stream);
        writer.WriteEndArray();
        writer.WritePropertyName("pendingStreams");
        writer.WriteStartArray();
        foreach (var stream in status.PendingStreams.OrderBy(value => value, StringComparer.Ordinal))
            writer.WriteStringValue(stream);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private sealed record ResolvedNameReport(
        string StorageUnit,
        string Kind,
        string LogicalName,
        string Identifier,
        ExecutableStorageObjectRole Target);
}
