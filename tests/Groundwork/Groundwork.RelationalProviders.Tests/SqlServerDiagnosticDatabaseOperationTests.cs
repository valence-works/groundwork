using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class SqlServerDiagnosticDatabaseOperationTests
{
    [Fact]
    public async Task Timeout_reports_database_operation_and_server_session_diagnostics()
    {
        var exception = await Assert.ThrowsAsync<SqlServerDiagnosticDatabaseOperationException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_timeout",
                "READ_COMMITTED_SNAPSHOT configuration",
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20),
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                () => ValueTask.CompletedTask,
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
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20),
                token => Task.FromCanceled(token),
                () => ValueTask.CompletedTask,
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
        var exception = await Assert.ThrowsAsync<SqlServerDiagnosticDatabaseOperationException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_failure",
                "drop",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20),
                _ => Task.FromException(new IOException("drop failed")),
                () => ValueTask.CompletedTask,
                _ => Task.FromException<string>(new InvalidOperationException("diagnostics failed")),
                CancellationToken.None));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("diagnostics unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostics failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_cooperative_execution_is_aborted_and_observed_at_the_deadline()
    {
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortCalls = 0;

        var exception = await Assert.ThrowsAsync<SqlServerDiagnosticDatabaseOperationException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_non_cooperative",
                "creation",
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20),
                _ => releaseExecution.Task,
                () =>
                {
                    Interlocked.Increment(ref abortCalls);
                    releaseExecution.TrySetCanceled();
                    return ValueTask.CompletedTask;
                },
                _ => Task.FromResult("session=91,status=rollback"),
                CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, abortCalls);
        Assert.Contains("groundwork_diagnostics_non_cooperative", exception.Message, StringComparison.Ordinal);
        Assert.Contains("session=91", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_cooperative_diagnostics_have_an_independent_hard_deadline()
    {
        var diagnostics = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<SqlServerDiagnosticDatabaseOperationException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_stuck_diagnostics",
                "drop",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20),
                _ => Task.FromException(new IOException("drop failed")),
                () => ValueTask.CompletedTask,
                _ => diagnostics.Task,
                CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("diagnostics unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("deadline", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operation_that_cannot_be_aborted_is_reported_as_not_quiesced_within_a_hard_deadline()
    {
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<SqlServerDiagnosticDatabaseOperationException>(() =>
            SqlServerDiagnosticDatabaseOperation.ExecuteAsync(
                "groundwork_diagnostics_poisoned",
                "READ_COMMITTED_SNAPSHOT configuration",
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
                _ => execution.Task,
                () => new(abort.Task),
                _ => Task.FromResult("session=101,status=rollback"),
                CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(exception.OperationQuiesced);
        Assert.Contains("quiesced=False", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Data.Contains("Groundwork.Tests.SqlServerDiagnosticDatabaseAbortFailure"));
    }
}
