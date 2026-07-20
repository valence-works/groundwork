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
        await EnsureReadyAsync(inspector, deployment, cancellationToken);
        return new Session(deployment, scope, inspector, openStoreAsync);
    }

    private static async ValueTask EnsureReadyAsync(
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
            await EnsureReadyAsync(inspector, deployment, cancellationToken);
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
        Handlers = new(this, this, this, this);
    }

    public DiagnosticRecordStoreHandlers Handlers { get; }

    public DiagnosticQueryHandlerCapabilities Capabilities => inner.Handlers.Query.Capabilities;

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
