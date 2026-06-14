namespace Groundwork.Core.Capabilities;

/// <summary>
/// Derived verdict describing how well a provider fits a manifest's storage requirements.
/// Computed by <see cref="ProviderCapabilityValidator.Evaluate"/> — never author-declared.
/// </summary>
public abstract record ProviderFit
{
    private ProviderFit()
    {
    }

    /// <summary>The provider supports every required capability (and evidence where gated).</summary>
    public sealed record Supported : ProviderFit;

    /// <summary>
    /// The provider supports the required capabilities, but one or more are evidence-gated and the
    /// provider has not yet supplied benchmark/operational evidence for them.
    /// </summary>
    public sealed record RequiresEvidence(IReadOnlyList<string> Reasons) : ProviderFit;

    /// <summary>The provider cannot serve one or more required capabilities at all.</summary>
    public sealed record Unsupported(IReadOnlyList<CapabilityId> MissingRequirements) : ProviderFit;
}
