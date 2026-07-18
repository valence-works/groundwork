using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Documents.Store;

/// <summary>Stable kinds for the ordered database commands in one bounded-query invocation.</summary>
public enum PhysicalDocumentQueryCommandKind
{
    LinkedIdentityCollisionCheck,
    Count,
    Page,
    First,
    Any,
    PrimaryHydration
}

/// <summary>Stable command identities shared by provider implementations and diagnostic consumers.</summary>
public static class PhysicalDocumentQueryCommandIdentities
{
    public const string LinkedIdentityCollisionCheck = "linked-identity-collision-check";
    public const string Count = "count";
    public const string Page = "page";
    public const string First = "first";
    public const string Any = "any";
    public const string PrimaryHydration = "primary-hydration";
}

/// <summary>One provider-applied physical order term rendered into an exact database command.</summary>
public sealed class PhysicalDocumentQueryCommandOrder
{
    public PhysicalDocumentQueryCommandOrder(
        string fieldIdentifier,
        PhysicalSortDirection direction,
        bool isIdentityTieBreak)
    {
        FieldIdentifier = PhysicalDocumentQueryExplanation.RequireValue(
            fieldIdentifier,
            nameof(fieldIdentifier));
        Direction = direction;
        IsIdentityTieBreak = isIdentityTieBreak;
    }

    public string FieldIdentifier { get; }
    public PhysicalSortDirection Direction { get; }
    public bool IsIdentityTieBreak { get; }
}

/// <summary>Provider-native evidence for one exact database command in an invocation.</summary>
public sealed class PhysicalDocumentQueryCommandExplanation
{
    public PhysicalDocumentQueryCommandExplanation(
        PhysicalDocumentQueryCommandKind kind,
        string identity,
        string nativePlanFormat,
        string nativePlan,
        IReadOnlyList<string> predicateFieldIdentifiers,
        int? providerAppliedMaximumRows = null,
        IReadOnlyList<PhysicalDocumentQueryCommandOrder>? providerAppliedOrder = null)
    {
        Kind = kind;
        Identity = PhysicalDocumentQueryExplanation.RequireValue(identity, nameof(identity));
        NativePlanFormat = PhysicalDocumentQueryExplanation.RequireValue(nativePlanFormat, nameof(nativePlanFormat));
        NativePlan = PhysicalDocumentQueryExplanation.RequireValue(nativePlan, nameof(nativePlan));
        ArgumentNullException.ThrowIfNull(predicateFieldIdentifiers);
        PredicateFieldIdentifiers = Array.AsReadOnly(predicateFieldIdentifiers
            .Select(identifier => PhysicalDocumentQueryExplanation.RequireValue(identifier, nameof(predicateFieldIdentifiers)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray());
        if (providerAppliedMaximumRows is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(providerAppliedMaximumRows),
                "A provider-applied maximum row count must be positive.");
        ProviderAppliedMaximumRows = providerAppliedMaximumRows;
        ProviderAppliedOrder = Array.AsReadOnly((providerAppliedOrder ?? []).ToArray());
    }

    public PhysicalDocumentQueryCommandKind Kind { get; }
    public string Identity { get; }
    public string NativePlanFormat { get; }

    /// <summary>Unsanitized provider output. Treat as sensitive diagnostic data.</summary>
    public string NativePlan { get; }

    public IReadOnlyList<string> PredicateFieldIdentifiers { get; }

    /// <summary>
    /// Finite maximum result rows enforced by the exact rendered provider command, or
    /// <see langword="null"/> when that command does not apply a finite maximum.
    /// </summary>
    public int? ProviderAppliedMaximumRows { get; }

    /// <summary>Ordered physical fields rendered into the exact provider command.</summary>
    public IReadOnlyList<PhysicalDocumentQueryCommandOrder> ProviderAppliedOrder { get; }
}

/// <summary>Provider-native diagnostic evidence for one complete compiled bounded-query invocation.</summary>
/// <remarks>
/// Command native plans can contain query values, storage-scope values, physical names, or other
/// sensitive database metadata. Callers must sanitize them before logging or persisting them.
/// <see cref="RuntimeInvocationFingerprint"/> contains no raw values, but it is pseudonymous
/// correlation metadata rather than a secrecy boundary: low-entropy inputs can be guessed offline.
/// Consumers should protect the fingerprint as diagnostic metadata.
/// </remarks>
public sealed class PhysicalDocumentQueryExplanation
{
    public PhysicalDocumentQueryExplanation(
        PhysicalQueryPlan plan,
        string runtimeInvocationFingerprint,
        IReadOnlyList<PhysicalDocumentQueryCommandExplanation> commands)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        RuntimeInvocationFingerprint = RequireValue(runtimeInvocationFingerprint, nameof(runtimeInvocationFingerprint));
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("At least one explained database command is required.", nameof(commands));
        Commands = Array.AsReadOnly(commands.ToArray());
    }

    public PhysicalQueryPlan Plan { get; }

    /// <summary>
    /// Pseudonymous SHA-256 digest for the query, compiled route, and inherited scope. It exposes no
    /// raw value but remains guessable for low-entropy inputs and must still be protected.
    /// </summary>
    public string RuntimeInvocationFingerprint { get; }

    /// <summary>
    /// Planned production stages in execution order. Stages conditional on query shape are omitted
    /// when statically known not to execute; data-dependent early exits can still stop the operation.
    /// </summary>
    public IReadOnlyList<PhysicalDocumentQueryCommandExplanation> Commands { get; }

    internal static string RequireValue(string value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("A nonblank value is required.", parameterName);
}

/// <summary>Resolves the immutable compiled plan for the same runtime shape accepted by execution.</summary>
public interface IPhysicalDocumentQueryInspector
{
    PhysicalQueryPlan ResolvePlan(
        DocumentQuery query,
        BoundedQueryResultOperation operation = BoundedQueryResultOperation.Documents);
}

/// <summary>
/// Produces provider-native diagnostic plans for bounded-query invocations. Providers may execute
/// the exact bounded read commands to collect actual plans or derive bounded hydration identities,
/// so explain can be costly and belongs only on diagnostic paths. Native output is unsanitized and
/// sensitive; callers must protect or sanitize it before persistence or logging.
/// </summary>
public interface IPhysicalDocumentQueryExplainer : IPhysicalDocumentQueryInspector
{
    /// <summary>
    /// Explains the terminal operation selected by <see cref="DocumentQuery.ResultOperation"/>.
    /// This diagnostic call may execute exact bounded reads and return sensitive native output.
    /// </summary>
    Task<PhysicalDocumentQueryExplanation> ExplainAsync(
        DocumentQuery query,
        CancellationToken cancellationToken = default);
}
