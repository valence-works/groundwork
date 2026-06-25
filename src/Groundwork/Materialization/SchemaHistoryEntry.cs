using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;

namespace Groundwork.Materialization;

public sealed record SchemaHistoryEntry(
    StorageManifestIdentity ManifestIdentity,
    StorageManifestVersion ManifestVersion,
    ProviderIdentity Provider,
    DateTimeOffset PlannedAt,
    IReadOnlyList<string> AppliedOperationTargets);
