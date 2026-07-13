using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Materialization;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.SchemaEvolution;

public enum PhysicalSchemaOperationKind
{
    CreatePrimaryStorage,
    CreateLinkedStorage,
    CreatePhysicalEntityStorage,
    AddProjectedColumn,
    FinalizeProjectedColumn,
    CreatePhysicalIndex,
    BackfillCanonicalJson,
    ValidatePhysicalSchema,
    RecordAppliedState
}

public enum CanonicalJsonBackfillSubjectKind
{
    ProjectedColumn,
    PhysicalIndex,
    LogicalIndex
}

/// <summary>
/// One immutable provider-neutral schema operation. Identity and fingerprint are derived only from
/// its semantic payload, so retries and restarts reproduce the same operation exactly.
/// </summary>
public abstract class PhysicalSchemaOperation
{
    protected PhysicalSchemaOperation(
        PhysicalSchemaOperationKind kind,
        StorageUnitIdentity? storageUnit,
        string subjectIdentity,
        params string?[] semanticParts)
        : this(kind, storageUnit, subjectIdentity, null, semanticParts)
    {
    }

    protected PhysicalSchemaOperation(
        PhysicalSchemaOperationKind kind,
        StorageUnitIdentity? storageUnit,
        string subjectIdentity,
        string? slotIdentity,
        IReadOnlyList<string?> semanticParts)
    {
        Kind = kind;
        StorageUnit = storageUnit;
        SubjectIdentity = subjectIdentity;
        SlotIdentity = slotIdentity ?? CreateSlotIdentity(kind, storageUnit, subjectIdentity);
        CanonicalPayload = PhysicalSchemaFingerprint.Canonicalize(
            [kind.ToString(), storageUnit?.Value, subjectIdentity, SlotIdentity, .. semanticParts]);
        Fingerprint = PhysicalSchemaFingerprint.CreateCanonical(CanonicalPayload);
        Identity = CreateIdentity(kind, storageUnit, subjectIdentity, Fingerprint);
    }

    public PhysicalSchemaOperationKind Kind { get; }

    public StorageUnitIdentity? StorageUnit { get; }

    public string SubjectIdentity { get; }

    public string Identity { get; }

    public string Fingerprint { get; }

    /// <summary>Canonical semantic payload used to verify durable operation fingerprints.</summary>
    public string CanonicalPayload { get; }

    /// <summary>
    /// Stable semantic slot used to distinguish additive work from a mutation. Content changes
    /// alter <see cref="Identity"/> while retaining this slot; independent operations must have
    /// independent slots.
    /// </summary>
    public string SlotIdentity { get; }

    internal static string CreateIdentity(
        PhysicalSchemaOperationKind kind,
        StorageUnitIdentity? storageUnit,
        string subjectIdentity,
        string fingerprint) =>
        $"{ToKebabCase(kind.ToString())}:{storageUnit?.Value ?? "manifest"}:{subjectIdentity}:{fingerprint[..16]}";

    internal static string CreateSlotIdentity(
        PhysicalSchemaOperationKind kind,
        StorageUnitIdentity? storageUnit,
        string subjectIdentity,
        params string?[] discriminators) =>
        $"{ToKebabCase(kind.ToString())}:{PhysicalSchemaFingerprint.Create(
            [kind.ToString(), storageUnit?.Value, subjectIdentity, .. discriminators])}";

    private static string ToKebabCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index != 0 && char.IsUpper(character))
                result.Append('-');
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }
}

public sealed class CreatePrimaryStorageOperation : PhysicalSchemaOperation
{
    internal CreatePrimaryStorageOperation(ExecutableStorageRoute route)
        : base(
            PhysicalSchemaOperationKind.CreatePrimaryStorage,
            route.Form == PhysicalStorageForm.SharedDocuments ? null : route.StorageUnit,
            route.PrimaryStorage.Name.LogicalName,
            PhysicalSchemaOperationCanonicalizer.PrimaryStorage(route)) => Route = route;

    public ExecutableStorageRoute Route { get; }

    public ExecutableStorageObjectRoute Storage => Route.PrimaryStorage;
}

public sealed class CreateLinkedStorageOperation : PhysicalSchemaOperation
{
    internal CreateLinkedStorageOperation(ExecutableStorageRoute route)
        : base(
            PhysicalSchemaOperationKind.CreateLinkedStorage,
            route.StorageUnit,
            route.LinkedIndexStorage!.Name.LogicalName,
            PhysicalSchemaOperationCanonicalizer.LinkedStorage(route)) => Route = route;

    public ExecutableStorageRoute Route { get; }

    public ExecutableStorageObjectRoute Storage => Route.LinkedIndexStorage!;
}

public sealed class CreatePhysicalEntityStorageOperation : PhysicalSchemaOperation
{
    internal CreatePhysicalEntityStorageOperation(ExecutableStorageRoute route)
        : base(
            PhysicalSchemaOperationKind.CreatePhysicalEntityStorage,
            route.StorageUnit,
            route.PrimaryStorage.Name.LogicalName,
            PhysicalSchemaOperationCanonicalizer.PrimaryStorage(route)) => Route = route;

    public ExecutableStorageRoute Route { get; }

    public ExecutableStorageObjectRoute Storage => Route.PrimaryStorage;
}

public sealed class AddProjectedColumnOperation : PhysicalSchemaOperation
{
    internal AddProjectedColumnOperation(ExecutableStorageRoute route, ExecutableProjectedColumnRoute column)
        : base(
            PhysicalSchemaOperationKind.AddProjectedColumn,
            route.StorageUnit,
            column.Definition.LogicalName,
            PhysicalSchemaOperationCanonicalizer.ProjectedColumn(column))
    {
        Route = route;
        Column = column;
    }

    public ExecutableStorageRoute Route { get; }

    public ExecutableProjectedColumnRoute Column { get; }

    public ExecutableStorageObjectRoute Storage => PhysicalSchemaOperationStorage.Resolve(Route, Column.Target);
}

/// <summary>
/// Makes a staged projected column enforce its declared nullability after canonical JSON has been
/// backfilled. Providers may implement this as an ALTER or as an atomic table rebuild.
/// </summary>
public sealed class FinalizeProjectedColumnOperation : PhysicalSchemaOperation
{
    internal FinalizeProjectedColumnOperation(ExecutableStorageRoute route, ExecutableProjectedColumnRoute column)
        : base(
            PhysicalSchemaOperationKind.FinalizeProjectedColumn,
            route.StorageUnit,
            column.Definition.LogicalName,
            PhysicalSchemaOperationCanonicalizer.ProjectedColumn(column))
    {
        Route = route;
        Column = column;
    }

    public ExecutableStorageRoute Route { get; }

    public ExecutableProjectedColumnRoute Column { get; }

    public ExecutableStorageObjectRoute Storage => PhysicalSchemaOperationStorage.Resolve(Route, Column.Target);
}

public sealed class CreatePhysicalIndexOperation : PhysicalSchemaOperation
{
    internal CreatePhysicalIndexOperation(ExecutableStorageRoute route, ExecutablePhysicalIndexRoute index)
        : base(
            PhysicalSchemaOperationKind.CreatePhysicalIndex,
            route.StorageUnit,
            index.Identity,
            PhysicalSchemaOperationCanonicalizer.Index(index))
    {
        Route = route;
        Index = index;
    }

    public ExecutableStorageRoute Route { get; }

    public ExecutablePhysicalIndexRoute Index { get; }

    public ExecutableStorageObjectRoute Storage => PhysicalSchemaOperationStorage.Resolve(Route, Index.Target);
}

public sealed class BackfillCanonicalJsonOperation : PhysicalSchemaOperation, IProviderMaterializationOperation
{
    internal BackfillCanonicalJsonOperation(
        ExecutableStorageRoute route,
        ExecutableStorageObjectRole target,
        CanonicalJsonBackfillSubjectKind subjectKind,
        string subjectIdentity,
        IReadOnlyList<string> sourcePaths,
        string subjectFingerprint)
        : this(
            route.StorageUnit,
            route,
            target,
            subjectKind,
            subjectIdentity,
            sourcePaths,
            [subjectFingerprint],
            null)
    {
    }

    private BackfillCanonicalJsonOperation(
        StorageUnitIdentity storageUnit,
        ExecutableStorageRoute? route,
        ExecutableStorageObjectRole target,
        CanonicalJsonBackfillSubjectKind subjectKind,
        string subjectIdentity,
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string?> semanticParts,
        IndexDeclaration? logicalIndex)
        : base(
            PhysicalSchemaOperationKind.BackfillCanonicalJson,
            storageUnit,
            subjectIdentity,
            CreateSlotIdentity(
                PhysicalSchemaOperationKind.BackfillCanonicalJson,
                storageUnit,
                subjectIdentity,
                target.ToString(),
                subjectKind.ToString()),
            [
                target.ToString(),
                subjectKind.ToString(),
                .. semanticParts,
                .. sourcePaths.Order(StringComparer.Ordinal)
            ])
    {
        Route = route;
        Target = target;
        SubjectKind = subjectKind;
        SourcePaths = Array.AsReadOnly(sourcePaths.Order(StringComparer.Ordinal).ToArray());
        LogicalIndex = logicalIndex;
    }

    public ExecutableStorageRoute? Route { get; }

    public ExecutableStorageObjectRole Target { get; }

    public CanonicalJsonBackfillSubjectKind SubjectKind { get; }

    public IReadOnlyList<string> SourcePaths { get; }

    public IndexDeclaration? LogicalIndex { get; }

    public ExecutableStorageObjectRoute? Storage => Route is null
        ? null
        : PhysicalSchemaOperationStorage.Resolve(Route, Target);

    MaterializationOperationKind IProviderMaterializationOperation.Kind =>
        MaterializationOperationKind.BackfillCanonicalJson;

    string IProviderMaterializationOperation.Target =>
        $"{StorageUnit!.Value}.{SubjectIdentity}.backfill-canonical-json";

    public static BackfillCanonicalJsonOperation ForLogicalIndex(
        StorageUnitIdentity storageUnit,
        IndexDeclaration index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var semanticParts = new string?[]
        {
            index.ValueKind.ToString(),
            index.IsUnique.ToString(CultureInfo.InvariantCulture),
            index.IsSortable.ToString(CultureInfo.InvariantCulture),
            index.MissingValueBehavior.ToString()
        };
        return new BackfillCanonicalJsonOperation(
            storageUnit,
            null,
            ExecutableStorageObjectRole.LinkedIndexStorage,
            CanonicalJsonBackfillSubjectKind.LogicalIndex,
            index.Identity,
            index.Fields.Select(field => field.Path).ToArray(),
            semanticParts,
            index);
    }

}

public sealed class ValidatePhysicalSchemaOperation : PhysicalSchemaOperation
{
    internal ValidatePhysicalSchemaOperation(string targetFingerprint, IReadOnlyList<ExecutableStorageRoute> routes)
        : base(
            PhysicalSchemaOperationKind.ValidatePhysicalSchema,
            null,
            "target",
            [targetFingerprint, .. routes.Select(route => route.Fingerprint).Order(StringComparer.Ordinal)])
    {
        TargetFingerprint = targetFingerprint;
        Routes = Array.AsReadOnly(routes.OrderBy(route => route.StorageUnit.Value, StringComparer.Ordinal).ToArray());
        RouteFingerprints = Array.AsReadOnly(Routes.Select(route => route.Fingerprint).ToArray());
    }

    public string TargetFingerprint { get; }

    public IReadOnlyList<ExecutableStorageRoute> Routes { get; }

    public IReadOnlyList<string> RouteFingerprints { get; }
}

internal static class PhysicalSchemaOperationStorage
{
    public static ExecutableStorageObjectRoute Resolve(
        ExecutableStorageRoute route,
        ExecutableStorageObjectRole target) => target switch
        {
            ExecutableStorageObjectRole.PrimaryStorage => route.PrimaryStorage,
            ExecutableStorageObjectRole.LinkedIndexStorage => route.LinkedIndexStorage ??
                throw new InvalidOperationException($"Route '{route.StorageUnit.Value}' has no linked index storage."),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
}

internal static class PhysicalSchemaOperationIntegrity
{
    public static void Validate(AppliedSemanticOperationSnapshot operation) => Validate(
        operation.Identity,
        operation.Fingerprint,
        operation.Kind,
        operation.StorageUnit,
        operation.SubjectIdentity,
        operation.SlotIdentity,
        operation.CanonicalPayload);

    public static void Validate(PhysicalSchemaAppliedOperation operation) => Validate(
        operation.Identity,
        operation.Fingerprint,
        operation.Kind,
        operation.StorageUnit,
        operation.SubjectIdentity,
        operation.SlotIdentity,
        operation.CanonicalPayload);

    private static void Validate(
        string identity,
        string fingerprint,
        PhysicalSchemaOperationKind kind,
        StorageUnitIdentity? storageUnit,
        string subjectIdentity,
        string slotIdentity,
        string canonicalPayload)
    {
        if (!PhysicalSchemaFingerprint.TryParseCanonical(canonicalPayload, out var semanticParts) ||
            semanticParts.Count < 4 ||
            semanticParts[0] != kind.ToString() ||
            semanticParts[1] != storageUnit?.Value ||
            semanticParts[2] != subjectIdentity ||
            !TryCreateExpectedSlotIdentity(kind, storageUnit, subjectIdentity, semanticParts, out var expectedSlotIdentity))
        {
            throw InconsistentOperation(identity);
        }

        var expectedFingerprint = PhysicalSchemaFingerprint.CreateCanonical(canonicalPayload);
        if (semanticParts[3] != slotIdentity ||
            expectedSlotIdentity != slotIdentity ||
            expectedFingerprint != fingerprint ||
            PhysicalSchemaOperation.CreateIdentity(kind, storageUnit, subjectIdentity, fingerprint) != identity)
        {
            throw InconsistentOperation(identity);
        }
    }

    private static bool TryCreateExpectedSlotIdentity(
        PhysicalSchemaOperationKind kind,
        StorageUnitIdentity? storageUnit,
        string subjectIdentity,
        IReadOnlyList<string?> semanticParts,
        out string slotIdentity)
    {
        if (kind != PhysicalSchemaOperationKind.BackfillCanonicalJson)
        {
            slotIdentity = PhysicalSchemaOperation.CreateSlotIdentity(kind, storageUnit, subjectIdentity);
            return true;
        }

        if (semanticParts.Count < 6 ||
            !Enum.TryParse<ExecutableStorageObjectRole>(semanticParts[4], out var target) ||
            !Enum.IsDefined(target) ||
            semanticParts[4] != target.ToString() ||
            !Enum.TryParse<CanonicalJsonBackfillSubjectKind>(semanticParts[5], out var subjectKind) ||
            !Enum.IsDefined(subjectKind) ||
            semanticParts[5] != subjectKind.ToString())
        {
            slotIdentity = string.Empty;
            return false;
        }

        slotIdentity = PhysicalSchemaOperation.CreateSlotIdentity(
            kind,
            storageUnit,
            subjectIdentity,
            target.ToString(),
            subjectKind.ToString());
        return true;
    }

    private static InvalidOperationException InconsistentOperation(string identity) =>
        new($"Applied operation '{identity}' has inconsistent identity, slot, payload, or fingerprint evidence.");
}

public sealed class RecordPhysicalSchemaAppliedStateOperation : PhysicalSchemaOperation
{
    internal RecordPhysicalSchemaAppliedStateOperation(string targetFingerprint)
        : base(PhysicalSchemaOperationKind.RecordAppliedState, null, "target", targetFingerprint) =>
        TargetFingerprint = targetFingerprint;

    public string TargetFingerprint { get; }
}

internal static class PhysicalSchemaOperationCanonicalizer
{
    public static string PrimaryStorage(ExecutableStorageRoute route) => string.Join(
        '\u001f',
        new[]
        {
            Storage(route.PrimaryStorage),
            Column(route.Envelope.Id),
            Column(route.Envelope.DocumentKind),
            Column(route.Envelope.StorageScope),
            Column(route.Envelope.Version),
            Column(route.Envelope.SchemaVersion),
            Column(route.Envelope.CanonicalJson),
            Key(route.PrimaryKey),
            Column(route.Discriminator.Column),
            route.Discriminator.ParticipatesInPrimaryKey.ToString(CultureInfo.InvariantCulture),
            Column(route.ScopeKey.Column),
            route.ScopeKey.ParticipatesInPrimaryKey.ToString(CultureInfo.InvariantCulture)
        });

    public static string LinkedStorage(ExecutableStorageRoute route) => string.Join(
        '\u001f',
        new[]
        {
            Storage(route.LinkedIndexStorage!),
            Column(route.LinkedRelationship!.DocumentId),
            Column(route.LinkedRelationship.DocumentKind),
            Column(route.LinkedRelationship.StorageScope),
            Key(route.AuxiliaryKey!)
        });

    public static string Storage(ExecutableStorageObjectRoute storage) => string.Join(
        '\u001f',
        storage.Role.ToString(),
        storage.Name.ObjectKind.ToString(),
        storage.Name.FeatureDefaultLogicalName,
        storage.Name.LogicalName,
        storage.Name.Identifier,
        storage.Name.CollisionScope,
        storage.Name.NamingOwner.Value,
        storage.SchemaVersion.ToString(CultureInfo.InvariantCulture),
        storage.Evolution?.RequiresBackfill.ToString(CultureInfo.InvariantCulture),
        storage.Evolution?.IsDestructive.ToString(CultureInfo.InvariantCulture),
        storage.Evolution?.SemanticMigrationIdentity);

    public static string ProjectedColumn(ExecutableProjectedColumnRoute column) => string.Join(
        '\u001f',
        column.Target.ToString(),
        column.Name.Identifier,
        column.Column.LogicalName,
        column.Column.Identifier,
        column.Definition.LogicalName,
        column.Definition.Path,
        column.Definition.Type.ToString(),
        column.Definition.Length?.ToString(CultureInfo.InvariantCulture),
        column.Definition.Precision?.ToString(CultureInfo.InvariantCulture),
        column.Definition.Scale?.ToString(CultureInfo.InvariantCulture),
        column.Definition.IsNullable.ToString(CultureInfo.InvariantCulture),
        column.Definition.Collation,
        column.Definition.DefaultValue,
        column.Definition.RebuildMode.ToString());

    public static string Index(ExecutablePhysicalIndexRoute index) => string.Join(
        '\u001f',
        new string?[]
            {
                index.Name.Identifier,
                index.Target.ToString(),
                index.IsUnique.ToString(CultureInfo.InvariantCulture),
                index.MissingValueBehavior.ToString(),
                index.Definition.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                index.Definition.Evolution?.RequiresBackfill.ToString(CultureInfo.InvariantCulture),
                index.Definition.Evolution?.IsDestructive.ToString(CultureInfo.InvariantCulture),
                index.Definition.Evolution?.SemanticMigrationIdentity
            }
            .Concat(index.Columns
                .OrderBy(column => column.Order)
                .SelectMany(column => new[]
                {
                    column.Column.LogicalName,
                    column.Column.Identifier,
                    column.Order.ToString(CultureInfo.InvariantCulture),
                    column.Direction.ToString()
                })));

    private static string Column(ExecutableColumnRoute column) =>
        $"{column.LogicalName}\u001e{column.Identifier}";

    private static string Key(ExecutableKeyRoute key) => string.Join(
        '\u001e',
        key.Columns.Select(Column));
}

internal static class PhysicalSchemaFingerprint
{
    public static string Create(IEnumerable<string?> parts) => CreateCanonical(Canonicalize(parts));

    public static string Canonicalize(IEnumerable<string?> parts) =>
        string.Concat(parts.Select(part =>
            $"{(part?.Length ?? -1).ToString(CultureInfo.InvariantCulture)}:{part ?? string.Empty};"));

    public static string CreateCanonical(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    public static bool TryParseCanonical(string canonical, out IReadOnlyList<string?> parts)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var parsed = new List<string?>();
        var position = 0;
        while (position < canonical.Length)
        {
            var separator = canonical.IndexOf(':', position);
            if (separator < position ||
                !int.TryParse(
                    canonical.AsSpan(position, separator - position),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var length) ||
                length < -1)
            {
                parts = [];
                return false;
            }

            position = separator + 1;
            if (length == -1)
            {
                if (position >= canonical.Length || canonical[position] != ';')
                {
                    parts = [];
                    return false;
                }
                parsed.Add(null);
                position++;
                continue;
            }

            if (canonical.Length - position <= length || canonical[position + length] != ';')
            {
                parts = [];
                return false;
            }

            parsed.Add(canonical.Substring(position, length));
            position += length + 1;
        }

        parts = parsed;
        return true;
    }
}
