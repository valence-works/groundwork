using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Documents.Store;

/// <summary>Stable kinds for the ordered selector commands in one bounded-mutation invocation.</summary>
public enum PhysicalDocumentMutationCommandKind
{
    Selection,
    CandidateDiscovery,
    PredicateRecheck
}

/// <summary>Stable command identities shared by relational mutation runtimes and diagnostic consumers.</summary>
public static class PhysicalDocumentMutationCommandIdentities
{
    public const string Selection = "selection";
    public const string CandidateDiscovery = "candidate-discovery";
    public const string PredicateRecheck = "predicate-recheck";
}

/// <summary>One physical selector whose declared index was observed in a provider-native mutation plan.</summary>
public sealed class PhysicalDocumentMutationSelectorEvidence
{
    public PhysicalDocumentMutationSelectorEvidence(
        ExecutableStorageObjectRole target,
        ProviderPhysicalObjectName storageObject,
        ProviderPhysicalObjectName? index,
        string observedStorageObjectIdentifier,
        string observedIndexIdentifier)
    {
        Target = target;
        StorageObject = storageObject ?? throw new ArgumentNullException(nameof(storageObject));
        Index = index;
        ObservedStorageObjectIdentifier = PhysicalDocumentQueryExplanation.RequireValue(
            observedStorageObjectIdentifier,
            nameof(observedStorageObjectIdentifier));
        ObservedIndexIdentifier = PhysicalDocumentQueryExplanation.RequireValue(
            observedIndexIdentifier,
            nameof(observedIndexIdentifier));
    }

    public ExecutableStorageObjectRole Target { get; }
    public ProviderPhysicalObjectName StorageObject { get; }
    public ProviderPhysicalObjectName? Index { get; }

    /// <summary>The storage-object identifier read from provider-native evidence.</summary>
    public string ObservedStorageObjectIdentifier { get; }

    /// <summary>The index identifier read from the provider-native plan, never inferred from the declaration.</summary>
    public string ObservedIndexIdentifier { get; }
}

/// <summary>Provider-native evidence for one exact selector command in a bounded mutation.</summary>
public sealed class PhysicalDocumentMutationCommandExplanation
{
    public PhysicalDocumentMutationCommandExplanation(
        PhysicalDocumentMutationCommandKind kind,
        string identity,
        string nativePlanFormat,
        string nativePlan,
        IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> selectors,
        string? renderedCommand = null,
        long? preparedRestrictionRowCount = null)
    {
        if (preparedRestrictionRowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(preparedRestrictionRowCount));
        Kind = kind;
        Identity = PhysicalDocumentQueryExplanation.RequireValue(identity, nameof(identity));
        NativePlanFormat = PhysicalDocumentQueryExplanation.RequireValue(nativePlanFormat, nameof(nativePlanFormat));
        NativePlan = PhysicalDocumentQueryExplanation.RequireValue(nativePlan, nameof(nativePlan));
        RenderedCommand = string.IsNullOrWhiteSpace(renderedCommand) ? null : renderedCommand;
        PreparedRestrictionRowCount = preparedRestrictionRowCount;
        ArgumentNullException.ThrowIfNull(selectors);
        Selectors = Array.AsReadOnly(selectors.ToArray());
    }

    public PhysicalDocumentMutationCommandKind Kind { get; }
    public string Identity { get; }
    public string NativePlanFormat { get; }

    /// <summary>Unsanitized provider-native plan output. Treat as sensitive diagnostic data.</summary>
    public string NativePlan { get; }

    /// <summary>The exact provider command submitted for planning.</summary>
    public string? RenderedCommand { get; }

    /// <summary>
    /// Number of rows prepared in the command's transaction-local restriction before planning.
    /// Null when the command has no prepared restriction.
    /// </summary>
    public long? PreparedRestrictionRowCount { get; }

    public IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> Selectors { get; }
}

/// <summary>
/// Provider-native diagnostic evidence for one admitted bounded mutation. Native output is preserved
/// verbatim and can contain values, physical names, or other sensitive provider metadata.
/// </summary>
public sealed class PhysicalDocumentMutationExplanation
{
    public PhysicalDocumentMutationExplanation(
        PhysicalMutationPlan plan,
        string runtimeInvocationFingerprint,
        string nativePlanFormat,
        string nativePlan,
        IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> selectors,
        string? renderedCommand = null)
        : this(
            plan,
            runtimeInvocationFingerprint,
            [
                new PhysicalDocumentMutationCommandExplanation(
                    PhysicalDocumentMutationCommandKind.Selection,
                    PhysicalDocumentMutationCommandIdentities.Selection,
                    nativePlanFormat,
                    nativePlan,
                    selectors,
                    renderedCommand)
            ])
    {
    }

    public PhysicalDocumentMutationExplanation(
        PhysicalMutationPlan plan,
        string runtimeInvocationFingerprint,
        IReadOnlyList<PhysicalDocumentMutationCommandExplanation> commands)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        RuntimeInvocationFingerprint = PhysicalDocumentQueryExplanation.RequireValue(
            runtimeInvocationFingerprint,
            nameof(runtimeInvocationFingerprint));
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("At least one explained mutation command is required.", nameof(commands));
        Commands = Array.AsReadOnly(commands.ToArray());
        NativePlanFormat = commands.Count == 1 ? commands[0].NativePlanFormat : "multiple-native-plans";
        NativePlan = commands.Count == 1
            ? commands[0].NativePlan
            : string.Join(
                Environment.NewLine,
                commands.Select(command => $"[{command.Identity}]{Environment.NewLine}{command.NativePlan}"));
        RenderedCommand = commands.Count == 1
            ? commands[0].RenderedCommand
            : string.Join(
                Environment.NewLine,
                commands.Select(command => $"[{command.Identity}]{Environment.NewLine}{command.RenderedCommand}"));
        Selectors = Array.AsReadOnly(commands
            .SelectMany(command => command.Selectors)
            .DistinctBy(selector => selector.Target)
            .ToArray());
    }

    public PhysicalMutationPlan Plan { get; }
    public string RuntimeInvocationFingerprint { get; }

    /// <summary>Planned production selector stages in execution order.</summary>
    public IReadOnlyList<PhysicalDocumentMutationCommandExplanation> Commands { get; }

    /// <summary>Compatibility aggregate. Inspect <see cref="Commands"/> for exact per-command formats.</summary>
    public string NativePlanFormat { get; }

    /// <summary>Compatibility aggregate of unsanitized command plans.</summary>
    public string NativePlan { get; }

    /// <summary>
    /// Compatibility aggregate of exact provider commands. Inspect <see cref="Commands"/> for
    /// command boundaries.
    /// </summary>
    public string? RenderedCommand { get; }

    /// <summary>Compatibility union of selector roles. Inspect <see cref="Commands"/> for stage-local proof.</summary>
    public IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> Selectors { get; }
}

/// <summary>Resolves the admitted immutable mutation plan without issuing provider I/O.</summary>
public interface IPhysicalDocumentMutationInspector
{
    PhysicalMutationPlan ResolvePlan(DocumentMutation mutation);
}

/// <summary>
/// Explains every exact selector stage of the admitted mutation through its provider's native
/// planner. Explanation fails closed when the provider cannot return plan evidence or a stage does
/// not show its exact certified physical targets and indexes.
/// </summary>
public interface IPhysicalDocumentMutationExplainer : IBoundedDocumentMutationStore, IPhysicalDocumentMutationInspector
{
    Task<PhysicalDocumentMutationExplanation> ExplainAsync(
        DocumentMutation mutation,
        CancellationToken cancellationToken = default);
}

public delegate Task<PhysicalDocumentMutationExplanation> PhysicalDocumentMutationExplainExecutor(
    DocumentMutation mutation,
    PhysicalMutationPlan plan,
    CancellationToken cancellationToken);

public delegate string PhysicalDocumentMutationInvocationFingerprintResolver(
    DocumentMutation mutation,
    PhysicalMutationPlan plan);
