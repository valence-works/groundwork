using System.Text.RegularExpressions;

namespace Groundwork.Core.Capabilities;

/// <summary>
/// Stable, namespaced identifier for a persistence capability (e.g.
/// <c>groundwork.operational.atomic-claim</c>). Capability ids are the open/closed seam: storage
/// units require them, providers advertise them, and modules introduce new ones — all without core
/// edits. The <c>groundwork.*</c> namespace is reserved for built-in capabilities; modules own their
/// own vendor namespace.
/// </summary>
public readonly partial record struct CapabilityId
{
    private static readonly Regex NamespacedForm = CapabilityIdPattern();

    public CapabilityId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Capability id must be a non-empty namespaced value (e.g. 'vendor.area.name').", nameof(value));

        if (!NamespacedForm.IsMatch(value))
            throw new ArgumentException(
                $"Capability id '{value}' must be a dotted, lowercase, namespaced value of the form 'vendor.area.name' (segments [a-z0-9-], at least two segments).",
                nameof(value));

        Value = value;
    }

    public string Value { get; }

    /// <summary>The leading namespace segment (the owner), e.g. <c>groundwork</c>.</summary>
    public string Namespace => Value[..Value.IndexOf('.')];

    public bool Equals(CapabilityId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static implicit operator string(CapabilityId id) => id.Value;

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdPattern();
}
