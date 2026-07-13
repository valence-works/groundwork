using System.Collections.Concurrent;
using System.Diagnostics;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record DatabaseSignalSnapshot(long CommandStarts, long ClientActivities)
{
    public long? ObservableRoundTrips => CommandStarts > 0
        ? CommandStarts
        : ClientActivities > 0 ? ClientActivities : null;

    public IReadOnlyDictionary<string, long> ToProviderWork() => new Dictionary<string, long>
    {
        ["diagnostic_command_starts"] = CommandStarts,
        ["database_client_activities"] = ClientActivities,
        ["round_trips_observable"] = ObservableRoundTrips.HasValue ? 1 : 0,
        ["round_trip_signal_is_diagnostic_command"] = CommandStarts > 0 ? 1 : 0,
        ["round_trip_signal_is_client_activity"] = CommandStarts == 0 && ClientActivities > 0 ? 1 : 0
    };
}

public sealed class DatabaseSignalCollector :
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly ConcurrentBag<IDisposable> subscriptions = [];
    private readonly IDisposable allListenersSubscription;
    private readonly ActivityListener activityListener;
    private long commandStarts;
    private long clientActivities;

    public DatabaseSignalCollector()
    {
        allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        activityListener = new ActivityListener
        {
            ShouldListenTo = source => IsDatabaseSource(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.Kind == ActivityKind.Client && IsDatabaseSource(activity.Source.Name))
                    Volatile.Read(ref Current)?.IncrementActivity();
            }
        };
        ActivitySource.AddActivityListener(activityListener);
    }

    private static DatabaseSignalCollector? Current;

    public MeasurementScope BeginMeasurement()
    {
        Volatile.Write(ref Current, this);
        return new MeasurementScope(this, Interlocked.Read(ref commandStarts), Interlocked.Read(ref clientActivities));
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (IsDatabaseSource(listener.Name))
            subscriptions.Add(listener.Subscribe(this, IsCommandStartEvent));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (IsCommandStartEvent(value.Key))
            Interlocked.Increment(ref commandStarts);
    }

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        Volatile.Write(ref Current, null);
        activityListener.Dispose();
        allListenersSubscription.Dispose();
        while (subscriptions.TryTake(out var subscription))
            subscription.Dispose();
    }

    public static bool IsCommandStartEvent(string eventName) =>
        eventName.Contains("Command", StringComparison.OrdinalIgnoreCase) &&
        (eventName.EndsWith("Before", StringComparison.OrdinalIgnoreCase) ||
         eventName.EndsWith("Start", StringComparison.OrdinalIgnoreCase) ||
         eventName.EndsWith("Started", StringComparison.OrdinalIgnoreCase));

    private static bool IsDatabaseSource(string source) =>
        source.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("MongoDB", StringComparison.OrdinalIgnoreCase);

    private void IncrementActivity() => Interlocked.Increment(ref clientActivities);

    public sealed class MeasurementScope : IDisposable
    {
        private readonly DatabaseSignalCollector owner;
        private readonly long commandStarts;
        private readonly long clientActivities;
        private int disposed;

        internal MeasurementScope(DatabaseSignalCollector owner, long commandStarts, long clientActivities)
        {
            this.owner = owner;
            this.commandStarts = commandStarts;
            this.clientActivities = clientActivities;
        }

        public DatabaseSignalSnapshot Complete()
        {
            Dispose();
            return new DatabaseSignalSnapshot(
                Interlocked.Read(ref owner.commandStarts) - commandStarts,
                Interlocked.Read(ref owner.clientActivities) - clientActivities);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Volatile.Write(ref Current, null);
        }
    }
}
