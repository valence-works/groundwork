using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Documents.Store;

/// <summary>One physical selector whose declared index was observed in a provider-native mutation plan.</summary>
public sealed class PhysicalDocumentMutationSelectorEvidence
{
    public PhysicalDocumentMutationSelectorEvidence(
        ExecutableStorageObjectRole target,
        ProviderPhysicalObjectName storageObject,
        ProviderPhysicalObjectName index,
        string observedIndexIdentifier)
    {
        Target = target;
        StorageObject = storageObject ?? throw new ArgumentNullException(nameof(storageObject));
        Index = index ?? throw new ArgumentNullException(nameof(index));
        ObservedIndexIdentifier = PhysicalDocumentQueryExplanation.RequireValue(
            observedIndexIdentifier,
            nameof(observedIndexIdentifier));
    }

    public ExecutableStorageObjectRole Target { get; }
    public ProviderPhysicalObjectName StorageObject { get; }
    public ProviderPhysicalObjectName Index { get; }

    /// <summary>The index identifier read from the provider-native plan, never inferred from the declaration.</summary>
    public string ObservedIndexIdentifier { get; }
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
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        RuntimeInvocationFingerprint = PhysicalDocumentQueryExplanation.RequireValue(
            runtimeInvocationFingerprint,
            nameof(runtimeInvocationFingerprint));
        NativePlanFormat = PhysicalDocumentQueryExplanation.RequireValue(nativePlanFormat, nameof(nativePlanFormat));
        NativePlan = PhysicalDocumentQueryExplanation.RequireValue(nativePlan, nameof(nativePlan));
        RenderedCommand = string.IsNullOrWhiteSpace(renderedCommand) ? null : renderedCommand;
        ArgumentNullException.ThrowIfNull(selectors);
        if (selectors.Count == 0)
            throw new ArgumentException("At least one observed mutation selector is required.", nameof(selectors));
        Selectors = Array.AsReadOnly(selectors.ToArray());
    }

    public PhysicalMutationPlan Plan { get; }
    public string RuntimeInvocationFingerprint { get; }
    public string NativePlanFormat { get; }

    /// <summary>Unsanitized provider-native plan output. Treat as sensitive diagnostic data.</summary>
    public string NativePlan { get; }

    /// <summary>
    /// The exact provider command submitted for planning when the native plan aliases its target.
    /// It is provider-native, unsanitized diagnostic data and can contain raw values.
    /// </summary>
    public string? RenderedCommand { get; }

    public IReadOnlyList<PhysicalDocumentMutationSelectorEvidence> Selectors { get; }
}

/// <summary>Resolves the admitted immutable mutation plan without issuing provider I/O.</summary>
public interface IPhysicalDocumentMutationInspector
{
    PhysicalMutationPlan ResolvePlan(DocumentMutation mutation);
}

/// <summary>
/// Explains the exact admitted mutation selector through its provider's native planner. Explanation
/// fails closed when the provider cannot return plan evidence or that evidence does not show every
/// certified physical target and index.
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
