using System.Data.Common;
using Groundwork.Provider.Relational;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalSessionPoolPressure
{
    public static async Task AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(Func<DbConnection> createConnection)
    {
        var sessions = RelationalSessionFactory.Concurrent(createConnection);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        async Task<bool> HoldPooledConnection(DbConnection _, CancellationToken __)
        {
            if (Interlocked.Increment(ref entered) == 2)
                twoEntered.SetResult();
            await release.Task;
            return true;
        }

        var first = sessions.ExecuteAsync(HoldPooledConnection);
        var second = sessions.ExecuteAsync(HoldPooledConnection);
        try
        {
            await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using var cancellation = new CancellationTokenSource();
            var third = sessions.ExecuteAsync((_, _) =>
            {
                thirdEntered.TrySetResult();
                return Task.FromResult(true);
            }, cancellation.Token);
            await Task.Delay(250);

            Assert.False(thirdEntered.Task.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
    }
}
