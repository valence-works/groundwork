namespace Groundwork.DiagnosticRecords.Relational;

internal sealed class RelationalDiagnosticRecordAdmission(
    Func<CancellationToken, Task> admitAsync)
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private bool admitted;

    public async ValueTask EnsureAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref admitted))
            return;

        await mutex.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref admitted))
                return;
            await admitAsync(cancellationToken);
            Volatile.Write(ref admitted, true);
        }
        finally
        {
            mutex.Release();
        }
    }
}
