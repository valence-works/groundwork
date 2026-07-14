using System.Text.Json;
using Groundwork.Core.Manifests;
using Groundwork.Core.Text;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Durable identity semantics admitted for one Document Store Storage Unit.
/// </summary>
public sealed record DocumentStoreIdentitySchemaState
{
    private const int CurrentFormatVersion = 1;

    private DocumentStoreIdentitySchemaState(
        StringIdentityCasePolicy stringCasePolicy,
        string comparisonAlgorithmId,
        string lookupAlgorithmId)
    {
        StringCasePolicy = stringCasePolicy;
        ComparisonAlgorithmId = comparisonAlgorithmId;
        LookupAlgorithmId = lookupAlgorithmId;
    }

    public StringIdentityCasePolicy StringCasePolicy { get; }

    public string ComparisonAlgorithmId { get; }

    public string LookupAlgorithmId { get; }

    public static DocumentStoreIdentitySchemaState Capture(IdentityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var stringCasePolicy = policy.Kind == StorageIdentityKind.String
            ? policy.StringCasePolicy
            : StringIdentityCasePolicy.Ordinal;
        var comparisonPolicy = PortableStringComparison.ForIdentityPolicy(stringCasePolicy);
        return new(
            stringCasePolicy,
            PortableStringComparison.GetAlgorithmId(comparisonPolicy),
            PortableStringComparison.LookupHashAlgorithmId);
    }

    public string ToCanonicalJson() => JsonSerializer.Serialize(new SerializedState(
        CurrentFormatVersion,
        StringCasePolicy,
        ComparisonAlgorithmId,
        LookupAlgorithmId));

    public static DocumentStoreIdentitySchemaState FromCanonicalJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SerializedState? serialized;
        try
        {
            serialized = JsonSerializer.Deserialize<SerializedState>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Persisted Document Store identity schema state is invalid.", exception);
        }

        if (serialized is null ||
            serialized.FormatVersion != CurrentFormatVersion ||
            !Enum.IsDefined(serialized.StringCasePolicy) ||
            string.IsNullOrWhiteSpace(serialized.ComparisonAlgorithmId) ||
            string.IsNullOrWhiteSpace(serialized.LookupAlgorithmId))
        {
            throw new InvalidOperationException("Persisted Document Store identity schema state is unsupported or incomplete.");
        }

        return new(
            serialized.StringCasePolicy,
            serialized.ComparisonAlgorithmId,
            serialized.LookupAlgorithmId);
    }

    private sealed record SerializedState(
        int FormatVersion,
        StringIdentityCasePolicy StringCasePolicy,
        string ComparisonAlgorithmId,
        string LookupAlgorithmId);
}
