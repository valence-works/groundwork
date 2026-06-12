namespace Groundwork.Core.Intents;

public sealed record StorageIntent(
    StorageIntentKind Kind,
    IReadOnlySet<StorageRequirement> Requirements,
    string? Rationale)
{
    public static StorageIntent PortableDocument() =>
        new(StorageIntentKind.PortableDocument, new HashSet<StorageRequirement>(), null);

    public static StorageIntent BenchmarkGated(string rationale, params StorageRequirement[] requirements) =>
        new(StorageIntentKind.BenchmarkGated, requirements.ToHashSet(), rationale);

    public static StorageIntent SpecializedProvider(string rationale, params StorageRequirement[] requirements) =>
        new(StorageIntentKind.SpecializedProvider, requirements.ToHashSet(), rationale);
}

public enum StorageIntentKind
{
    PortableDocument,
    BenchmarkGated,
    SpecializedProvider
}

public enum StorageRequirement
{
    AtomicClaim,
    LeaseRecovery,
    OrderedConsumption,
    RetryRecovery,
    Idempotency,
    RetentionPolicy,
    AtomicCommit,
    ConcurrencyEvidence,
    OperationalDiagnostics
}
