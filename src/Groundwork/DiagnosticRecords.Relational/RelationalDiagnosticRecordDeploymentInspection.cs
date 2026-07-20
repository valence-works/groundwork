using Groundwork.DiagnosticRecords;

namespace Groundwork.DiagnosticRecords.Relational;

/// <summary>
/// Provider-normalized read-only snapshots used to classify a diagnostic-record relational
/// deployment. Providers own catalog access and normalization; this type owns the common
/// missing-versus-drifted semantics so admission cannot diverge by dialect.
/// </summary>
internal sealed record RelationalDiagnosticTableSnapshot(
    string Name,
    IReadOnlyList<RelationalDiagnosticColumnSnapshot> Columns,
    IReadOnlyList<string> PrimaryKey,
    string Kind,
    string? PrimaryKeyKind,
    bool IsPrimaryKeyUsable);

internal sealed record RelationalDiagnosticColumnSnapshot(
    string Name,
    string StoreType,
    bool IsNullable,
    string? Collation);

internal sealed record RelationalDiagnosticIndexKeySnapshot(string Name, bool Descending);

internal sealed record RelationalDiagnosticIndexSnapshot(
    string Name,
    string Table,
    IReadOnlyList<RelationalDiagnosticIndexKeySnapshot> Keys,
    bool IsUnique,
    string? Filter,
    IReadOnlyList<string> IncludedColumns,
    string Kind,
    bool IsUsable);

internal sealed record RelationalDiagnosticDefinitionSnapshot(
    long SchemaVersion,
    string DefinitionFingerprint,
    string AlgorithmManifestFingerprint);

internal static class RelationalDiagnosticRecordDeploymentInspector
{
    public static DiagnosticRecordDeploymentInspection Classify(
        string provider,
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlyList<RelationalDiagnosticTableSnapshot> expectedTables,
        IReadOnlyList<RelationalDiagnosticIndexSnapshot> expectedIndexes,
        IReadOnlyList<RelationalDiagnosticTableSnapshot> actualTables,
        IReadOnlyList<RelationalDiagnosticIndexSnapshot> actualIndexes,
        IReadOnlyDictionary<string, RelationalDiagnosticDefinitionSnapshot> definitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(expectedTables);
        ArgumentNullException.ThrowIfNull(expectedIndexes);
        ArgumentNullException.ThrowIfNull(actualTables);
        ArgumentNullException.ThrowIfNull(actualIndexes);
        ArgumentNullException.ThrowIfNull(definitions);

        var physical = ClassifyPhysical(
            provider,
            deployment,
            expectedTables,
            expectedIndexes,
            actualTables,
            actualIndexes);
        return physical.IsReady
            ? ClassifyDefinitions(provider, deployment, definitions)
            : physical;
    }

    public static DiagnosticRecordDeploymentInspection ClassifyPhysical(
        string provider,
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlyList<RelationalDiagnosticTableSnapshot> expectedTables,
        IReadOnlyList<RelationalDiagnosticIndexSnapshot> expectedIndexes,
        IReadOnlyList<RelationalDiagnosticTableSnapshot> actualTables,
        IReadOnlyList<RelationalDiagnosticIndexSnapshot> actualIndexes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(expectedTables);
        ArgumentNullException.ThrowIfNull(expectedIndexes);
        ArgumentNullException.ThrowIfNull(actualTables);
        ArgumentNullException.ThrowIfNull(actualIndexes);

        var actualTableMap = actualTables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        var actualIndexMap = actualIndexes.ToDictionary(index => (index.Table, index.Name));
        if (expectedTables.Any(table => !actualTableMap.ContainsKey(table.Name)))
            return DiagnosticRecordDeploymentInspection.Missing(provider, deployment);

        if (expectedTables.Any(table => !TableMatches(table, actualTableMap[table.Name])))
            return DiagnosticRecordDeploymentInspection.Drifted(
                provider,
                deployment.Streams.Select(stream => stream.Stream.Value).ToArray());

        if (expectedIndexes.Any(index => !actualIndexMap.ContainsKey((index.Table, index.Name))))
            return DiagnosticRecordDeploymentInspection.Missing(provider, deployment);

        return expectedIndexes.Any(index => !IndexMatches(index, actualIndexMap[(index.Table, index.Name)]))
            ? DiagnosticRecordDeploymentInspection.Drifted(
                provider,
                deployment.Streams.Select(stream => stream.Stream.Value).ToArray())
            : DiagnosticRecordDeploymentInspection.Ready(provider);
    }

    public static DiagnosticRecordDeploymentInspection ClassifyDefinitions(
        string provider,
        DiagnosticRecordDeploymentManifest deployment,
        IReadOnlyDictionary<string, RelationalDiagnosticDefinitionSnapshot> definitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(definitions);
        var missing = new List<string>();
        var drifted = new List<string>();
        foreach (var stream in deployment.Streams)
        {
            if (!definitions.TryGetValue(stream.Stream.Value, out var actual))
            {
                missing.Add(stream.Stream.Value);
                continue;
            }

            var expected = DiagnosticRecordPhysicalSchemaState.Capture(stream);
            if (actual.SchemaVersion != stream.SchemaVersion ||
                !StringComparer.Ordinal.Equals(actual.DefinitionFingerprint, expected.DefinitionFingerprint) ||
                !StringComparer.Ordinal.Equals(actual.AlgorithmManifestFingerprint, expected.ComparisonAlgorithmManifestFingerprint))
            {
                drifted.Add(stream.Stream.Value);
            }
        }

        if (missing.Count != 0)
            return DiagnosticRecordDeploymentInspection.Missing(provider, deployment, missing);
        return drifted.Count != 0
            ? DiagnosticRecordDeploymentInspection.Drifted(provider, drifted)
            : DiagnosticRecordDeploymentInspection.Ready(provider);
    }

    private static bool TableMatches(
        RelationalDiagnosticTableSnapshot expected,
        RelationalDiagnosticTableSnapshot actual) =>
        StringComparer.OrdinalIgnoreCase.Equals(expected.Kind, actual.Kind) &&
        SequenceEquals(expected.PrimaryKey, actual.PrimaryKey) &&
        StringComparer.OrdinalIgnoreCase.Equals(expected.PrimaryKeyKind, actual.PrimaryKeyKind) &&
        expected.IsPrimaryKeyUsable == actual.IsPrimaryKeyUsable &&
        expected.Columns.Count == actual.Columns.Count &&
        expected.Columns.Zip(actual.Columns).All(pair =>
            StringComparer.Ordinal.Equals(pair.First.Name, pair.Second.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(pair.First.StoreType, pair.Second.StoreType) &&
            pair.First.IsNullable == pair.Second.IsNullable &&
            StringComparer.OrdinalIgnoreCase.Equals(pair.First.Collation, pair.Second.Collation));

    private static bool IndexMatches(
        RelationalDiagnosticIndexSnapshot expected,
        RelationalDiagnosticIndexSnapshot actual) =>
        StringComparer.Ordinal.Equals(expected.Table, actual.Table) &&
        expected.IsUnique == actual.IsUnique &&
        SequenceEquals(expected.Keys, actual.Keys) &&
        SequenceEquals(expected.IncludedColumns, actual.IncludedColumns) &&
        StringComparer.OrdinalIgnoreCase.Equals(expected.Kind, actual.Kind) &&
        expected.IsUsable == actual.IsUsable &&
        StringComparer.Ordinal.Equals(NormalizeFilter(expected.Filter), NormalizeFilter(actual.Filter));

    private static bool SequenceEquals(IReadOnlyList<string> expected, IReadOnlyList<string> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(pair =>
            StringComparer.Ordinal.Equals(pair.First, pair.Second));

    private static bool SequenceEquals(
        IReadOnlyList<RelationalDiagnosticIndexKeySnapshot> expected,
        IReadOnlyList<RelationalDiagnosticIndexKeySnapshot> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(pair =>
            StringComparer.Ordinal.Equals(pair.First.Name, pair.Second.Name) &&
            pair.First.Descending == pair.Second.Descending);

    private static string NormalizeFilter(string? filter) => string.Concat((filter ?? string.Empty)
        .Where(character => !char.IsWhiteSpace(character) && character is not '[' and not ']' and not '"' and not '(' and not ')')
        .Select(char.ToLowerInvariant));
}
