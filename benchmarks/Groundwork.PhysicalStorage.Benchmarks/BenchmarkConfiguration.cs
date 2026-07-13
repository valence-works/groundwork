using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public enum BenchmarkProvider
{
    Sqlite,
    SqlServer,
    PostgreSql,
    MongoDb
}

public enum BenchmarkWorkload
{
    ColdPointRead,
    WarmPointRead,
    IndexedQuery,
    MixedCompoundOrdering,
    Insert,
    Update,
    Delete,
    UnitOfWork,
    ConcurrentCreate,
    OptimisticConcurrency,
    PaginationAndCount,
    BackfillMigration,
    RestartRecovery,
    StorageGrowth
}

public enum BenchmarkRunMode
{
    Smoke,
    Scheduled
}

public sealed record BenchmarkRunConfiguration(
    string SchemaVersion,
    BenchmarkRunMode Mode,
    int Seed,
    int DatasetSize,
    int MigrationDatasetSize,
    int WarmupIterations,
    int MeasurementIterations,
    int OperationsPerIteration,
    int Concurrency,
    IReadOnlyList<BenchmarkProvider> Providers,
    IReadOnlyList<PhysicalStorageForm> StorageForms)
{
    public void Validate()
    {
        if (SchemaVersion != BenchmarkProfiles.SchemaVersion)
            throw new InvalidOperationException($"Unsupported benchmark schema '{SchemaVersion}'.");
        if (DatasetSize <= 0 || MigrationDatasetSize <= 0)
            throw new InvalidOperationException("Dataset sizes must be positive.");
        if (WarmupIterations < 1 || MeasurementIterations < 5 || OperationsPerIteration < 1 || Concurrency < 1)
            throw new InvalidOperationException("Warmup, measurement, operation, and concurrency settings are below the reproducibility floor.");
        if (Providers.Count == 0 || StorageForms.Count == 0)
            throw new InvalidOperationException("At least one provider and storage form must be selected.");
    }
}

public static class BenchmarkProfiles
{
    public const string SchemaVersion = "groundwork.physical-storage.benchmark/v1";
    public const int ReproducibleSeed = 20260713;

    public static BenchmarkRunConfiguration Smoke { get; } = new(
        SchemaVersion,
        BenchmarkRunMode.Smoke,
        ReproducibleSeed,
        DatasetSize: 250,
        MigrationDatasetSize: 100,
        WarmupIterations: 2,
        MeasurementIterations: 7,
        OperationsPerIteration: 10,
        Concurrency: 4,
        Providers: [BenchmarkProvider.Sqlite],
        StorageForms: Enum.GetValues<PhysicalStorageForm>());

    public static BenchmarkRunConfiguration Scheduled { get; } = new(
        SchemaVersion,
        BenchmarkRunMode.Scheduled,
        ReproducibleSeed,
        DatasetSize: 10_000,
        MigrationDatasetSize: 5_000,
        WarmupIterations: 5,
        MeasurementIterations: 30,
        OperationsPerIteration: 100,
        Concurrency: 16,
        Providers: Enum.GetValues<BenchmarkProvider>(),
        StorageForms: Enum.GetValues<PhysicalStorageForm>());
}

public sealed record BenchmarkCase(
    BenchmarkProvider Provider,
    PhysicalStorageForm StorageForm,
    BenchmarkWorkload Workload)
{
    public string Identity => $"{Provider}/{StorageForm}/{Workload}";
}

public static class BenchmarkMatrix
{
    public static IReadOnlyList<BenchmarkCase> Create(BenchmarkRunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        return configuration.Providers
            .SelectMany(provider => configuration.StorageForms.SelectMany(form =>
                Enum.GetValues<BenchmarkWorkload>().Select(workload => new BenchmarkCase(provider, form, workload))))
            .ToArray();
    }
}
