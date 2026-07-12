using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;
using Groundwork.Documents.Store;
using Groundwork.Materialization;
using Groundwork.MongoDb.Documents;
using Groundwork.Modules.Inbox;
using Groundwork.Modules.Inbox.Sqlite;
using Groundwork.PostgreSql.Documents;
using Groundwork.SqlServer.Documents;
using Groundwork.Operational;
using Groundwork.Sqlite;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.Materialization;
using Groundwork.Sqlite.Operational;
using Groundwork.SupportTickets.ExternalModules;
using Groundwork.SupportTickets.Operations;
using Microsoft.Data.Sqlite;

namespace Groundwork.SupportTickets;

public sealed class SupportTicketSampleHost : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> disposables;

    private SupportTicketSampleHost(
        StorageManifest manifest,
        IDocumentStore store,
        SupportTicketRepository tickets,
        SupportTicketOperations operations,
        OperationalFitReport operationalFit,
        IInboxStore inbox,
        ExternalModuleFitReport externalModuleFit,
        List<IAsyncDisposable> disposables)
    {
        Manifest = manifest;
        Store = store;
        Tickets = tickets;
        Operations = operations;
        OperationalFit = operationalFit;
        Inbox = inbox;
        ExternalModuleFit = externalModuleFit;
        this.disposables = disposables;
    }

    public StorageManifest Manifest { get; }
    public IDocumentStore Store { get; }
    public SupportTicketRepository Tickets { get; }

    /// <summary>The operational hot path (triage queue, ownership leases, notification outbox).</summary>
    public SupportTicketOperations Operations { get; }

    /// <summary>
    /// The capability-derived verdict for the operational manifest against an operational-capable
    /// provider versus a portable document-only provider. Demonstrates that fit is computed from
    /// requirements, not author-declared.
    /// </summary>
    public OperationalFitReport OperationalFit { get; }

    /// <summary>An external module store proving custom capabilities can be wired without core edits.</summary>
    public IInboxStore Inbox { get; }

    /// <summary>
    /// Capability-derived verdict for the externally registered Inbox module.
    /// </summary>
    public ExternalModuleFitReport ExternalModuleFit { get; }

    public static Task<SupportTicketSampleHost> CreateAsync(string connectionString = "Data Source=:memory:") =>
        CreateAsync(new SupportTicketStorageOptions(SupportTicketProvider.Sqlite, connectionString));

    public static async Task<SupportTicketSampleHost> CreateAsync(
        SupportTicketStorageOptions options,
        CancellationToken cancellationToken = default)
    {
        var manifest = SupportTicketManifest.Create(options.EffectivePhysicalization, options.EffectivePhysicalizedIndexes);
        var (store, disposables) = await CreateStoreAsync(options, manifest, cancellationToken);
        var (operations, fit) = await CreateOperationsAsync(disposables, options.OperationalClock, cancellationToken);
        var (inbox, externalModuleFit) = await CreateExternalModulesAsync(disposables, cancellationToken);
        return new SupportTicketSampleHost(manifest, store, new SupportTicketRepository(store), operations, fit, inbox, externalModuleFit, disposables);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in disposables)
            await disposable.DisposeAsync();
    }

    private static async Task<(IDocumentStore Store, List<IAsyncDisposable> Disposables)> CreateStoreAsync(
        SupportTicketStorageOptions options,
        StorageManifest manifest,
        CancellationToken cancellationToken)
    {
        var disposables = new List<IAsyncDisposable>();
        switch (options.Provider)
        {
            case SupportTicketProvider.Sqlite:
                {
                    var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
                    if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:")
                    {
                        var connection = new SqliteConnection(options.ConnectionString);
                        try
                        {
                            var provider = Provider("groundwork-sqlite");
                            var plan = new MaterializationPlanner(new StorageManifestValidator(), new ProviderCapabilityValidator())
                                .Plan(
                                    manifest,
                                    SqliteGroundworkCapabilities.Runtime(provider),
                                    SqliteGroundworkCapabilities.Materialization(provider));
                            await new SqliteGroundworkMaterializer(connection).MaterializeAsync(
                                plan,
                                cancellationToken);
                            disposables.Add(connection);
                            return (new SqliteDocumentStore(connection, manifest, Groundwork.Documents.Scoping.DocumentStoreAccess.Global), disposables);
                        }
                        catch
                        {
                            await connection.DisposeAsync();
                            throw;
                        }
                    }

                    var store = await SqliteDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        manifest,
                        Provider("groundwork-sqlite"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    return (store, disposables);
                }
            case SupportTicketProvider.PostgreSql:
                {
                    var store = await PostgreSqlDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        manifest,
                        Provider("groundwork-postgresql"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    return (store, disposables);
                }
            case SupportTicketProvider.SqlServer:
                {
                    var store = await SqlServerDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        manifest,
                        Provider("groundwork-sqlserver"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    return (store, disposables);
                }
            case SupportTicketProvider.MongoDb:
                {
                    var handle = await MongoDbDocumentStoreFactory.CreateAsync(
                        options.ConnectionString,
                        options.DatabaseName ?? "groundwork_support_tickets",
                        manifest,
                        Provider("groundwork-mongodb"),
                        Groundwork.Documents.Scoping.DocumentStoreAccess.Global,
                        cancellationToken: cancellationToken);
                    disposables.Add(handle);
                    return (handle.Store, disposables);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unsupported support-ticket provider.");
        }
    }

    private static async Task<(SupportTicketOperations Operations, OperationalFitReport Fit)> CreateOperationsAsync(
        List<IAsyncDisposable> disposables,
        IOperationalClock? clock,
        CancellationToken cancellationToken)
    {
        // The operational hot path always runs on a dedicated SQLite operational provider, regardless
        // of the chosen document provider. This is the capability-derived fit story in action: the
        // operational units below are Unsupported on a portable document-only provider, so they must
        // run on an operational-capable one.
        var connection = new SqliteConnection("Data Source=:memory:");
        disposables.Insert(0, connection);
        await connection.OpenAsync(cancellationToken);
        await new SqliteOperationalMaterializer(connection).MaterializeAsync(cancellationToken);

        var store = new SqliteOperationalStore(connection, clock);
        var operations = new SupportTicketOperations(store);

        var operationalManifest = SupportTicketOperationsManifest.Create();
        var validator = new ProviderCapabilityValidator();
        var fit = new OperationalFitReport(
            validator.Evaluate(operationalManifest, SupportTicketOperationsManifest.OperationalProvider()),
            validator.Evaluate(operationalManifest, SupportTicketOperationsManifest.DocumentOnlyProvider()));

        return (operations, fit);
    }

    private static async Task<(IInboxStore Inbox, ExternalModuleFitReport Fit)> CreateExternalModulesAsync(
        List<IAsyncDisposable> disposables,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        disposables.Insert(0, connection);
        await connection.OpenAsync(cancellationToken);
        await new SqliteInboxMaterializer(connection).MaterializeAsync(cancellationToken);

        return (new SqliteInboxStore(connection), SupportTicketExternalModuleManifest.EvaluateInboxFit());
    }

    private static ProviderIdentity Provider(string name) => new(name, "1.0.0");
}
