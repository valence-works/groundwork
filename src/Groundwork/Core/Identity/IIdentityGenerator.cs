namespace Groundwork.Core.Identity;

/// <summary>
/// Generates the string identifiers Groundwork creates internally (operational message ids, lease
/// tokens, …). Implementations are interchangeable; consumers pick one and pass it through the
/// relevant store constructor. The default catalog lives alongside this interface in
/// <see cref="Groundwork.Core.Identity"/>.
/// </summary>
public interface IIdentityGenerator
{
    /// <summary>Produces a new identifier. Must be safe to call from multiple threads concurrently.</summary>
    string Generate();
}
