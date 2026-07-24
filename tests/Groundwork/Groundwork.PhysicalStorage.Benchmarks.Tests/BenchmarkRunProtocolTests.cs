using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkRunProtocolTests
{
    [Fact]
    public void Scheduled_plan_expands_ratified_scales_and_explicit_dimensions_into_independent_workers()
    {
        var request = new BenchmarkRunRequest(
            Environment.CurrentDirectory,
            BenchmarkProfiles.Scheduled with
            {
                Providers = [BenchmarkProvider.Sqlite],
                StorageForms = [PhysicalStorageForm.PhysicalEntityTable]
            },
            [BenchmarkWorkload.IndexedQuery],
            null,
            null,
            AllowContainers: false,
            RegressionConfirmationRun: false,
            new BenchmarkMatrixDimensions(
                BenchmarkProfiles.RatifiedDatasetSizes,
                PayloadPaddingBytes: [0, 1_024],
                QuerySelectivityBasisPoints: [1_000, 5_000],
                IndependentRuns: BenchmarkProfiles.ScheduledDimensions.IndependentRuns));

        var invocations = BenchmarkRunProtocol.CreateInvocations(request, "group-a");

        Assert.Equal(3 * 2 * 2 * (1 + 3), invocations.Count);
        Assert.Equal(Enumerable.Range(1, invocations.Count), invocations.Select(invocation => invocation.Ordinal));
        Assert.All(invocations, invocation =>
        {
            Assert.Equal(BenchmarkRunProtocol.ProtocolVersion, invocation.ProtocolVersion);
            Assert.Equal("group-a", invocation.RunGroupId);
            Assert.Single(invocation.Request.Configuration.Providers);
            Assert.Single(invocation.Request.Configuration.StorageForms);
            Assert.Single(invocation.Request.Workloads);
            Assert.NotNull(invocation.Request.DataShape);
            Assert.Null(invocation.Request.Dimensions);
            Assert.Equal(invocation.Role, invocation.Request.Role);
        });
        Assert.Equal(3 * 2 * 2, invocations.Count(invocation => invocation.Role == BenchmarkExecutionRole.UntimedWarmup));
        Assert.Equal(3 * 2 * 2 * 3, invocations.Count(invocation => invocation.Role == BenchmarkExecutionRole.Measured));
        Assert.All(
            invocations.Where(invocation => invocation.Role == BenchmarkExecutionRole.UntimedWarmup),
            invocation => Assert.Equal(0, invocation.IndependentRun));
        Assert.All(
            invocations.Chunk(4),
            workers =>
            {
                Assert.Equal(BenchmarkExecutionRole.UntimedWarmup, workers[0].Role);
                Assert.Equal(0, workers[0].IndependentRun);
                Assert.Collection(
                    workers.Skip(1),
                    worker =>
                    {
                        Assert.Equal(BenchmarkExecutionRole.Measured, worker.Role);
                        Assert.Equal(1, worker.IndependentRun);
                    },
                    worker =>
                    {
                        Assert.Equal(BenchmarkExecutionRole.Measured, worker.Role);
                        Assert.Equal(2, worker.IndependentRun);
                    },
                    worker =>
                    {
                        Assert.Equal(BenchmarkExecutionRole.Measured, worker.Role);
                        Assert.Equal(3, worker.IndependentRun);
                    });
            });
        Assert.Equal(
            BenchmarkProfiles.RatifiedDatasetSizes,
            invocations.Select(invocation => invocation.Request.DataShape!.DatasetSize).Distinct());
    }

    [Fact]
    public void Data_shape_rejects_ambiguous_or_unbounded_selectivity()
    {
        Assert.Throws<InvalidOperationException>(() => new BenchmarkDataShape(1_000, -1, 5_000).Validate());
        Assert.Throws<InvalidOperationException>(() => new BenchmarkDataShape(1_000, 0, 0).Validate());
        Assert.Throws<InvalidOperationException>(() => new BenchmarkDataShape(1_000, 0, 10_000).Validate());
    }

    [Theory]
    [InlineData(1_000, 1_000, 100)]
    [InlineData(1_000, 5_000, 500)]
    [InlineData(100_000, 1_000, 10_000)]
    public void Data_shape_distributes_basis_point_selectivity_at_every_ratified_scale(
        int datasetSize,
        int selectivityBasisPoints,
        int expectedSelected)
    {
        var shape = new BenchmarkDataShape(datasetSize, 0, selectivityBasisPoints);

        var selected = Enumerable.Range(0, datasetSize).Count(shape.IsSelectedDocument);

        Assert.Equal(expectedSelected, shape.GetSelectedDocumentCount());
        Assert.Equal(expectedSelected, selected);
    }

    [Fact]
    public void Worker_rejects_an_envelope_that_disagrees_with_the_embedded_execution_role()
    {
        var request = new BenchmarkRunRequest(
            Environment.CurrentDirectory,
            BenchmarkProfiles.Smoke with
            {
                Providers = [BenchmarkProvider.Sqlite],
                StorageForms = [PhysicalStorageForm.SharedDocuments]
            },
            [BenchmarkWorkload.IndexedQuery],
            "artifacts/worker",
            null,
            AllowContainers: false,
            RegressionConfirmationRun: false,
            DataShape: new BenchmarkDataShape(250, 0, 5_000),
            IndependentRun: 1,
            Role: BenchmarkExecutionRole.Measured);
        var invocation = new BenchmarkWorkerInvocation(
            BenchmarkRunProtocol.ProtocolVersion,
            "group-a",
            Ordinal: 1,
            IndependentRun: 0,
            BenchmarkExecutionRole.UntimedWarmup,
            request)
        {
            ExpectedGitCommit = "test-commit",
            ExpectedGitTreeDigest = new('a', 64)
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BenchmarkRunProtocol.ValidateInvocation(invocation));

        Assert.Contains("role", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("group")]
    [InlineData("ordinal")]
    [InlineData("role")]
    public void Coordinator_rejects_a_response_that_does_not_match_its_worker_invocation(string mismatch)
    {
        var invocation = MeasuredInvocation();
        var response = new BenchmarkWorkerResponse(
            BenchmarkRunProtocol.ProtocolVersion,
            invocation.RunGroupId,
            invocation.Ordinal,
            invocation.Role,
            Succeeded: true,
            RunDirectory: "runs/000001",
            ConsumerEvidence: "runs/000001/reports/consumer-evidence.json",
            FailureType: null)
        {
            GitCommit = invocation.ExpectedGitCommit,
            GitTreeDigest = invocation.ExpectedGitTreeDigest,
            Artifacts = TestArtifacts()
        };
        response = mismatch switch
        {
            "protocol" => response with { ProtocolVersion = "unsupported" },
            "group" => response with { RunGroupId = "other-group" },
            "ordinal" => response with { Ordinal = 2 },
            "role" => response with { Role = BenchmarkExecutionRole.UntimedWarmup },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BenchmarkSubprocessCoordinator.ValidateResponse(invocation, response));

        Assert.Contains(mismatch, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BenchmarkExecutionRole.Measured, null)]
    [InlineData(BenchmarkExecutionRole.UntimedWarmup, "runs/000001/reports/consumer-evidence.json")]
    public void Coordinator_rejects_response_evidence_that_disagrees_with_the_execution_role(
        BenchmarkExecutionRole role,
        string? consumerEvidence)
    {
        var measured = MeasuredInvocation();
        var invocation = role == BenchmarkExecutionRole.Measured
            ? measured
            : measured with
            {
                IndependentRun = 0,
                Role = BenchmarkExecutionRole.UntimedWarmup,
                Request = measured.Request with
                {
                    IndependentRun = 0,
                    Role = BenchmarkExecutionRole.UntimedWarmup
                }
            };
        var response = new BenchmarkWorkerResponse(
            BenchmarkRunProtocol.ProtocolVersion,
            invocation.RunGroupId,
            invocation.Ordinal,
            invocation.Role,
            Succeeded: true,
            RunDirectory: "runs/000001",
            ConsumerEvidence: consumerEvidence,
            FailureType: null)
        {
            GitCommit = invocation.ExpectedGitCommit,
            GitTreeDigest = invocation.ExpectedGitTreeDigest,
            Artifacts = TestArtifacts()
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BenchmarkSubprocessCoordinator.ValidateResponse(invocation, response));

        Assert.Contains("consumer evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static BenchmarkWorkerInvocation MeasuredInvocation()
    {
        var request = new BenchmarkRunRequest(
            Environment.CurrentDirectory,
            BenchmarkProfiles.Smoke with
            {
                Providers = [BenchmarkProvider.Sqlite],
                StorageForms = [PhysicalStorageForm.SharedDocuments]
            },
            [BenchmarkWorkload.IndexedQuery],
            "artifacts/worker",
            null,
            AllowContainers: false,
            RegressionConfirmationRun: false,
            DataShape: new BenchmarkDataShape(250, 0, 5_000),
            IndependentRun: 1,
            Role: BenchmarkExecutionRole.Measured);
        return new BenchmarkWorkerInvocation(
            BenchmarkRunProtocol.ProtocolVersion,
            "group-a",
            Ordinal: 1,
            IndependentRun: 1,
            BenchmarkExecutionRole.Measured,
            request)
        {
            ExpectedGitCommit = "test-commit",
            ExpectedGitTreeDigest = new('a', 64)
        };
    }

    private static BenchmarkWorkerArtifactDigests TestArtifacts() => new(
        "manifest.json",
        new('a', 64),
        "reports/elsa-migration-evidence.json",
        new('b', 64),
        "reports/consumer-evidence.json",
        new('c', 64));
}
