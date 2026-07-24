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
    ClientResetPointReadBatch,
    ReusedClientPointReadBatch,
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
    ClientRestartValidation,
    StorageGrowth
}

public enum BenchmarkRunMode
{
    Smoke,
    Scheduled
}

public enum BenchmarkExecutionRole
{
    UntimedWarmup,
    Measured
}

public sealed record BenchmarkDataShape(
    int DatasetSize,
    int PayloadPaddingBytes,
    int QuerySelectivityBasisPoints)
{
    public string Identity =>
        $"n{DatasetSize}-payload{PayloadPaddingBytes}-selectivity{QuerySelectivityBasisPoints}bp";

    public int GetSelectedDocumentCount()
    {
        Validate();
        return checked((int)(((long)DatasetSize * QuerySelectivityBasisPoints + 9_999) / 10_000));
    }

    public bool IsSelectedDocument(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0 || zeroBasedIndex >= DatasetSize)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        return (long)zeroBasedIndex * 10_000 / DatasetSize < QuerySelectivityBasisPoints;
    }

    public void Validate()
    {
        if (DatasetSize <= 0)
            throw new InvalidOperationException("The benchmark dataset size must be positive.");
        if (PayloadPaddingBytes < 0)
            throw new InvalidOperationException("The benchmark payload padding cannot be negative.");
        if (QuerySelectivityBasisPoints is <= 0 or >= 10_000)
        {
            throw new InvalidOperationException(
                "Query selectivity must be between 1 and 9,999 basis points so selection workloads remain observable.");
        }
    }
}

public sealed record BenchmarkMatrixDimensions(
    IReadOnlyList<int> DatasetSizes,
    IReadOnlyList<int> PayloadPaddingBytes,
    IReadOnlyList<int> QuerySelectivityBasisPoints,
    int IndependentRuns)
{
    public IReadOnlyList<BenchmarkDataShape> CreateShapes()
    {
        Validate();
        return DatasetSizes
            .SelectMany(size => PayloadPaddingBytes.SelectMany(payload =>
                QuerySelectivityBasisPoints.Select(selectivity =>
                    new BenchmarkDataShape(size, payload, selectivity))))
            .ToArray();
    }

    public void Validate()
    {
        if (DatasetSizes.Count == 0 || PayloadPaddingBytes.Count == 0 || QuerySelectivityBasisPoints.Count == 0)
            throw new InvalidOperationException("Dataset, payload, and selectivity dimensions must all contain values.");
        if (IndependentRuns <= 0)
            throw new InvalidOperationException("At least one independent run is required.");
        if (DatasetSizes.Distinct().Count() != DatasetSizes.Count ||
            PayloadPaddingBytes.Distinct().Count() != PayloadPaddingBytes.Count ||
            QuerySelectivityBasisPoints.Distinct().Count() != QuerySelectivityBasisPoints.Count)
            throw new InvalidOperationException("Benchmark matrix dimensions cannot contain duplicate values.");
        foreach (var shape in CreateUncheckedShapes())
            shape.Validate();
    }

    private IEnumerable<BenchmarkDataShape> CreateUncheckedShapes() =>
        DatasetSizes.SelectMany(size => PayloadPaddingBytes.SelectMany(payload =>
            QuerySelectivityBasisPoints.Select(selectivity => new BenchmarkDataShape(size, payload, selectivity))));
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
    public BenchmarkDataShape? DataShape { get; init; }
    public int MinimumMeasuredOperations { get; init; } = 1;
    public int MinimumSteadyStateDurationSeconds { get; init; }

    public void Validate()
    {
        if (SchemaVersion != BenchmarkProfiles.SchemaVersion)
            throw new InvalidOperationException($"Unsupported benchmark schema '{SchemaVersion}'.");
        if (DatasetSize <= 0 || MigrationDatasetSize <= 0)
            throw new InvalidOperationException("Dataset sizes must be positive.");
        if (WarmupIterations < 1 || MeasurementIterations < 5 || OperationsPerIteration < 1 || Concurrency < 1)
            throw new InvalidOperationException("Warmup, measurement, operation, and concurrency settings are below the reproducibility floor.");
        if (MinimumMeasuredOperations < 1 || MinimumSteadyStateDurationSeconds < 0)
            throw new InvalidOperationException("Measured operation and steady-state duration floors cannot be negative or empty.");
        if (Mode == BenchmarkRunMode.Scheduled &&
            (MinimumMeasuredOperations < 100 || MinimumSteadyStateDurationSeconds < 30))
        {
            throw new InvalidOperationException(
                "Scheduled workers require at least 100 measured operations and 30 seconds of steady-state execution.");
        }
        if (Providers.Count == 0 || StorageForms.Count == 0)
            throw new InvalidOperationException("At least one provider and storage form must be selected.");
        if (DataShape is not null)
        {
            DataShape.Validate();
            if (DataShape.DatasetSize != DatasetSize)
                throw new InvalidOperationException("The execution data shape must match the fixed dataset-size control.");
        }
    }
}

public static class BenchmarkProfiles
{
    public const string SchemaVersion = "groundwork.physical-storage.benchmark/v1";
    public const int ReproducibleSeed = 20260713;
    public static IReadOnlyList<int> RatifiedDatasetSizes { get; } = [1_000, 100_000, 1_000_000];
    public static BenchmarkMatrixDimensions SmokeDimensions { get; } = new(
        DatasetSizes: [250],
        PayloadPaddingBytes: [0],
        QuerySelectivityBasisPoints: [5_000],
        IndependentRuns: 1);
    public static BenchmarkMatrixDimensions ScheduledDimensions { get; } = new(
        RatifiedDatasetSizes,
        PayloadPaddingBytes: [0],
        QuerySelectivityBasisPoints: [5_000],
        IndependentRuns: 3);

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
        StorageForms: Enum.GetValues<PhysicalStorageForm>())
    {
        MinimumMeasuredOperations = 1,
        MinimumSteadyStateDurationSeconds = 0
    };

    public static BenchmarkRunConfiguration Scheduled { get; } = new(
        SchemaVersion,
        BenchmarkRunMode.Scheduled,
        ReproducibleSeed,
        DatasetSize: 1_000,
        MigrationDatasetSize: 5_000,
        WarmupIterations: 5,
        MeasurementIterations: 30,
        OperationsPerIteration: 100,
        Concurrency: 16,
        Providers: Enum.GetValues<BenchmarkProvider>(),
        StorageForms: Enum.GetValues<PhysicalStorageForm>())
    {
        MinimumMeasuredOperations = 100,
        MinimumSteadyStateDurationSeconds = 30
    };
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
