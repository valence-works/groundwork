using Groundwork.Core.Capabilities;

namespace Groundwork.Core.Migrations;

public interface IGroundworkMigration
{
    string Identity { get; }

    long Version { get; }

    string Description { get; }

    IReadOnlyList<GroundworkMigrationOperation> Operations { get; }
}

public sealed record GroundworkMigration(
    string Identity,
    long Version,
    string Description,
    IReadOnlyList<GroundworkMigrationOperation> Operations) : IGroundworkMigration;

public sealed record GroundworkMigrationOperation(
    string Identity,
    GroundworkMigrationOperationKind Kind,
    string Target,
    IReadOnlyDictionary<string, string> Metadata)
{
    public bool IsDestructive =>
        Kind is GroundworkMigrationOperationKind.DropStorageUnit
            or GroundworkMigrationOperationKind.DropIndex
            or GroundworkMigrationOperationKind.DropOptimizedProjection
            or GroundworkMigrationOperationKind.ProviderDestructive;

    public static GroundworkMigrationOperation ProviderSql(
        string identity,
        string sql,
        string target = "provider-sql",
        bool destructive = false) =>
        new(
            identity,
            destructive ? GroundworkMigrationOperationKind.ProviderDestructive : GroundworkMigrationOperationKind.ProviderSql,
            target,
            new Dictionary<string, string> { ["sql"] = sql });
}

public enum GroundworkMigrationOperationKind
{
    CreateStorageUnit,
    CreateIndex,
    CreateOptimizedProjection,
    BackfillDocuments,
    BackfillOptimizedProjection,
    TransformDocuments,
    ProviderSql,
    ProviderDestructive,
    DropIndex,
    DropOptimizedProjection,
    DropStorageUnit
}

public sealed record GroundworkMigrationRecord(
    string Identity,
    long Version,
    ProviderIdentity Provider,
    DateTimeOffset AppliedUtc,
    string Description);

public sealed record GroundworkMigrationExecutionOptions(
    bool DryRun = false,
    bool AllowDestructive = false);

public sealed record GroundworkMigrationResult(
    IReadOnlyList<GroundworkMigrationRecord> Applied,
    IReadOnlyList<IGroundworkMigration> Pending,
    IReadOnlyList<string> Diagnostics)
{
    public bool HasErrors => Diagnostics.Count != 0;
}
