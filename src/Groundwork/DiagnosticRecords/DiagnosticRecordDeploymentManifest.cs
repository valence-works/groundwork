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
    Func<DiagnosticRecordStreamDefinition, CancellationToken, ValueTask<DiagnosticRecordStoreLease>> openStoreAsync)
    : IDiagnosticRecordStoreSessionFactory
{
    private readonly Func<DiagnosticRecordStreamDefinition, CancellationToken, ValueTask<DiagnosticRecordStoreLease>> openStoreAsync =
        openStoreAsync ?? throw new ArgumentNullException(nameof(openStoreAsync));

    public ValueTask<IDiagnosticRecordStoreSession> OpenAsync(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStorageScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IDiagnosticRecordStoreSession>(new Session(deployment, scope, openStoreAsync));
    }

    private sealed class Session(
        DiagnosticRecordDeploymentManifest deployment,
        DiagnosticStorageScope scope,
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
                    open = openStoreAsync(definition, lifetime.Token).AsTask();
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
                try
                {
                    await (await open).DisposeAsync();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    // An in-flight open observed session disposal and never produced a lease.
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
