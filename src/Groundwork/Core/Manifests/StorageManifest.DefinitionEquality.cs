using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;

namespace Groundwork.Core.Manifests;

public sealed partial record StorageManifest
{
    /// <summary>
    /// Compares the complete manifest definition by stable domain identity rather than by the
    /// reference identity of its immutable collection properties.
    /// </summary>
    public bool HasSameDefinitionAs(StorageManifest? other) =>
        other is not null &&
        Identity == other.Identity &&
        Owner == other.Owner &&
        Version == other.Version &&
        RequiredCapabilities.SetEquals(other.RequiredCapabilities) &&
        CompatibilityNotes.SequenceEqual(other.CompatibilityNotes, StringComparer.Ordinal) &&
        SequenceBy(
            SharedDocumentStorages,
            other.SharedDocumentStorages,
            item => item.Binding.Value,
            EqualityComparer<SharedDocumentStorageDefinition>.Default) &&
        UnitsEqual(StorageUnits, other.StorageUnits);

    private static bool UnitsEqual(
        IReadOnlyList<StorageUnit> first,
        IReadOnlyList<StorageUnit> second) =>
        first.Count == second.Count && first.All(unit =>
        {
            var candidate = second.SingleOrDefault(item => item.Identity == unit.Identity);
            return candidate is not null && UnitEquals(unit, candidate);
        });

    private static bool UnitEquals(StorageUnit first, StorageUnit second) =>
        first.Identity == second.Identity &&
        first.DisplayName == second.DisplayName &&
        first.Intent == second.Intent &&
        first.Lifecycle == second.Lifecycle &&
        first.IdentityPolicy == second.IdentityPolicy &&
        first.Tenancy == second.Tenancy &&
        first.Concurrency == second.Concurrency &&
        first.Serialization == second.Serialization &&
        first.Physicalization == second.Physicalization &&
        first.PhysicalStorage == second.PhysicalStorage &&
        SequenceBy(first.Indexes, second.Indexes, item => item.Identity, IndexDeclarationComparer.Instance) &&
        SequenceBy(first.Queries, second.Queries, item => item.Identity, PortableQueryDeclarationComparer.Instance);

    private static bool SequenceBy<T>(
        IReadOnlyList<T> first,
        IReadOnlyList<T> second,
        Func<T, string> identity,
        IEqualityComparer<T> comparer) =>
        first.Count == second.Count && first.All(item =>
        {
            var matches = second.Where(candidate => identity(candidate) == identity(item)).ToArray();
            return matches.Length == 1 && comparer.Equals(item, matches[0]);
        });

    private sealed class IndexDeclarationComparer : IEqualityComparer<IndexDeclaration>
    {
        public static IndexDeclarationComparer Instance { get; } = new();

        public bool Equals(IndexDeclaration? first, IndexDeclaration? second) =>
            first is not null &&
            second is not null &&
            first.Identity == second.Identity &&
            first.Fields.SequenceEqual(second.Fields) &&
            first.ValueKind == second.ValueKind &&
            first.IsUnique == second.IsUnique &&
            first.IsSortable == second.IsSortable &&
            first.MissingValueBehavior == second.MissingValueBehavior &&
            first.SupportedOperations.SetEquals(second.SupportedOperations) &&
            first.Physicalization == second.Physicalization;

        public int GetHashCode(IndexDeclaration item) =>
            item.Identity.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class PortableQueryDeclarationComparer : IEqualityComparer<PortableQueryDeclaration>
    {
        public static PortableQueryDeclarationComparer Instance { get; } = new();

        public bool Equals(PortableQueryDeclaration? first, PortableQueryDeclaration? second) =>
            first is not null &&
            second is not null &&
            first.Identity == second.Identity &&
            first.IndexIdentity == second.IndexIdentity &&
            first.Operations.SetEquals(second.Operations) &&
            first.SortSupport == second.SortSupport &&
            first.PagingSupport == second.PagingSupport &&
            first.SupportsDisjunction == second.SupportsDisjunction &&
            first.SupportsTotalCount == second.SupportsTotalCount;

        public int GetHashCode(PortableQueryDeclaration item) =>
            item.Identity.GetHashCode(StringComparison.Ordinal);
    }
}
