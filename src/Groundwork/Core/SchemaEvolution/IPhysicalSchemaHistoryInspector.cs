namespace Groundwork.Core.SchemaEvolution;

/// <summary>
/// Reads a point-in-time provider-state snapshot without acquiring an application lock, creating
/// Groundwork infrastructure, recording operation evidence, or changing the physical target.
/// Inspection is suitable for readiness validation only; application must reread and authorize the
/// exact plan while holding <see cref="IPhysicalSchemaApplicationLock"/>.
/// </summary>
public interface IPhysicalSchemaHistoryInspector
{
    ValueTask<PhysicalSchemaHistoryState> InspectHistoryAsync(
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken);
}
