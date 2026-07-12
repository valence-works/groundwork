using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

public enum ExecutableStorageObjectRole
{
    PrimaryStorage,
    LinkedIndexStorage
}

public enum ExecutableMaintenanceOperation
{
    Save,
    Update,
    Delete
}

public enum ExecutableQueryPathKind
{
    PrimaryIdentity,
    PhysicalIndex
}

public enum ExecutableStorageCapability
{
    PrimaryDocumentStorage,
    SharedDocumentDiscriminator,
    ScopedStorageKey,
    GlobalStorageKey,
    LinkedStorageMaintenance,
    ProjectedColumnMaintenance,
    InPrimaryProjection,
    PhysicalIndexLookup,
    CompoundIndexLookup,
    ScaleBearingQuery
}

public sealed record ExecutableColumnRoute(string LogicalName, string Identifier);

public sealed record ExecutableStorageObjectRoute(
    ExecutableStorageObjectRole Role,
    ProviderPhysicalObjectName Name,
    int SchemaVersion,
    PhysicalEvolutionMetadata? Evolution);

public sealed record ExecutableDiscriminatorRoute(
    ExecutableColumnRoute Column,
    string Value,
    bool ParticipatesInPrimaryKey);

public sealed record ExecutableScopeKeyRoute(
    ExecutableColumnRoute Column,
    StorageScopePolicy Policy,
    bool ParticipatesInPrimaryKey,
    bool ParticipatesInAuxiliaryKey)
{
    public bool UsesGlobalSentinel => Policy == StorageScopePolicy.Global;
}

public sealed record ExecutableDocumentEnvelopeRoute(
    ExecutableColumnRoute Id,
    ExecutableColumnRoute DocumentKind,
    ExecutableColumnRoute StorageScope,
    ExecutableColumnRoute Version,
    ExecutableColumnRoute SchemaVersion,
    ExecutableColumnRoute CanonicalJson);

public sealed record ExecutableLinkedRelationshipRoute(
    ExecutableColumnRoute DocumentId,
    ExecutableColumnRoute DocumentKind,
    ExecutableColumnRoute StorageScope);

public sealed class ExecutableKeyRoute : IEquatable<ExecutableKeyRoute>
{
    public ExecutableKeyRoute(
        ExecutableStorageObjectRole target,
        IReadOnlyList<ExecutableColumnRoute> columns)
    {
        Target = target;
        Columns = Array.AsReadOnly(columns?.ToArray() ?? throw new ArgumentNullException(nameof(columns)));
    }

    public ExecutableStorageObjectRole Target { get; }

    public IReadOnlyList<ExecutableColumnRoute> Columns { get; }

    public bool Equals(ExecutableKeyRoute? other) =>
        other is not null && Target == other.Target && Columns.SequenceEqual(other.Columns);

    public override bool Equals(object? obj) => Equals(obj as ExecutableKeyRoute);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Target);
        foreach (var column in Columns)
            hash.Add(column);
        return hash.ToHashCode();
    }
}

public sealed record ExecutableProjectedColumnRoute(
    ProjectedColumnDefinition Definition,
    ExecutableColumnRoute Column,
    ExecutableStorageObjectRole Target,
    ProviderPhysicalObjectName Name);

public sealed record ExecutableIndexColumnRoute(
    ExecutableColumnRoute Column,
    int Order,
    PhysicalSortDirection Direction);

public sealed class ExecutablePhysicalIndexRoute : IEquatable<ExecutablePhysicalIndexRoute>
{
    public ExecutablePhysicalIndexRoute(
        PhysicalIndexDefinition definition,
        ProviderPhysicalObjectName name,
        ExecutableStorageObjectRole target,
        IReadOnlyList<ExecutableIndexColumnRoute> columns)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Name = name;
        Target = target;
        Columns = Array.AsReadOnly(columns?.OrderBy(column => column.Order).ToArray()
            ?? throw new ArgumentNullException(nameof(columns)));
    }

    public PhysicalIndexDefinition Definition { get; }

    public string Identity => Definition.LogicalName;

    public ProviderPhysicalObjectName Name { get; }

    public ExecutableStorageObjectRole Target { get; }

    public IReadOnlyList<ExecutableIndexColumnRoute> Columns { get; }

    public bool IsUnique => Definition.IsUnique;

    public bool Equals(ExecutablePhysicalIndexRoute? other) =>
        other is not null &&
        Definition.Equals(other.Definition) &&
        Name == other.Name &&
        Target == other.Target &&
        Columns.SequenceEqual(other.Columns);

    public override bool Equals(object? obj) => Equals(obj as ExecutablePhysicalIndexRoute);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);
        hash.Add(Name);
        hash.Add(Target);
        foreach (var column in Columns)
            hash.Add(column);
        return hash.ToHashCode();
    }
}

public sealed class ExecutableMaintenanceRoute : IEquatable<ExecutableMaintenanceRoute>
{
    public ExecutableMaintenanceRoute(
        ExecutableMaintenanceOperation operation,
        IReadOnlyList<ExecutableStorageObjectRole> targets)
    {
        Operation = operation;
        Targets = Array.AsReadOnly(targets?.Distinct().Order().ToArray()
            ?? throw new ArgumentNullException(nameof(targets)));
    }

    public ExecutableMaintenanceOperation Operation { get; }

    public IReadOnlyList<ExecutableStorageObjectRole> Targets { get; }

    public bool Equals(ExecutableMaintenanceRoute? other) =>
        other is not null && Operation == other.Operation && Targets.SequenceEqual(other.Targets);

    public override bool Equals(object? obj) => Equals(obj as ExecutableMaintenanceRoute);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Operation);
        foreach (var target in Targets)
            hash.Add(target);
        return hash.ToHashCode();
    }
}

public sealed class ExecutableQueryPathRoute : IEquatable<ExecutableQueryPathRoute>
{
    public ExecutableQueryPathRoute(
        string identity,
        ExecutableQueryPathKind kind,
        ExecutableStorageObjectRole target,
        ProviderPhysicalObjectName? indexName,
        IReadOnlyList<ExecutableIndexColumnRoute> columns,
        IReadOnlyList<string> queryIdentities,
        bool isScaleBearing)
    {
        Identity = identity;
        Kind = kind;
        Target = target;
        IndexName = indexName;
        Columns = Array.AsReadOnly(columns?.OrderBy(column => column.Order).ToArray()
            ?? throw new ArgumentNullException(nameof(columns)));
        QueryIdentities = Array.AsReadOnly(
            queryIdentities?.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentNullException(nameof(queryIdentities)));
        IsScaleBearing = isScaleBearing;
    }

    public string Identity { get; }

    public ExecutableQueryPathKind Kind { get; }

    public ExecutableStorageObjectRole Target { get; }

    public ProviderPhysicalObjectName? IndexName { get; }

    public IReadOnlyList<ExecutableIndexColumnRoute> Columns { get; }

    public IReadOnlyList<string> QueryIdentities { get; }

    public bool IsScaleBearing { get; }

    public bool Equals(ExecutableQueryPathRoute? other) =>
        other is not null &&
        Identity == other.Identity &&
        Kind == other.Kind &&
        Target == other.Target &&
        IndexName == other.IndexName &&
        Columns.SequenceEqual(other.Columns) &&
        QueryIdentities.SequenceEqual(other.QueryIdentities) &&
        IsScaleBearing == other.IsScaleBearing;

    public override bool Equals(object? obj) => Equals(obj as ExecutableQueryPathRoute);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Identity, StringComparer.Ordinal);
        hash.Add(Kind);
        hash.Add(Target);
        hash.Add(IndexName);
        foreach (var column in Columns)
            hash.Add(column);
        foreach (var query in QueryIdentities)
            hash.Add(query, StringComparer.Ordinal);
        hash.Add(IsScaleBearing);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Immutable provider-neutral execution mapping compiled from one provider physical definition.
/// Providers consume this route instead of re-deriving form, names, fields, keys, or maintenance paths.
/// </summary>
public sealed class ExecutableStorageRoute : IEquatable<ExecutableStorageRoute>
{
    internal ExecutableStorageRoute(
        StorageUnitIdentity storageUnit,
        StorageUnitProvisioningMode provisioningMode,
        PhysicalStorageForm form,
        SharedStorageBinding? sharedStorage,
        StorageScopePolicy scopePolicy,
        ExecutableStorageObjectRoute primaryStorage,
        ExecutableStorageObjectRoute? linkedIndexStorage,
        ExecutableDocumentEnvelopeRoute envelope,
        ExecutableLinkedRelationshipRoute? linkedRelationship,
        ExecutableDiscriminatorRoute discriminator,
        ExecutableScopeKeyRoute scopeKey,
        ExecutableKeyRoute primaryKey,
        ExecutableKeyRoute? auxiliaryKey,
        IReadOnlyList<ExecutableProjectedColumnRoute> projectedColumns,
        IReadOnlyList<ExecutablePhysicalIndexRoute> indexes,
        IReadOnlyList<ExecutableMaintenanceRoute> maintenanceRoutes,
        IReadOnlyList<ExecutableQueryPathRoute> candidateQueryPaths,
        IReadOnlyList<ExecutableStorageCapability> capabilityRequirements,
        string definitionFingerprint,
        string fingerprint)
    {
        StorageUnit = storageUnit;
        ProvisioningMode = provisioningMode;
        Form = form;
        SharedStorage = sharedStorage;
        ScopePolicy = scopePolicy;
        PrimaryStorage = primaryStorage;
        LinkedIndexStorage = linkedIndexStorage;
        Envelope = envelope;
        LinkedRelationship = linkedRelationship;
        Discriminator = discriminator;
        ScopeKey = scopeKey;
        PrimaryKey = primaryKey;
        AuxiliaryKey = auxiliaryKey;
        ProjectedColumns = Array.AsReadOnly(projectedColumns.OrderBy(column => column.Definition.LogicalName, StringComparer.Ordinal).ToArray());
        Indexes = Array.AsReadOnly(indexes.OrderBy(index => index.Identity, StringComparer.Ordinal).ToArray());
        MaintenanceRoutes = Array.AsReadOnly(maintenanceRoutes.OrderBy(route => route.Operation).ToArray());
        CandidateQueryPaths = Array.AsReadOnly(candidateQueryPaths
            .OrderBy(path => path.Kind)
            .ThenBy(path => path.Identity, StringComparer.Ordinal)
            .ToArray());
        CapabilityRequirements = Array.AsReadOnly(capabilityRequirements.Distinct().Order().ToArray());
        DefinitionFingerprint = definitionFingerprint;
        Fingerprint = fingerprint;
    }

    public StorageUnitIdentity StorageUnit { get; }
    public StorageUnitProvisioningMode ProvisioningMode { get; }
    public PhysicalStorageForm Form { get; }
    public SharedStorageBinding? SharedStorage { get; }
    public StorageScopePolicy ScopePolicy { get; }
    public ExecutableStorageObjectRoute PrimaryStorage { get; }
    public ExecutableStorageObjectRoute? LinkedIndexStorage { get; }
    public ExecutableDocumentEnvelopeRoute Envelope { get; }
    public ExecutableLinkedRelationshipRoute? LinkedRelationship { get; }
    public ExecutableDiscriminatorRoute Discriminator { get; }
    public ExecutableScopeKeyRoute ScopeKey { get; }
    public ExecutableKeyRoute PrimaryKey { get; }
    public ExecutableKeyRoute? AuxiliaryKey { get; }
    public IReadOnlyList<ExecutableProjectedColumnRoute> ProjectedColumns { get; }
    public IReadOnlyList<ExecutablePhysicalIndexRoute> Indexes { get; }
    public IReadOnlyList<ExecutableMaintenanceRoute> MaintenanceRoutes { get; }
    public IReadOnlyList<ExecutableQueryPathRoute> CandidateQueryPaths { get; }
    public IReadOnlyList<ExecutableStorageCapability> CapabilityRequirements { get; }
    public string DefinitionFingerprint { get; }
    public string Fingerprint { get; }

    internal ExecutableStorageRoute WithFingerprint(string fingerprint) =>
        new(
            StorageUnit,
            ProvisioningMode,
            Form,
            SharedStorage,
            ScopePolicy,
            PrimaryStorage,
            LinkedIndexStorage,
            Envelope,
            LinkedRelationship,
            Discriminator,
            ScopeKey,
            PrimaryKey,
            AuxiliaryKey,
            ProjectedColumns,
            Indexes,
            MaintenanceRoutes,
            CandidateQueryPaths,
            CapabilityRequirements,
            DefinitionFingerprint,
            fingerprint);

    public bool Equals(ExecutableStorageRoute? other) =>
        other is not null &&
        StorageUnit == other.StorageUnit &&
        ProvisioningMode == other.ProvisioningMode &&
        Form == other.Form &&
        SharedStorage == other.SharedStorage &&
        ScopePolicy == other.ScopePolicy &&
        PrimaryStorage == other.PrimaryStorage &&
        LinkedIndexStorage == other.LinkedIndexStorage &&
        Envelope == other.Envelope &&
        LinkedRelationship == other.LinkedRelationship &&
        Discriminator == other.Discriminator &&
        ScopeKey == other.ScopeKey &&
        PrimaryKey.Equals(other.PrimaryKey) &&
        Equals(AuxiliaryKey, other.AuxiliaryKey) &&
        ProjectedColumns.SequenceEqual(other.ProjectedColumns) &&
        Indexes.SequenceEqual(other.Indexes) &&
        MaintenanceRoutes.SequenceEqual(other.MaintenanceRoutes) &&
        CandidateQueryPaths.SequenceEqual(other.CandidateQueryPaths) &&
        CapabilityRequirements.SequenceEqual(other.CapabilityRequirements) &&
        DefinitionFingerprint == other.DefinitionFingerprint &&
        Fingerprint == other.Fingerprint;

    public override bool Equals(object? obj) => Equals(obj as ExecutableStorageRoute);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StorageUnit);
        hash.Add(ProvisioningMode);
        hash.Add(Form);
        hash.Add(SharedStorage);
        hash.Add(ScopePolicy);
        hash.Add(PrimaryStorage);
        hash.Add(LinkedIndexStorage);
        hash.Add(Envelope);
        hash.Add(LinkedRelationship);
        hash.Add(Discriminator);
        hash.Add(ScopeKey);
        hash.Add(PrimaryKey);
        hash.Add(AuxiliaryKey);
        foreach (var column in ProjectedColumns)
            hash.Add(column);
        foreach (var index in Indexes)
            hash.Add(index);
        foreach (var maintenance in MaintenanceRoutes)
            hash.Add(maintenance);
        foreach (var queryPath in CandidateQueryPaths)
            hash.Add(queryPath);
        foreach (var capability in CapabilityRequirements)
            hash.Add(capability);
        hash.Add(DefinitionFingerprint, StringComparer.Ordinal);
        hash.Add(Fingerprint, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed class ExecutableStorageRouteCompilationResult
{
    public ExecutableStorageRouteCompilationResult(
        IReadOnlyList<ExecutableStorageRoute> routes,
        IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        Routes = Array.AsReadOnly(routes.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<ExecutableStorageRoute> Routes { get; }

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    public bool IsValid => Diagnostics.All(diagnostic => !diagnostic.IsError);
}
