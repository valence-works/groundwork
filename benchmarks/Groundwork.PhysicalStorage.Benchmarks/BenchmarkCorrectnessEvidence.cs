using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Documents.Store;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkObservableResult(
    int Sequence,
    string Identity,
    string Status,
    long? Version,
    long Count,
    string? Payload);

public sealed class BenchmarkObservableResultVector
{
    public const string ContractVersion = "groundwork.physical-storage.observable-result/v1";

    private BenchmarkObservableResultVector(IReadOnlyList<BenchmarkObservableResult> results)
    {
        Results = results;
    }

    public IReadOnlyList<BenchmarkObservableResult> Results { get; }

    public string Digest
    {
        get
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    Contract = ContractVersion,
                    Results
                },
                BenchmarkJson.CompactOptions);
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
    }

    public static BenchmarkObservableResultVector Create(
        IEnumerable<BenchmarkObservableResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var canonical = results.ToArray();
        if (canonical.Length == 0)
            throw new ArgumentException("An observable result vector cannot be empty.", nameof(results));

        for (var index = 0; index < canonical.Length; index++)
        {
            var item = canonical[index];
            if (item.Sequence != index)
            {
                throw new ArgumentException(
                    $"Observable result sequence {item.Sequence} does not match canonical position {index}.",
                    nameof(results));
            }
            if (string.IsNullOrWhiteSpace(item.Identity))
                throw new ArgumentException("Observable result identities cannot be empty.", nameof(results));
            if (string.IsNullOrWhiteSpace(item.Status))
                throw new ArgumentException("Observable result statuses cannot be empty.", nameof(results));
            if (item.Count < 0)
                throw new ArgumentException("Observable result counts cannot be negative.", nameof(results));
        }

        return new BenchmarkObservableResultVector(Array.AsReadOnly(canonical));
    }

    /// <summary>
    /// Retains every measured iteration in a deterministic, iteration-aware result
    /// contract. This is deliberately not a set or a first-vector cache: pagination
    /// observes different deterministic pages on successive iterations.
    /// </summary>
    public static BenchmarkObservableResultVector AggregateIterations(
        IEnumerable<(int Iteration, BenchmarkObservableResultVector Vector)> iterations)
    {
        ArgumentNullException.ThrowIfNull(iterations);
        var canonical = iterations
            .OrderBy(item => item.Iteration)
            .SelectMany(item => item.Vector.Results.Select(result => new BenchmarkObservableResult(
                0,
                $"iteration-{item.Iteration:D6}/{result.Identity}",
                result.Status,
                result.Version,
                result.Count,
                result.Payload)))
            .Select((result, sequence) => result with { Sequence = sequence })
            .ToArray();
        if (canonical.Length == 0)
            throw new ArgumentException("Measured observable iteration evidence cannot be empty.", nameof(iterations));
        return Create(canonical);
    }
}

internal sealed class BenchmarkObservableResultBuilder
{
    private readonly List<BenchmarkObservableResult> results = [];

    public void Add(
        string identity,
        string status,
        long? version,
        long count,
        string? payload) =>
        results.Add(new BenchmarkObservableResult(
            results.Count,
            identity,
            status,
            version,
            count,
            payload));

    public void AddDocument(
        DocumentEnvelope document,
        string status,
        string? canonicalIdentity = null,
        long count = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        Add(
            canonicalIdentity ?? document.Id,
            status,
            document.Version,
            count,
            document.ContentJson);
    }

    public BenchmarkObservableResultVector Build() =>
        BenchmarkObservableResultVector.Create(results);
}
