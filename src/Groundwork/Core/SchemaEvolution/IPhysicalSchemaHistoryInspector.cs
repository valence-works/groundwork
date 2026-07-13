namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Reads a point-in-time provider-state snapshot and validates the physical objects described by
/// its durable applied routes without acquiring an application lock, creating Groundwork
/// infrastructure, recording operation evidence, or changing the physical target. Desired routes
/// are diff input only and are never executed by inspection. Application must reread and authorize
/// the exact plan while holding <see cref="IPhysicalSchemaApplicationLock"/>.
/// </summary>
public interface IPhysicalSchemaHistoryInspector
{
    ValueTask<PhysicalSchemaInspectionResult> InspectHistoryAsync(
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// Point-in-time durable history plus the compatibility of the physical objects described by that
/// applied history. Desired target operations are never part of inspection.
/// </summary>
public sealed record PhysicalSchemaInspectionResult(
    PhysicalSchemaHistoryState History,
    bool IsAppliedSchemaValid);
