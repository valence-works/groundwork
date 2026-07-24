using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;
using Groundwork.Core.Text;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

public enum ExecutableStorageObjectRole
{
    PrimaryStorage,
    LinkedIndexStorage,
    CollectionElementStorage
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

public sealed record ExecutableDocumentIdentityRoute(
    StringIdentityCasePolicy StringCasePolicy,
    string ComparisonAlgorithmId,
    string LookupAlgorithmId,
    ExecutableColumnRoute OriginalId,
    ExecutableColumnRoute ComparisonKey,
    ExecutableColumnRoute LookupKey)
{
    public PortableStringIdentityProjection Project(string originalId)
    {
        return Project(
            StringCasePolicy,
            ComparisonAlgorithmId,
            LookupAlgorithmId,
            originalId);
    }

    internal void EnsureSupportedAlgorithms()
    {
        EnsureSupportedAlgorithms(StringCasePolicy, ComparisonAlgorithmId, LookupAlgorithmId);
    }

    internal static PortableStringIdentityProjection Project(
        StringIdentityCasePolicy stringCasePolicy,
        string comparisonAlgorithmId,
        string lookupAlgorithmId,
        string originalId)
    {
        EnsureSupportedAlgorithms(stringCasePolicy, comparisonAlgorithmId, lookupAlgorithmId);
        PortableStringComparison.ValidateIdentity(originalId);
        return PortableStringComparison.ProjectIdentity(
            originalId,
            ToPortableComparisonPolicy(stringCasePolicy));
    }

    private static void EnsureSupportedAlgorithms(
        StringIdentityCasePolicy stringCasePolicy,
        string comparisonAlgorithmId,
        string lookupAlgorithmId)
    {
        var portablePolicy = ToPortableComparisonPolicy(stringCasePolicy);
        var expectedComparison = PortableStringComparison.GetAlgorithmId(portablePolicy);
        if (!string.Equals(comparisonAlgorithmId, expectedComparison, StringComparison.Ordinal) ||
            !string.Equals(lookupAlgorithmId, PortableStringComparison.LookupHashAlgorithmId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The executable identity route contains an unsupported comparison or lookup algorithm.");
        }
    }

    internal static PortableStringComparisonPolicy ToPortableComparisonPolicy(
        StringIdentityCasePolicy policy) => policy switch
        {
            StringIdentityCasePolicy.Ordinal => PortableStringComparisonPolicy.Ordinal,
            StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase => PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
}

public sealed record ExecutableDocumentEnvelopeRoute(
    ExecutableDocumentIdentityRoute Identity,
    ExecutableColumnRoute DocumentKind,
    ExecutableColumnRoute StorageScope,
    ExecutableColumnRoute Version,
    ExecutableColumnRoute SchemaVersion,
    ExecutableColumnRoute CanonicalJson)
{
    public ExecutableColumnRoute Id => Identity.OriginalId;
}

public sealed record ExecutableLinkedRelationshipRoute(
    ExecutableDocumentIdentityRoute Identity,
    ExecutableColumnRoute DocumentKind,
    ExecutableColumnRoute StorageScope)
{
    public ExecutableColumnRoute DocumentId => Identity.OriginalId;
}

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

/// <summary>One provider-owned, typed element storage object for a bounded collection projection.</summary>
public sealed class ExecutableCollectionElementStorageRoute : IEquatable<ExecutableCollectionElementStorageRoute>
{
    public ExecutableCollectionElementStorageRoute(
        ExecutableStorageObjectRoute storage,
        ExecutableProjectedColumnRoute projection,
        ExecutableCollectionElementFieldRoute documentKind,
        ExecutableCollectionElementFieldRoute storageScope,
        ExecutableCollectionElementFieldRoute idComparisonKey,
        ExecutableCollectionElementFieldRoute idLookupKey,
        ExecutableCollectionElementFieldRoute ordinal,
        ExecutableProjectedColumnRoute value,
        ExecutableCollectionElementKeyRoute ownerOrdinalKey)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(documentKind);
        ArgumentNullException.ThrowIfNull(storageScope);
        ArgumentNullException.ThrowIfNull(idComparisonKey);
        ArgumentNullException.ThrowIfNull(idLookupKey);
        ArgumentNullException.ThrowIfNull(ordinal);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(ownerOrdinalKey);
        Storage = storage;
        Projection = projection;
        DocumentKind = documentKind;
        StorageScope = storageScope;
        IdComparisonKey = idComparisonKey;
        IdLookupKey = idLookupKey;
        Ordinal = ordinal;
        Value = value;
        OwnerOrdinalKey = ownerOrdinalKey;
        if (Storage.Role != ExecutableStorageObjectRole.CollectionElementStorage ||
            Value.Target != Storage.Role ||
            Value.Name != Storage.Name ||
            DocumentKind.Role != ExecutableCollectionElementFieldRole.DocumentKind ||
            StorageScope.Role != ExecutableCollectionElementFieldRole.StorageScope ||
            IdComparisonKey.Role != ExecutableCollectionElementFieldRole.IdentityComparison ||
            IdLookupKey.Role != ExecutableCollectionElementFieldRole.IdentityLookup ||
            Ordinal.Role != ExecutableCollectionElementFieldRole.Ordinal ||
            !OwnerOrdinalKey.Columns.SequenceEqual([DocumentKind, StorageScope, IdLookupKey, Ordinal]))
        {
            throw new ArgumentException("Collection element storage fields and owner key do not match the required roles.");
        }
    }

    public ExecutableStorageObjectRoute Storage { get; }
    public ExecutableProjectedColumnRoute Projection { get; }
    public ExecutableCollectionElementFieldRoute DocumentKind { get; }
    public ExecutableCollectionElementFieldRoute StorageScope { get; }
    public ExecutableCollectionElementFieldRoute IdComparisonKey { get; }
    public ExecutableCollectionElementFieldRoute IdLookupKey { get; }
    public ExecutableCollectionElementFieldRoute Ordinal { get; }
    public ExecutableProjectedColumnRoute Value { get; }
    public ExecutableCollectionElementKeyRoute OwnerOrdinalKey { get; }

    public bool Equals(ExecutableCollectionElementStorageRoute? other) =>
        other is not null &&
        Storage == other.Storage &&
        Projection == other.Projection &&
        DocumentKind == other.DocumentKind &&
        StorageScope == other.StorageScope &&
        IdComparisonKey == other.IdComparisonKey &&
        IdLookupKey == other.IdLookupKey &&
        Ordinal == other.Ordinal &&
        Value == other.Value &&
        OwnerOrdinalKey.Equals(other.OwnerOrdinalKey);

    public override bool Equals(object? obj) => Equals(obj as ExecutableCollectionElementStorageRoute);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Storage);
        hash.Add(Projection);
        hash.Add(DocumentKind);
        hash.Add(StorageScope);
        hash.Add(IdComparisonKey);
        hash.Add(IdLookupKey);
        hash.Add(Ordinal);
        hash.Add(Value);
        hash.Add(OwnerOrdinalKey);
        return hash.ToHashCode();
    }
}

public enum ExecutableCollectionElementFieldRole
{
    DocumentKind,
    StorageScope,
    IdentityComparison,
    IdentityLookup,
    Ordinal
}

public sealed record ExecutableCollectionElementFieldRoute(
    ExecutableCollectionElementFieldRole Role,
    ExecutableColumnRoute Column);

/// <summary>Provider-resolved uniqueness evidence for one collection owner and source ordinal.</summary>
public sealed class ExecutableCollectionElementKeyRoute : IEquatable<ExecutableCollectionElementKeyRoute>
{
    public ExecutableCollectionElementKeyRoute(
        ProviderPhysicalObjectName name,
        ExecutableStorageObjectRole target,
        IEnumerable<ExecutableCollectionElementFieldRoute> columns)
    {
        Name = name;
        Target = target;
        Columns = Array.AsReadOnly(columns?.ToArray() ?? throw new ArgumentNullException(nameof(columns)));
        ExecutableCollectionElementFieldRole[] expectedColumns =
        [
            ExecutableCollectionElementFieldRole.DocumentKind,
            ExecutableCollectionElementFieldRole.StorageScope,
            ExecutableCollectionElementFieldRole.IdentityLookup,
            ExecutableCollectionElementFieldRole.Ordinal
        ];
        if (Name.ObjectKind != PhysicalObjectKind.PhysicalIndex ||
            Target != ExecutableStorageObjectRole.CollectionElementStorage ||
            !Columns.Select(column => column.Role).SequenceEqual(expectedColumns))
        {
            throw new ArgumentException(
                "Collection element keys require a physical-index name and the ordered collection-storage columns " +
                "'document_kind', 'storage_scope', 'id_lookup_key', and 'ordinal'.",
                nameof(columns));
        }
    }

    public ProviderPhysicalObjectName Name { get; }
    public ExecutableStorageObjectRole Target { get; }
    public IReadOnlyList<ExecutableCollectionElementFieldRoute> Columns { get; }

    public bool Equals(ExecutableCollectionElementKeyRoute? other) => other is not null &&
        Name == other.Name && Target == other.Target && Columns.SequenceEqual(other.Columns);

    public override bool Equals(object? obj) => Equals(obj as ExecutableCollectionElementKeyRoute);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Target);
        foreach (var column in Columns) hash.Add(column);
        return hash.ToHashCode();
    }
}

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

    public Groundwork.Core.Indexing.MissingValueBehavior MissingValueBehavior => Definition.MissingValueBehavior;

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
        IReadOnlyList<ExecutableCollectionElementStorageRoute> collectionElementStorages,
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
        CollectionElementStorages = Array.AsReadOnly(collectionElementStorages
            .OrderBy(storage => storage.Projection.Definition.LogicalName, StringComparer.Ordinal).ToArray());
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
    public IReadOnlyList<ExecutableCollectionElementStorageRoute> CollectionElementStorages { get; }
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
            CollectionElementStorages,
            Indexes,
            MaintenanceRoutes,
            CandidateQueryPaths,
            CapabilityRequirements,
            DefinitionFingerprint,
            fingerprint);

    internal void EnsureSupportedIdentityAlgorithms()
    {
        Envelope.Identity.EnsureSupportedAlgorithms();
        if (LinkedRelationship is null)
            return;

        LinkedRelationship.Identity.EnsureSupportedAlgorithms();
        if (LinkedRelationship.Identity.StringCasePolicy != Envelope.Identity.StringCasePolicy ||
            !string.Equals(
                LinkedRelationship.Identity.ComparisonAlgorithmId,
                Envelope.Identity.ComparisonAlgorithmId,
                StringComparison.Ordinal) ||
            !string.Equals(
                LinkedRelationship.Identity.LookupAlgorithmId,
                Envelope.Identity.LookupAlgorithmId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The executable primary and linked identity routes do not use the same identity policy and algorithms.");
        }
    }

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
        CollectionElementStorages.SequenceEqual(other.CollectionElementStorages) &&
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
        foreach (var storage in CollectionElementStorages)
            hash.Add(storage);
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
