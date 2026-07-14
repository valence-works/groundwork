using Groundwork.Core.Manifests;
using Groundwork.Core.Text;

namespace Groundwork.Documents.Store;

/// <summary>
/// Binds one admitted identity policy to the portable string projection used by Document Store
/// implementations. Declared string identities retain their case policy; other identity kinds preserve
/// their ordinal string representation. Providers persist the returned projection without re-deriving it.
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
        var stringCasePolicy = unit.IdentityPolicy.Kind == StorageIdentityKind.String
            ? unit.IdentityPolicy.StringCasePolicy
            : StringIdentityCasePolicy.Ordinal;
        return new DocumentIdentityBinding(stringCasePolicy);
    }

    public static IReadOnlyDictionary<string, DocumentIdentityBinding> Bind(StorageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.StorageUnits.ToDictionary(
            unit => unit.Identity.Value,
            From,
            StringComparer.Ordinal);
    }

    public PortableStringIdentityProjection Project(string originalId)
    {
        PortableStringComparison.ValidateIdentity(originalId);
        return PortableStringComparison.ProjectIdentity(originalId, comparisonPolicy);
    }

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
