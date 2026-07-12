using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;
using Groundwork.Core.Validation;

namespace Groundwork.Core.Materialization;

public sealed record MaterializationPlan(
    string Identity,
    ProviderIdentity Provider,
    StorageManifestIdentity ManifestIdentity,
    StorageManifestVersion ManifestVersion,
    IReadOnlyList<MaterializationOperation> Operations,
    SchemaHistoryEntry SchemaHistory,
    IReadOnlyList<GroundworkDiagnostic> Diagnostics);

public sealed record MaterializationOperation(
    MaterializationOperationKind Kind,
    string Target,
    IReadOnlyDictionary<string, string> Metadata) : IProviderMaterializationOperation;

public enum MaterializationOperationKind
{
    CreateStorageUnit,
    CreateIndex,
    BackfillCanonicalJson,
    CreateOptimizedProjection,
    RecordSchemaHistory
}

/// <summary>Common execution contract for legacy and schema-evolution materialization steps.</summary>
public interface IProviderMaterializationOperation
{
    MaterializationOperationKind Kind { get; }

    string Target { get; }
}
