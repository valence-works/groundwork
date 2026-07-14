using Groundwork.Core.Manifests;
using Groundwork.Core.Text;

namespace Groundwork.Documents.Store;

/// <summary>
/// Binds one declared string-identity policy to the portable comparison and lookup algorithms used
/// by Document Store implementations. Providers persist the returned projection without re-deriving it.
/// </summary>
public sealed class DocumentIdentityBinding
{
    private readonly PortableStringComparisonPolicy comparisonPolicy;

    private DocumentIdentityBinding(StringIdentityCasePolicy stringCasePolicy)
    {
        comparisonPolicy = PortableStringComparison.ForIdentityPolicy(stringCasePolicy);
    }

    public static DocumentIdentityBinding From(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.IdentityPolicy.Kind != StorageIdentityKind.String)
        {
            throw new InvalidOperationException(
                $"Document Store Storage Unit '{unit.Identity.Value}' requires a string identity policy.");
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
