using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Manifests;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.DiagnosticRecords;

/// <summary>
/// Provider-neutral deployment input. It composes the application's physical document storage
/// declaration with immutable diagnostic-record stream snapshots that share the same provider
/// deployment boundary. Diagnostic streams deliberately remain distinct from document units: a
/// stream is an append/trim log with provider-owned operational ledgers, not a document table.
/// </summary>
public sealed class DiagnosticRecordDeploymentManifest
{
    public DiagnosticRecordDeploymentManifest(
        StorageManifest storage,
        IReadOnlyList<DiagnosticRecordStreamDefinition>? streams = null)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Streams = Array.AsReadOnly((streams ?? [])
            .Select(DiagnosticRecordStreamDefinitionSnapshot.Capture)
            .OrderBy(stream => stream.Stream.Value, StringComparer.Ordinal)
            .ToArray());
        Validate();
        DiagnosticFingerprint = CreateFingerprint(Streams);
    }

    /// <summary>The physical document-storage declaration deployed beside the diagnostic streams.</summary>
    public StorageManifest Storage { get; }

    /// <summary>Immutable stream-definition snapshots ordered by stream identity.</summary>
    public IReadOnlyList<DiagnosticRecordStreamDefinition> Streams { get; }

    /// <summary>
    /// Deterministic diagnostic-side identity. The compiled physical-schema target provides the
    /// independent document-side identity when the schema tool builds a combined plan.
    /// </summary>
    public string DiagnosticFingerprint { get; }

    public static DiagnosticRecordDeploymentManifest Capture(
        StorageManifest storage,
        IReadOnlyList<DiagnosticRecordStreamDefinition>? streams = null) =>
        new(storage, streams);

    private void Validate()
    {
        var duplicates = Streams
            .GroupBy(stream => stream.Stream.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length != 0)
        {
            throw new DiagnosticRecordDeploymentManifestException(
                "deployment.stream.duplicate",
                $"Diagnostic stream identity '{duplicates[0]}' is declared more than once.");
        }

        foreach (var stream in Streams)
            DiagnosticRecordStreamDefinitionValidator.ValidateAndThrow(stream);
    }

    private static string CreateFingerprint(IReadOnlyList<DiagnosticRecordStreamDefinition> streams)
    {
        var parts = new List<string>
        {
            "groundwork-diagnostic-record-deployment-manifest-v2"
        };
        parts.AddRange(streams.Select(stream =>
        {
            var state = DiagnosticRecordPhysicalSchemaState.Capture(stream);
            return $"{stream.Stream.Value}:{state.DefinitionFingerprint}";
        }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }
}

/// <summary>Thrown when a combined deployment declaration cannot be compiled safely.</summary>
public sealed class DiagnosticRecordDeploymentManifestException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Optional deployment-source extension for applications that declare diagnostic-record streams.
/// Existing Core-only <see cref="IPhysicalSchemaManifestSource"/> implementations remain valid:
/// the schema tool treats them as a deployment with zero streams.
/// </summary>
public interface IDiagnosticRecordDeploymentManifestSource : IPhysicalSchemaManifestSource
{
    DiagnosticRecordDeploymentManifest CreateDeploymentManifest();
}

/// <summary>
/// Provider-neutral admission boundary for a declared stream set. Provider packages own concrete
/// implementations, connection handling, and deterministic disposal; hosts only provide the
/// deployment declaration and the logical scope they are opening.
/// </summary>
public interface IDiagnosticRecordStoreSessionFactory
{
    ValueTask<IDiagnosticRecordStoreSession> OpenAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStorageScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only provider admission for a complete diagnostic-record deployment. Provider packages
/// own the inspection mechanics; this contract makes the result and failure semantics stable for
/// application hosts without exposing provider SDK types.
/// </summary>
public interface IDiagnosticRecordDeploymentInspector
{
    /// <summary>The stable provider identity used in admission results and failures.</summary>
    string Provider { get; }

    ValueTask<DiagnosticRecordDeploymentInspection> InspectAsync(
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces provider-native planner evidence for the exact bounded diagnostic-record query,
/// statistics-inspection, or trim-selection route. Inspection is admission-gated and read-only:
/// it never creates or repairs physical storage. This deliberately covers the scale-bearing
/// diagnostic-record reads, not generic point document save/delete mutations. The returned raw
/// plan can expose object names, predicates, and parameter values, so hosts must treat it as
/// sensitive diagnostic data.
/// </summary>
public interface IDiagnosticRecordPlanInspector
{
    /// <summary>The stable provider identity used in plan-inspection results.</summary>
    string Provider { get; }

    ValueTask<DiagnosticRecordNativePlan> InspectQueryAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<DiagnosticRecordNativePlan> InspectStatisticsAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<DiagnosticRecordNativePlan> InspectTrimSelectionAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>The executable diagnostic-record route represented by native planner output.</summary>
public enum DiagnosticRecordPlanOperation
{
    Query,
    Statistics,
    TrimSelection
}

/// <summary>
/// Immutable provider-native planner output. <see cref="RawPlans"/> is intentionally not
/// normalized: it is evidence emitted by the provider for the exact operation route and may
/// contain sensitive database metadata or query values.
/// </summary>
public sealed record DiagnosticRecordNativePlan
{
    public DiagnosticRecordNativePlan(
        string provider,
        DiagnosticRecordPlanOperation operation,
        string format,
        IReadOnlyList<string> rawPlans)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        ArgumentNullException.ThrowIfNull(rawPlans);
        if (rawPlans.Count == 0 || rawPlans.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Native planner output must contain at least one non-empty item.", nameof(rawPlans));

        Provider = provider;
        Operation = operation;
        Format = format;
        RawPlans = Array.AsReadOnly(rawPlans.ToArray());
    }

    public string Provider { get; }
    public DiagnosticRecordPlanOperation Operation { get; }
    public string Format { get; }
    public IReadOnlyList<string> RawPlans { get; }
}

/// <summary>Stable names for provider-native diagnostic-record plan payload formats.</summary>
public static class DiagnosticRecordNativePlanFormats
{
    public const string SqliteExplainQueryPlan = "sqlite-explain-query-plan";
    public const string PostgreSqlExplainJson = "postgresql-explain-json";
    public const string SqlServerShowplanXml = "sqlserver-showplan-xml";
    public const string MongoDbExplainJson = "mongodb-explain-json";
}

/// <summary>
/// Provider-neutral adapter that gates native plan inspection through the same non-mutating
/// deployment admission used by runtime sessions. Provider packages supply the exact existing
/// command/pipeline explain routes; this type deliberately owns no provider SDK dependency.
/// </summary>
public sealed class DelegatingDiagnosticRecordPlanInspector : IDiagnosticRecordPlanInspector
{
    private readonly IDiagnosticRecordDeploymentInspector deploymentInspector;
    private readonly Func<DiagnosticRecordStreamDefinition, DiagnosticRecordQuery, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectQueryAsync;
    private readonly Func<DiagnosticRecordStreamDefinition, DiagnosticStreamInspectionRequest, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectStatisticsAsync;
    private readonly Func<DiagnosticRecordStreamDefinition, DiagnosticTrimRequest, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectTrimSelectionAsync;

    public DelegatingDiagnosticRecordPlanInspector(
        IDiagnosticRecordDeploymentInspector deploymentInspector,
        Func<DiagnosticRecordStreamDefinition, DiagnosticRecordQuery, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectQueryAsync,
        Func<DiagnosticRecordStreamDefinition, DiagnosticTrimRequest, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectTrimSelectionAsync)
        : this(deploymentInspector, inspectQueryAsync, UnsupportedStatisticsInspection, inspectTrimSelectionAsync)
    {
    }

    public DelegatingDiagnosticRecordPlanInspector(
        IDiagnosticRecordDeploymentInspector deploymentInspector,
        Func<DiagnosticRecordStreamDefinition, DiagnosticRecordQuery, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectQueryAsync,
        Func<DiagnosticRecordStreamDefinition, DiagnosticStreamInspectionRequest, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectStatisticsAsync,
        Func<DiagnosticRecordStreamDefinition, DiagnosticTrimRequest, CancellationToken, ValueTask<DiagnosticRecordNativePlan>> inspectTrimSelectionAsync)
    {
        this.deploymentInspector = deploymentInspector ?? throw new ArgumentNullException(nameof(deploymentInspector));
        this.inspectQueryAsync = inspectQueryAsync ?? throw new ArgumentNullException(nameof(inspectQueryAsync));
        this.inspectStatisticsAsync = inspectStatisticsAsync ?? throw new ArgumentNullException(nameof(inspectStatisticsAsync));
        this.inspectTrimSelectionAsync = inspectTrimSelectionAsync ?? throw new ArgumentNullException(nameof(inspectTrimSelectionAsync));
    }

    public string Provider => deploymentInspector.Provider;

    public ValueTask<DiagnosticRecordNativePlan> InspectQueryAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        InspectAsync(deployment, query.Stream, DiagnosticRecordPlanOperation.Query,
            definition => inspectQueryAsync(definition, query, cancellationToken), cancellationToken);

    public ValueTask<DiagnosticRecordNativePlan> InspectStatisticsAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        InspectAsync(deployment, request.Stream, DiagnosticRecordPlanOperation.Statistics,
            definition => inspectStatisticsAsync(definition, request, cancellationToken), cancellationToken);

    public ValueTask<DiagnosticRecordNativePlan> InspectTrimSelectionAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        InspectAsync(deployment, request.Stream, DiagnosticRecordPlanOperation.TrimSelection,
            definition => inspectTrimSelectionAsync(definition, request, cancellationToken), cancellationToken);

    private async ValueTask<DiagnosticRecordNativePlan> InspectAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStreamId stream,
        DiagnosticRecordPlanOperation operation,
        Func<DiagnosticRecordStreamDefinition, ValueTask<DiagnosticRecordNativePlan>> inspectAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        cancellationToken.ThrowIfCancellationRequested();
        var definition = deployment.Streams.SingleOrDefault(item =>
            StringComparer.Ordinal.Equals(item.Stream.Value, stream.Value))
            ?? throw new InvalidOperationException($"Diagnostic stream '{stream.Value}' is not declared by this deployment.");

        await DiagnosticRecordDeploymentAdmission.EnsureReadyAsync(deploymentInspector, deployment, cancellationToken);
        var plan = await inspectAsync(definition)
            ?? throw new InvalidOperationException($"Diagnostic-record plan inspector '{GetType().FullName}' returned no result.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(plan.Provider, Provider))
            throw new InvalidOperationException($"Diagnostic-record plan inspector reported provider '{plan.Provider}' instead of '{Provider}'.");
        if (plan.Operation != operation)
            throw new InvalidOperationException($"Diagnostic-record plan inspector returned '{plan.Operation}' for requested '{operation}'.");
        return plan;
    }

    private static ValueTask<DiagnosticRecordNativePlan> UnsupportedStatisticsInspection(
        DiagnosticRecordStreamDefinition definition,
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<DiagnosticRecordNativePlan>(new NotSupportedException(
            "This diagnostic-record plan inspector does not provide statistics-inspection plans."));
}

/// <summary>The result of a provider's non-mutating diagnostic-record deployment inspection.</summary>
public sealed record DiagnosticRecordDeploymentInspection
{
    public DiagnosticRecordDeploymentInspection(
        string provider,
        DiagnosticRecordDeploymentAdmissionStatus status,
        IReadOnlyList<string>? missingStreams = null,
        IReadOnlyList<string>? driftedStreams = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (!StringComparer.Ordinal.Equals(provider, provider.Trim()))
            throw new ArgumentException("The provider identity cannot start or end with whitespace.", nameof(provider));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, null);

        Provider = provider;
        Status = status;
        MissingStreams = Snapshot(missingStreams);
        DriftedStreams = Snapshot(driftedStreams);
        if (status != DiagnosticRecordDeploymentAdmissionStatus.Missing && MissingStreams.Count != 0)
            throw new ArgumentException("Only a missing deployment inspection may declare missing streams.", nameof(missingStreams));
        if (status != DiagnosticRecordDeploymentAdmissionStatus.Drifted && DriftedStreams.Count != 0)
            throw new ArgumentException("Only a drifted deployment inspection may declare drifted streams.", nameof(driftedStreams));
        if (status == DiagnosticRecordDeploymentAdmissionStatus.Missing && MissingStreams.Count == 0)
            throw new ArgumentException("A missing deployment inspection must identify at least one stream.", nameof(missingStreams));
        if (status == DiagnosticRecordDeploymentAdmissionStatus.Drifted && DriftedStreams.Count == 0)
            throw new ArgumentException("A drifted deployment inspection must identify at least one stream.", nameof(driftedStreams));
    }

    public string Provider { get; }
    public DiagnosticRecordDeploymentAdmissionStatus Status { get; }
    public IReadOnlyList<string> MissingStreams { get; }
    public IReadOnlyList<string> DriftedStreams { get; }
    public bool IsReady => Status == DiagnosticRecordDeploymentAdmissionStatus.Ready;

    public static DiagnosticRecordDeploymentInspection Ready(string provider) =>
        new(provider, DiagnosticRecordDeploymentAdmissionStatus.Ready);

    public static DiagnosticRecordDeploymentInspection Missing(
        string provider,
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlyList<string>? streams = null)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return new(provider, DiagnosticRecordDeploymentAdmissionStatus.Missing,
            (streams ?? deployment.Streams.Select(stream => stream.Stream.Value).ToArray())
            .OrderBy(stream => stream, StringComparer.Ordinal).ToArray());
    }

    public static DiagnosticRecordDeploymentInspection Drifted(
        string provider,
        IReadOnlyList<string> streams) =>
        new(provider, DiagnosticRecordDeploymentAdmissionStatus.Drifted,
            driftedStreams: streams.OrderBy(stream => stream, StringComparer.Ordinal).ToArray());

    public static DiagnosticRecordDeploymentInspection Rejected(string provider) =>
        new(provider, DiagnosticRecordDeploymentAdmissionStatus.Rejected);

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string>? streams)
    {
        if (streams is null || streams.Count == 0)
            return Array.Empty<string>();
        if (streams.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Diagnostic stream identities must be non-empty.", nameof(streams));
        return Array.AsReadOnly(streams
            .Distinct(StringComparer.Ordinal)
            .OrderBy(stream => stream, StringComparer.Ordinal)
            .ToArray());
    }
}

/// <summary>The durable state of a provider inspection before a diagnostic-record session opens.</summary>
public enum DiagnosticRecordDeploymentAdmissionStatus
{
    Ready,
    Missing,
    Drifted,
    Rejected
}

/// <summary>Stable machine-readable codes emitted by runtime diagnostic-record admission.</summary>
public static class DiagnosticRecordDeploymentAdmissionErrorCodes
{
    public const string Missing = "GW-DIAG-DEPLOY-001";
    public const string Drifted = "GW-DIAG-DEPLOY-002";
    public const string InspectionFailed = "GW-DIAG-DEPLOY-003";
}

/// <summary>
/// Stable failure returned when runtime diagnostic persistence is not already deployed. Runtime
/// admission is intentionally inspect-only: callers must use their explicit deployment workflow
/// to repair missing or drifted state.
/// </summary>
public sealed class DiagnosticRecordDeploymentAdmissionException(
    string code,
    DiagnosticRecordDeploymentInspection inspection,
    Exception? innerException = null)
    : InvalidOperationException(CreateMessage(code, inspection), innerException)
{
    public string Code { get; } = code;
    public DiagnosticRecordDeploymentInspection Inspection { get; } = inspection;

    internal static DiagnosticRecordDeploymentAdmissionException FromInspection(
        DiagnosticRecordDeploymentInspection inspection) =>
        new(inspection.Status switch
        {
            DiagnosticRecordDeploymentAdmissionStatus.Missing => DiagnosticRecordDeploymentAdmissionErrorCodes.Missing,
            DiagnosticRecordDeploymentAdmissionStatus.Drifted => DiagnosticRecordDeploymentAdmissionErrorCodes.Drifted,
            _ => DiagnosticRecordDeploymentAdmissionErrorCodes.InspectionFailed
        }, inspection);

    private static string CreateMessage(string code, DiagnosticRecordDeploymentInspection inspection) =>
        inspection.Status switch
        {
            DiagnosticRecordDeploymentAdmissionStatus.Missing =>
                $"{code}: {inspection.Provider} diagnostic-record schema is not fully deployed. Missing streams: {string.Join(", ", inspection.MissingStreams)}.",
            DiagnosticRecordDeploymentAdmissionStatus.Drifted =>
                $"{code}: {inspection.Provider} diagnostic-record schema has incompatible physical or persisted-definition state. Affected streams: {string.Join(", ", inspection.DriftedStreams)}.",
            _ => $"{code}: {inspection.Provider} diagnostic-record deployment admission failed."
        };
}

/// <summary>A scope-bound diagnostic-record store session with deterministic async disposal.</summary>
public interface IDiagnosticRecordStoreSession : IAsyncDisposable
{
    DiagnosticStorageScope Scope { get; }

    ValueTask<IDiagnosticRecordStore> OpenStoreAsync(
        DiagnosticStreamId stream,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider-neutral adapter for provider packages that can open one declared stream. It snapshots
/// the deployment, rejects undeclared streams before a provider is touched, binds every returned
/// store to one logical scope, and disposes all opened provider resources deterministically.
/// </summary>
public sealed class DelegatingDiagnosticRecordStoreSessionFactory(
    IDiagnosticRecordDeploymentInspector inspector,
    Func<DiagnosticRecordStreamDefinition, CancellationToken, ValueTask<DiagnosticRecordStoreLease>> openStoreAsync)
    : IDiagnosticRecordStoreSessionFactory
{
    private readonly IDiagnosticRecordDeploymentInspector inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));
    private readonly Func<DiagnosticRecordStreamDefinition, CancellationToken, ValueTask<DiagnosticRecordStoreLease>> openStoreAsync =
        openStoreAsync ?? throw new ArgumentNullException(nameof(openStoreAsync));

    public async ValueTask<IDiagnosticRecordStoreSession> OpenAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStorageScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        cancellationToken.ThrowIfCancellationRequested();
        await DiagnosticRecordDeploymentAdmission.EnsureReadyAsync(inspector, deployment, cancellationToken);
        return new Session(deployment, scope, inspector, openStoreAsync);
    }

    private sealed class Session(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStorageScope scope,
        IDiagnosticRecordDeploymentInspector inspector,
        Func<DiagnosticRecordStreamDefinition, CancellationToken, ValueTask<DiagnosticRecordStoreLease>> openStoreAsync)
        : IDiagnosticRecordStoreSession
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Dictionary<string, Task<DiagnosticRecordStoreLease>> leases = new(StringComparer.Ordinal);
        private bool disposed;

        public DiagnosticStorageScope Scope { get; } = scope;

        public async ValueTask<IDiagnosticRecordStore> OpenStoreAsync(
            DiagnosticStreamId stream,
            CancellationToken cancellationToken = default)
        {
            var definition = deployment.Streams.SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item.Stream.Value, stream.Value));
            if (definition is null)
                throw new InvalidOperationException($"Diagnostic stream '{stream.Value}' is not declared by this deployment.");

            Task<DiagnosticRecordStoreLease> open;
            await gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!leases.TryGetValue(stream.Value, out open!))
                {
                    open = OpenAdmittedStoreAsync(definition, lifetime.Token);
                    leases.Add(stream.Value, open);
                }
            }
            finally
            {
                gate.Release();
            }

            var lease = await open.WaitAsync(cancellationToken);
            await gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }
            finally
            {
                gate.Release();
            }
            return new ScopeBoundDiagnosticRecordStore(lease.Store, Scope, stream);
        }

        private async Task<DiagnosticRecordStoreLease> OpenAdmittedStoreAsync(
            DiagnosticRecordStreamDefinition definition,
            CancellationToken cancellationToken)
        {
            // Re-inspect immediately before the first store for each stream is exposed. This closes
            // the ordinary host-start race without granting the open path any schema-repair authority.
            await DiagnosticRecordDeploymentAdmission.EnsureReadyAsync(inspector, deployment, cancellationToken);
            return await openStoreAsync(definition, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Task<DiagnosticRecordStoreLease>[] opens;
            await gate.WaitAsync();
            try
            {
                if (disposed)
                    return;
                disposed = true;
                lifetime.Cancel();
                opens = leases.OrderByDescending(item => item.Key, StringComparer.Ordinal)
                    .Select(item => item.Value)
                    .ToArray();
                leases.Clear();
            }
            finally
            {
                gate.Release();
            }

            var failures = new List<Exception>();
            foreach (var open in opens)
            {
                DiagnosticRecordStoreLease lease;
                try
                {
                    lease = await open;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    // An in-flight open observed session disposal and never produced a lease.
                    continue;
                }
                catch
                {
                    // Admission/provider-open failures are returned to the opening caller and do
                    // not own a resource. Re-throwing them from cleanup would mask that failure.
                    continue;
                }

                try
                {
                    await lease.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            lifetime.Dispose();
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException("One or more diagnostic-record leases failed to dispose.", failures);
        }
    }
}

internal static class DiagnosticRecordDeploymentAdmission
{
    public static async ValueTask EnsureReadyAsync(
        IDiagnosticRecordDeploymentInspector inspector,
        DiagnosticRecordDeploymentManifest deployment,
        CancellationToken cancellationToken)
    {
        DiagnosticRecordDeploymentInspection inspection;
        try
        {
            inspection = await inspector.InspectAsync(deployment, cancellationToken)
                         ?? throw new InvalidOperationException(
                             $"Diagnostic-record deployment inspector '{inspector.GetType().FullName}' returned no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DiagnosticRecordDeploymentAdmissionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DiagnosticRecordDeploymentAdmissionException(
                DiagnosticRecordDeploymentAdmissionErrorCodes.InspectionFailed,
                DiagnosticRecordDeploymentInspection.Rejected(inspector.Provider),
                exception);
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(inspection.Provider, inspector.Provider))
        {
            throw new DiagnosticRecordDeploymentAdmissionException(
                DiagnosticRecordDeploymentAdmissionErrorCodes.InspectionFailed,
                DiagnosticRecordDeploymentInspection.Rejected(inspector.Provider),
                new InvalidOperationException(
                    $"Diagnostic-record deployment inspector '{inspector.GetType().FullName}' reported provider " +
                    $"'{inspection.Provider}' instead of '{inspector.Provider}'."));
        }
        if (!inspection.IsReady)
            throw DiagnosticRecordDeploymentAdmissionException.FromInspection(inspection);
    }
}

/// <summary>Store plus optional provider resource ownership returned by a session-factory adapter.</summary>
public sealed class DiagnosticRecordStoreLease(IDiagnosticRecordStore store, IAsyncDisposable? resource = null) : IAsyncDisposable
{
    public IDiagnosticRecordStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask DisposeAsync() => resource?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal sealed class ScopeBoundDiagnosticRecordStore :
    IDiagnosticRecordStore,
    IDiagnosticAppendHandler,
    IDiagnosticQueryHandler,
    IDiagnosticGroupedQueryHandler,
    IDiagnosticInspectHandler,
    IDiagnosticTrimHandler
{
    private readonly IDiagnosticRecordStore inner;
    private readonly DiagnosticStorageScope scope;
    private readonly DiagnosticStreamId stream;

    public ScopeBoundDiagnosticRecordStore(
        IDiagnosticRecordStore inner,
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream)
    {
        this.inner = inner;
        this.scope = scope;
        this.stream = stream;
        Handlers = new(this, this, this, this) { GroupedQuery = this };
    }

    public DiagnosticRecordStoreHandlers Handlers { get; }

    public DiagnosticQueryHandlerCapabilities Capabilities => inner.Handlers.Query.Capabilities;
    DiagnosticGroupedQueryHandlerCapabilities IDiagnosticGroupedQueryHandler.Capabilities => inner.Handlers.GroupedQuery.Capabilities;

    public ValueTask<DiagnosticAppendResult> AppendAsync(DiagnosticRecordBatch batch, CancellationToken cancellationToken = default)
    {
        Ensure(batch.Scope, batch.Stream);
        return inner.AppendAsync(batch, cancellationToken);
    }

    public ValueTask<DiagnosticRecordPage> QueryAsync(DiagnosticRecordQuery query, CancellationToken cancellationToken = default)
    {
        Ensure(query.Scope, query.Stream);
        return inner.QueryAsync(query, cancellationToken);
    }

    public ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(DiagnosticRecordGroupQuery query, CancellationToken cancellationToken = default)
    {
        Ensure(query.Scope, query.Stream);
        return inner.QueryGroupsAsync(query, cancellationToken);
    }

    public ValueTask<DiagnosticStreamStatistics> InspectAsync(DiagnosticStreamInspectionRequest request, CancellationToken cancellationToken = default)
    {
        Ensure(request.Scope, request.Stream);
        return inner.InspectAsync(request, cancellationToken);
    }

    public ValueTask<DiagnosticTrimResult> TrimAsync(DiagnosticTrimRequest request, CancellationToken cancellationToken = default)
    {
        Ensure(request.Scope, request.Stream);
        return inner.TrimAsync(request, cancellationToken);
    }

    private void Ensure(DiagnosticStorageScope actualScope, DiagnosticStreamId actualStream)
    {
        if (actualScope != scope || actualStream != stream)
            throw new InvalidOperationException("The diagnostic store session cannot access a different scope or stream.");
    }
}
