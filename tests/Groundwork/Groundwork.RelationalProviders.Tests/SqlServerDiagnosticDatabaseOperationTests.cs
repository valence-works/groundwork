using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class SqlServerDiagnosticDatabaseOperationTests
{
    [Fact]
    public async Task Timeout_reports_database_operation_and_server_session_diagnostics()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_timeout",
                "READ_COMMITTED_SNAPSHOT configuration",
                TimeSpan.FromMilliseconds(20),
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                _ => Task.FromResult("session=71,status=suspended,wait=LCK_M_X,blocker=54"),
                CancellationToken.None));

        Assert.Contains("groundwork_diagnostics_timeout", exception.Message, StringComparison.Ordinal);
        Assert.Contains("READ_COMMITTED_SNAPSHOT configuration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("session=71", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LCK_M_X", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_without_running_failure_diagnostics()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var diagnosticCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_canceled",
                "creation",
                TimeSpan.FromSeconds(1),
                token => Task.FromCanceled(token),
                _ =>
                {
                    Interlocked.Increment(ref diagnosticCalls);
                    return Task.FromResult("unexpected");
                },
                cancellation.Token));

        Assert.Equal(0, diagnosticCalls);
    }

    [Fact]
    public async Task Diagnostic_failure_cannot_mask_the_database_operation_failure()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_failure",
                "drop",
                TimeSpan.FromSeconds(1),
                _ => Task.FromException(new IOException("drop failed")),
                _ => Task.FromException<string>(new InvalidOperationException("diagnostics failed")),
                CancellationToken.None));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("diagnostics unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostics failed", exception.Message, StringComparison.Ordinal);
    }
}
