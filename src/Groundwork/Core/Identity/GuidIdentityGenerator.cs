namespace Groundwork.Core.Identity;

/// <summary>
/// Emits a random GUID as 32 lowercase hex characters (<c>"N"</c> format). Provided for parity with
/// callers that don't need ordering. <b>Not</b> sortable, so it is not recommended for indexed keys —
/// prefer <see cref="ShortIdentityGenerator"/> or <see cref="UuidV7IdentityGenerator"/> there.
/// </summary>
public sealed class GuidIdentityGenerator : IGroundworkIdentityGenerator
{
    public string Generate() => Guid.NewGuid().ToString("N");
}
