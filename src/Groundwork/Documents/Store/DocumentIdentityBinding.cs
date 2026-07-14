using Groundwork.Core.Manifests;
using Groundwork.Core.Text;

namespace Groundwork.Documents.Store;

/// <summary>
/// Binds one declared string-identity policy to the portable comparison and lookup algorithms used
/// by conventional document stores. Providers persist the returned projection without re-deriving it.
/// </summary>
public sealed class DocumentIdentityBinding
{
    private readonly PortableStringComparisonPolicy comparisonPolicy;

    private DocumentIdentityBinding(StringIdentityCasePolicy stringCasePolicy)
    {
        StringCasePolicy = stringCasePolicy;
        comparisonPolicy = PortableStringComparison.ForIdentityPolicy(stringCasePolicy);
        ComparisonAlgorithmId = PortableStringComparison.GetAlgorithmId(comparisonPolicy);
        LookupAlgorithmId = PortableStringComparison.LookupHashAlgorithmId;
    }

    public StringIdentityCasePolicy StringCasePolicy { get; }

    public string ComparisonAlgorithmId { get; }

    public string LookupAlgorithmId { get; }

    public static DocumentIdentityBinding From(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.IdentityPolicy.Kind != StorageIdentityKind.String)
        {
            throw new InvalidOperationException(
                $"Conventional document store '{unit.Identity.Value}' requires a string identity policy.");
        }

        return new DocumentIdentityBinding(unit.IdentityPolicy.StringCasePolicy);
    }

    public static IReadOnlyDictionary<string, DocumentIdentityBinding> Bind(StorageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.StorageUnits.ToDictionary(
            unit => unit.Identity.Value,
            From,
            StringComparer.Ordinal);
    }

    public PortableStringIdentityProjection Project(string originalId) =>
        PortableStringComparison.ProjectIdentity(originalId, comparisonPolicy);

    public void EnsureLookupIntegrity(
        string documentKind,
        PortableStringIdentityProjection requested,
        string retainedId,
        string retainedComparisonKey,
        string retainedLookupKey)
    {
        if (string.Equals(requested.LookupKey, retainedLookupKey, StringComparison.Ordinal) &&
            string.Equals(requested.ComparisonKey, retainedComparisonKey, StringComparison.Ordinal))
        {
            return;
        }

        throw new DocumentIdentityLookupCollisionException(
            documentKind,
            requested.OriginalValue,
            retainedId,
            requested.LookupKey);
    }
}
