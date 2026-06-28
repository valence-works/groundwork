namespace Groundwork.Core.Identity;

/// <summary>
/// Emits a UUID v7 as 32 lowercase hex characters (<c>"N"</c> format). 128 bits, effectively
/// collision-free, and sortable by its canonical string because the high bits carry a millisecond
/// timestamp. Use when you want full UUID width with chronological ordering.
/// </summary>
public sealed class UuidV7IdentityGenerator(TimeProvider? timeProvider = null) : IGroundworkIdentityGenerator
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public string Generate() => Guid.CreateVersion7(timeProvider.GetUtcNow()).ToString("N");
}
