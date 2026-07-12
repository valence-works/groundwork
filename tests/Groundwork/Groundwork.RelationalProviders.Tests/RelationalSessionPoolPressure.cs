using System.Data.Common;
using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

internal static class RelationalSessionPoolPressure
{
    public static Task<IAsyncDisposable> BlockSqlServerDocumentsAsync(string connectionString) =>
        BlockDocumentsAsync(
            new Microsoft.Data.SqlClient.SqlConnection(connectionString),
            "SELECT COUNT(*) FROM groundwork_documents WITH (TABLOCKX, HOLDLOCK);");

    public static Task<IAsyncDisposable> BlockPostgreSqlDocumentsAsync(string connectionString) =>
        BlockDocumentsAsync(
            new Npgsql.NpgsqlConnection(connectionString),
            "LOCK TABLE groundwork_documents IN ACCESS EXCLUSIVE MODE;");

    public static async Task AssertTwoOperationsRunWhileThirdWaitsForProviderPoolAsync(
        IDocumentStore store,
        Task twoConnectionsOpened,
        IAsyncDisposable blocker)
    {
        var first = store.LoadAsync("configurationDocument", "pool-pressure-1");
        var second = store.LoadAsync("configurationDocument", "pool-pressure-2");
        try
        {
            await twoConnectionsOpened.WaitAsync(TimeSpan.FromSeconds(10));

            using var cancellation = new CancellationTokenSource();
            var third = store.LoadAsync("configurationDocument", "pool-pressure-3", cancellation.Token);
            await Task.Delay(250);

            Assert.False(third.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        await Task.WhenAll(first, second);
    }

    private static async Task<IAsyncDisposable> BlockDocumentsAsync(DbConnection connection, string commandText)
    {
        DbTransaction? transaction = null;
        try
        {
            await connection.OpenAsync();
            transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
            return new TransactionBlocker(connection, transaction);
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class TransactionBlocker(DbConnection connection, DbTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
