namespace Groundwork.Provider.Relational;

internal static class RelationalCleanupFailures
{
    internal const string DataKey = "Groundwork.Relational.CleanupFailures";

    public static void Attach(Exception primary, Exception cleanup)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (primary.Data[DataKey] is List<Exception> failures)
        {
            failures.Add(cleanup);
            return;
        }

        primary.Data[DataKey] = new List<Exception> { cleanup };
    }
}
