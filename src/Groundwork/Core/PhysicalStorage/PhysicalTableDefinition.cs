namespace Groundwork.Core.PhysicalStorage;

/// <summary>The portable data type of a projected physical column.</summary>
public enum PortablePhysicalType
{
    String,
    Int32,
    Int64,
    Decimal,
    Boolean,
    DateTime,
    Guid,
    Binary,
    Json
}

public enum ProjectionRebuildMode
{
    FromCanonicalJson,
    SemanticMigrationRequired
}

public enum PhysicalSortDirection
{
    Ascending,
    Descending
}

/// <summary>The standard envelope and authoritative canonical JSON column names.</summary>
public sealed record DocumentEnvelopeDefinition(
    string IdColumn = "id",
    string DocumentKindColumn = "document_kind",
    string StorageScopeColumn = "storage_scope",
    string VersionColumn = "version",
    string SchemaVersionColumn = "schema_version",
    string CanonicalJsonColumn = "document");

/// <summary>Schema-evolution hints that remain provider-neutral.</summary>
public sealed record PhysicalEvolutionMetadata(
    bool RequiresBackfill = false,
    bool IsDestructive = false,
    string? SemanticMigrationIdentity = null);

/// <summary>A rebuildable provider-native projection of one stable serialized path.</summary>
public sealed record ProjectedColumnDefinition(
    string LogicalName,
    string Path,
    PortablePhysicalType Type,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    bool IsNullable = true,
    string? Collation = null,
    string? DefaultValue = null,
    ProjectionRebuildMode RebuildMode = ProjectionRebuildMode.FromCanonicalJson);

/// <summary>One ordered column reference in a compound physical index.</summary>
public sealed record PhysicalIndexColumnDefinition(
    string ColumnLogicalName,
    int Order,
    PhysicalSortDirection Direction = PhysicalSortDirection.Ascending);

/// <summary>A physical index over columns owned by one physical table definition.</summary>
public sealed class PhysicalIndexDefinition : IEquatable<PhysicalIndexDefinition>
{
    public PhysicalIndexDefinition(
        string logicalName,
        IReadOnlyList<PhysicalIndexColumnDefinition> columns,
        bool isUnique = false,
        int schemaVersion = 1,
        PhysicalEvolutionMetadata? evolution = null)
    {
        LogicalName = logicalName;
        Columns = columns?
            .OrderBy(x => x.Order)
            .ThenBy(x => x.ColumnLogicalName, StringComparer.Ordinal)
            .ToArray() ?? throw new ArgumentNullException(nameof(columns));
        IsUnique = isUnique;
        SchemaVersion = schemaVersion;
        Evolution = evolution;
    }

    public string LogicalName { get; }

    public IReadOnlyList<PhysicalIndexColumnDefinition> Columns { get; }

    public bool IsUnique { get; }

    public int SchemaVersion { get; }

    public PhysicalEvolutionMetadata? Evolution { get; }

    public bool Equals(PhysicalIndexDefinition? other) =>
        other is not null &&
        LogicalName == other.LogicalName &&
        Columns.SequenceEqual(other.Columns) &&
        IsUnique == other.IsUnique &&
        SchemaVersion == other.SchemaVersion &&
        Evolution == other.Evolution;

    public override bool Equals(object? obj) => Equals(obj as PhysicalIndexDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(LogicalName, StringComparer.Ordinal);
        foreach (var column in Columns)
            hash.Add(column);
        hash.Add(IsUnique);
        hash.Add(SchemaVersion);
        hash.Add(Evolution);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Provider-neutral physical structure for one storage unit. Shared definitions reference a
/// manifest-owned primary store; dedicated and entity definitions own their primary envelope.
/// Canonical JSON remains authoritative for every form.
/// </summary>
public sealed class PhysicalTableDefinition : IEquatable<PhysicalTableDefinition>
{
    private PhysicalTableDefinition(
        PhysicalStorageForm form,
        string? featureDefaultLogicalName,
        SharedStorageBinding? sharedStorage,
        DocumentEnvelopeDefinition? envelope,
        IReadOnlyList<ProjectedColumnDefinition>? projectedColumns,
        IReadOnlyList<PhysicalIndexDefinition>? indexes,
        int schemaVersion,
        PhysicalEvolutionMetadata? evolution,
        string? linkedProjectionLogicalName)
    {
        Form = form;
        FeatureDefaultLogicalName = featureDefaultLogicalName;
        SharedStorage = sharedStorage;
        Envelope = envelope;
        ProjectedColumns = projectedColumns?
            .OrderBy(x => x.LogicalName, StringComparer.Ordinal)
            .ToArray() ?? [];
        Indexes = indexes?
            .OrderBy(x => x.LogicalName, StringComparer.Ordinal)
            .ToArray() ?? [];
        SchemaVersion = schemaVersion;
        Evolution = evolution;
        LinkedProjectionLogicalName = linkedProjectionLogicalName;
    }

    public PhysicalStorageForm Form { get; }

    public string? FeatureDefaultLogicalName { get; }

    public SharedStorageBinding? SharedStorage { get; }

    public DocumentEnvelopeDefinition? Envelope { get; }

    public IReadOnlyList<ProjectedColumnDefinition> ProjectedColumns { get; }

    public IReadOnlyList<PhysicalIndexDefinition> Indexes { get; }

    public int SchemaVersion { get; }

    public PhysicalEvolutionMetadata? Evolution { get; }

    /// <summary>
    /// Gets the unit-owned linked projection table name for shared-document storage. It is null
    /// for shared units without linked structures and for dedicated/entity primary tables.
    /// </summary>
    public string? LinkedProjectionLogicalName { get; }

    public static PhysicalTableDefinition SharedDocuments(
        SharedStorageBinding sharedStorage,
        IReadOnlyList<ProjectedColumnDefinition>? linkedProjectedColumns = null,
        IReadOnlyList<PhysicalIndexDefinition>? linkedIndexes = null,
        int schemaVersion = 1,
        PhysicalEvolutionMetadata? evolution = null,
        string? linkedProjectionLogicalName = null) =>
        new(
            PhysicalStorageForm.SharedDocuments,
            null,
            sharedStorage ?? throw new ArgumentNullException(nameof(sharedStorage)),
            null,
            linkedProjectedColumns,
            linkedIndexes,
            schemaVersion,
            evolution,
            linkedProjectionLogicalName);

    public static PhysicalTableDefinition DedicatedDocumentTable(
        string featureDefaultLogicalName,
        DocumentEnvelopeDefinition? envelope = null,
        IReadOnlyList<PhysicalIndexDefinition>? indexes = null,
        int schemaVersion = 1,
        PhysicalEvolutionMetadata? evolution = null,
        IReadOnlyList<ProjectedColumnDefinition>? linkedProjectedColumns = null,
        string? linkedProjectionLogicalName = null) =>
        new(
            PhysicalStorageForm.DedicatedDocumentTable,
            featureDefaultLogicalName,
            null,
            envelope ?? new DocumentEnvelopeDefinition(),
            linkedProjectedColumns,
            indexes,
            schemaVersion,
            evolution,
            linkedProjectionLogicalName);

    public static PhysicalTableDefinition PhysicalEntityTable(
        string featureDefaultLogicalName,
        IReadOnlyList<ProjectedColumnDefinition> projectedColumns,
        DocumentEnvelopeDefinition? envelope = null,
        IReadOnlyList<PhysicalIndexDefinition>? indexes = null,
        int schemaVersion = 1,
        PhysicalEvolutionMetadata? evolution = null) =>
        new(
            PhysicalStorageForm.PhysicalEntityTable,
            featureDefaultLogicalName,
            null,
            envelope ?? new DocumentEnvelopeDefinition(),
            projectedColumns,
            indexes,
            schemaVersion,
            evolution,
            null);

    public bool Equals(PhysicalTableDefinition? other) =>
        other is not null &&
        Form == other.Form &&
        FeatureDefaultLogicalName == other.FeatureDefaultLogicalName &&
        SharedStorage == other.SharedStorage &&
        Envelope == other.Envelope &&
        ProjectedColumns.SequenceEqual(other.ProjectedColumns) &&
        Indexes.SequenceEqual(other.Indexes) &&
        SchemaVersion == other.SchemaVersion &&
        Evolution == other.Evolution &&
        LinkedProjectionLogicalName == other.LinkedProjectionLogicalName;

    public override bool Equals(object? obj) => Equals(obj as PhysicalTableDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Form);
        hash.Add(FeatureDefaultLogicalName, StringComparer.Ordinal);
        hash.Add(SharedStorage);
        hash.Add(Envelope);
        foreach (var column in ProjectedColumns)
            hash.Add(column);
        foreach (var index in Indexes)
            hash.Add(index);
        hash.Add(SchemaVersion);
        hash.Add(Evolution);
        hash.Add(LinkedProjectionLogicalName, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
