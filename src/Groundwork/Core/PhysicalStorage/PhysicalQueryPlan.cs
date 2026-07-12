using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>Server-side field sources for which a provider has an executable query handler.</summary>
public enum PhysicalQuerySourceKind
{
    LinkedIndex,
    PrimaryEnvelope,
    PrimaryCanonicalJson,
    PrimaryProjectedColumns,
    NativeDocumentFields
}

/// <summary>The selected provider-neutral access strategy exposed through plan diagnostics.</summary>
public enum PhysicalQueryAccessKind
{
    LinkedIndexThenPrimary,
    PrimaryEnvelope,
    PrimaryCanonicalJson,
    PrimaryProjectedColumns,
    NativeDocumentFields
}

public enum PhysicalQueryFieldSource
{
    Envelope,
    LinkedRelationship,
    CanonicalJsonPath,
    ProjectedColumn,
    NativeDocumentField
}

/// <summary>
/// Provider-owned executable query-handler profile. Preference order is binding and is used as
/// supplied; Core never guesses which of a provider's handlers is preferable.
/// </summary>
public sealed class PhysicalQueryPlannerCapabilities : IEquatable<PhysicalQueryPlannerCapabilities>
{
    public PhysicalQueryPlannerCapabilities(
        ProviderIdentity provider,
        IReadOnlyList<PhysicalQuerySourceKind> sourcePreference,
        IReadOnlySet<PortableQueryOperation> supportedOperations,
        IReadOnlyDictionary<PhysicalQuerySourceKind, string> handlerIdentities,
        IReadOnlyDictionary<string, string>? nativeFieldIdentifiers,
        bool supportsCompoundPredicates,
        bool supportsDisjunction,
        bool supportsOffsetPaging,
        bool supportsKeysetPaging,
        bool supportsCount,
        bool supportsAny,
        bool supportsFirst,
        bool supportsLatestPerKey,
        IReadOnlyDictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>>? sourceValueKinds = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        SourcePreference = Array.AsReadOnly((sourcePreference ?? throw new ArgumentNullException(nameof(sourcePreference)))
            .Distinct()
            .ToArray());
        SupportedOperations = (supportedOperations ?? throw new ArgumentNullException(nameof(supportedOperations))).ToFrozenSet();
        HandlerIdentities = (handlerIdentities ?? throw new ArgumentNullException(nameof(handlerIdentities)))
            .ToFrozenDictionary();
        NativeFieldIdentifiers = (nativeFieldIdentifiers ?? new Dictionary<string, string>())
            .ToFrozenDictionary(StringComparer.Ordinal);
        SourceValueKinds = (sourceValueKinds ?? new Dictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>>())
            .ToFrozenDictionary(item => item.Key, item => (IReadOnlySet<IndexValueKind>)item.Value.ToFrozenSet());
        if (SourcePreference.Any(source =>
                !HandlerIdentities.TryGetValue(source, out var identity) || string.IsNullOrWhiteSpace(identity)))
        {
            throw new ArgumentException("Every preferred source must name its executable handler.", nameof(handlerIdentities));
        }
        if (NativeFieldIdentifiers.Any(field =>
                string.IsNullOrWhiteSpace(field.Key) || string.IsNullOrWhiteSpace(field.Value)))
        {
            throw new ArgumentException("Native field mappings require non-empty stable paths and provider identifiers.", nameof(nativeFieldIdentifiers));
        }
        SupportsCompoundPredicates = supportsCompoundPredicates;
        SupportsDisjunction = supportsDisjunction;
        SupportsOffsetPaging = supportsOffsetPaging;
        SupportsKeysetPaging = supportsKeysetPaging;
        SupportsCount = supportsCount;
        SupportsAny = supportsAny;
        SupportsFirst = supportsFirst;
        SupportsLatestPerKey = supportsLatestPerKey;
    }

    public ProviderIdentity Provider { get; }
    public IReadOnlyList<PhysicalQuerySourceKind> SourcePreference { get; }
    public IReadOnlySet<PortableQueryOperation> SupportedOperations { get; }
    public IReadOnlyDictionary<PhysicalQuerySourceKind, string> HandlerIdentities { get; }
    public IReadOnlyDictionary<string, string> NativeFieldIdentifiers { get; }
    public IReadOnlyDictionary<PhysicalQuerySourceKind, IReadOnlySet<IndexValueKind>> SourceValueKinds { get; }
    public bool SupportsCompoundPredicates { get; }
    public bool SupportsDisjunction { get; }
    public bool SupportsOffsetPaging { get; }
    public bool SupportsKeysetPaging { get; }
    public bool SupportsCount { get; }
    public bool SupportsAny { get; }
    public bool SupportsFirst { get; }
    public bool SupportsLatestPerKey { get; }

    public bool Equals(PhysicalQueryPlannerCapabilities? other) =>
        other is not null &&
        Provider == other.Provider &&
        SourcePreference.SequenceEqual(other.SourcePreference) &&
        SupportedOperations.SetEquals(other.SupportedOperations) &&
        DictionaryEquals(HandlerIdentities, other.HandlerIdentities) &&
        DictionaryEquals(NativeFieldIdentifiers, other.NativeFieldIdentifiers) &&
        SetDictionaryEquals(SourceValueKinds, other.SourceValueKinds) &&
        SupportsCompoundPredicates == other.SupportsCompoundPredicates &&
        SupportsDisjunction == other.SupportsDisjunction &&
        SupportsOffsetPaging == other.SupportsOffsetPaging &&
        SupportsKeysetPaging == other.SupportsKeysetPaging &&
        SupportsCount == other.SupportsCount &&
        SupportsAny == other.SupportsAny &&
        SupportsFirst == other.SupportsFirst &&
        SupportsLatestPerKey == other.SupportsLatestPerKey;

    public override bool Equals(object? obj) => Equals(obj as PhysicalQueryPlannerCapabilities);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Provider);
        foreach (var source in SourcePreference)
            hash.Add(source);
        foreach (var operation in SupportedOperations.Order())
            hash.Add(operation);
        AddDictionaryHash(ref hash, HandlerIdentities);
        AddDictionaryHash(ref hash, NativeFieldIdentifiers);
        foreach (var item in SourceValueKinds.OrderBy(item => item.Key))
        {
            hash.Add(item.Key);
            foreach (var kind in item.Value.Order())
                hash.Add(kind);
        }
        hash.Add(SupportsCompoundPredicates);
        hash.Add(SupportsDisjunction);
        hash.Add(SupportsOffsetPaging);
        hash.Add(SupportsKeysetPaging);
        hash.Add(SupportsCount);
        hash.Add(SupportsAny);
        hash.Add(SupportsFirst);
        hash.Add(SupportsLatestPerKey);
        return hash.ToHashCode();
    }

    private static bool DictionaryEquals<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> first,
        IReadOnlyDictionary<TKey, TValue> second) where TKey : notnull =>
        first.Count == second.Count && first.All(item =>
            second.TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(item.Value, value));

    private static void AddDictionaryHash<TKey, TValue>(
        ref HashCode hash,
        IReadOnlyDictionary<TKey, TValue> dictionary) where TKey : notnull
    {
        foreach (var item in dictionary.OrderBy(item => item.Key))
        {
            hash.Add(item.Key);
            hash.Add(item.Value);
        }
    }

    public bool Supports(PhysicalQuerySourceKind source, IndexValueKind valueKind) =>
        !SourceValueKinds.TryGetValue(source, out var supported) || supported.Contains(valueKind);

    private static bool SetDictionaryEquals<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlySet<TValue>> first,
        IReadOnlyDictionary<TKey, IReadOnlySet<TValue>> second) where TKey : notnull =>
        first.Count == second.Count && first.All(item =>
            second.TryGetValue(item.Key, out var value) && item.Value.SetEquals(value));
}

public sealed record PhysicalQueryField(
    string Path,
    string Identifier,
    PhysicalQueryFieldSource Source,
    ExecutableStorageObjectRole Target,
    ProviderPhysicalObjectName ObjectName,
    IndexValueKind ValueKind);

public sealed record PhysicalQueryScope(
    PhysicalQueryField Field,
    StorageScopePolicy Policy,
    bool IsMandatory,
    bool UsesGlobalSentinel);

public sealed record PhysicalQueryPredicate(
    string Path,
    PhysicalQueryField Field,
    IReadOnlySet<PortableQueryOperation> Operations)
{
    public bool Equals(PhysicalQueryPredicate? other) =>
        other is not null &&
        Path == other.Path &&
        Field == other.Field &&
        Operations.SetEquals(other.Operations);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Path, StringComparer.Ordinal);
        hash.Add(Field);
        foreach (var operation in Operations.Order())
            hash.Add(operation);
        return hash.ToHashCode();
    }
}

public sealed record PhysicalQueryOrder(
    string Path,
    PhysicalQueryField Field,
    PhysicalSortDirection Direction,
    bool IsIdentityTieBreak);

/// <summary>Immutable provider output for one declared bounded query.</summary>
public sealed class PhysicalQueryPlan : IEquatable<PhysicalQueryPlan>
{
    internal PhysicalQueryPlan(
        StorageUnitIdentity storageUnit,
        string queryIdentity,
        string logicalIndexIdentity,
        IReadOnlyList<string> logicalIndexPaths,
        string handlerIdentity,
        ProviderIdentity provider,
        PhysicalStorageForm form,
        PhysicalQueryAccessKind accessKind,
        ProviderPhysicalObjectName lookupObject,
        ProviderPhysicalObjectName primaryObject,
        ProviderPhysicalObjectName? indexName,
        PhysicalQueryScope scope,
        PhysicalQueryField discriminator,
        IReadOnlyList<PhysicalQueryPredicate> predicates,
        IReadOnlyList<PhysicalQueryOrder> order,
        IReadOnlyList<string> requiredEqualityPrefixPaths,
        QueryPagingSupport pagingSupport,
        IReadOnlySet<BoundedQueryResultOperation> resultOperations,
        bool supportsDisjunction,
        string? latestPerKeyPath,
        bool requiresPrimaryLookup,
        bool isScaleBearing,
        string routeFingerprint,
        string fingerprint)
    {
        StorageUnit = storageUnit;
        QueryIdentity = queryIdentity;
        LogicalIndexIdentity = logicalIndexIdentity;
        LogicalIndexPaths = Array.AsReadOnly(logicalIndexPaths.ToArray());
        HandlerIdentity = handlerIdentity;
        Provider = provider;
        Form = form;
        AccessKind = accessKind;
        LookupObject = lookupObject;
        PrimaryObject = primaryObject;
        IndexName = indexName;
        Scope = scope;
        Discriminator = discriminator;
        Predicates = Array.AsReadOnly(predicates.ToArray());
        Order = Array.AsReadOnly(order.ToArray());
        RequiredEqualityPrefixPaths = Array.AsReadOnly(requiredEqualityPrefixPaths.ToArray());
        PagingSupport = pagingSupport;
        ResultOperations = resultOperations.ToFrozenSet();
        SupportsDisjunction = supportsDisjunction;
        LatestPerKeyPath = latestPerKeyPath;
        RequiresPrimaryLookup = requiresPrimaryLookup;
        IsScaleBearing = isScaleBearing;
        RouteFingerprint = routeFingerprint;
        Fingerprint = fingerprint;
    }

    public StorageUnitIdentity StorageUnit { get; }
    public string QueryIdentity { get; }
    public string LogicalIndexIdentity { get; }
    public IReadOnlyList<string> LogicalIndexPaths { get; }
    public string HandlerIdentity { get; }
    public ProviderIdentity Provider { get; }
    public PhysicalStorageForm Form { get; }
    public PhysicalQueryAccessKind AccessKind { get; }
    public ProviderPhysicalObjectName LookupObject { get; }
    public ProviderPhysicalObjectName PrimaryObject { get; }
    public ProviderPhysicalObjectName? IndexName { get; }
    public PhysicalQueryScope Scope { get; }
    public PhysicalQueryField Discriminator { get; }
    public IReadOnlyList<PhysicalQueryPredicate> Predicates { get; }
    public IReadOnlyList<PhysicalQueryOrder> Order { get; }
    public IReadOnlyList<string> RequiredEqualityPrefixPaths { get; }
    public QueryPagingSupport PagingSupport { get; }
    public IReadOnlySet<BoundedQueryResultOperation> ResultOperations { get; }
    public bool SupportsDisjunction { get; }
    public string? LatestPerKeyPath { get; }
    public bool RequiresPrimaryLookup { get; }
    public bool IsScaleBearing { get; }
    public string RouteFingerprint { get; }
    public string Fingerprint { get; }

    internal PhysicalQueryPlan WithFingerprint(string fingerprint) =>
        new(
            StorageUnit,
            QueryIdentity,
            LogicalIndexIdentity,
            LogicalIndexPaths,
            HandlerIdentity,
            Provider,
            Form,
            AccessKind,
            LookupObject,
            PrimaryObject,
            IndexName,
            Scope,
            Discriminator,
            Predicates,
            Order,
            RequiredEqualityPrefixPaths,
            PagingSupport,
            ResultOperations,
            SupportsDisjunction,
            LatestPerKeyPath,
            RequiresPrimaryLookup,
            IsScaleBearing,
            RouteFingerprint,
            fingerprint);

    public bool Equals(PhysicalQueryPlan? other) =>
        other is not null &&
        StorageUnit == other.StorageUnit &&
        QueryIdentity == other.QueryIdentity &&
        LogicalIndexIdentity == other.LogicalIndexIdentity &&
        LogicalIndexPaths.SequenceEqual(other.LogicalIndexPaths) &&
        HandlerIdentity == other.HandlerIdentity &&
        Provider == other.Provider &&
        Form == other.Form &&
        AccessKind == other.AccessKind &&
        LookupObject == other.LookupObject &&
        PrimaryObject == other.PrimaryObject &&
        IndexName == other.IndexName &&
        Scope == other.Scope &&
        Discriminator == other.Discriminator &&
        Predicates.SequenceEqual(other.Predicates) &&
        Order.SequenceEqual(other.Order) &&
        RequiredEqualityPrefixPaths.SequenceEqual(other.RequiredEqualityPrefixPaths) &&
        PagingSupport == other.PagingSupport &&
        ResultOperations.SetEquals(other.ResultOperations) &&
        SupportsDisjunction == other.SupportsDisjunction &&
        LatestPerKeyPath == other.LatestPerKeyPath &&
        RequiresPrimaryLookup == other.RequiresPrimaryLookup &&
        IsScaleBearing == other.IsScaleBearing &&
        RouteFingerprint == other.RouteFingerprint &&
        Fingerprint == other.Fingerprint;

    public override bool Equals(object? obj) => Equals(obj as PhysicalQueryPlan);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);
}

public sealed class PhysicalQueryPlanCompilationResult
{
    public PhysicalQueryPlanCompilationResult(
        IReadOnlyList<PhysicalQueryPlan> plans,
        IReadOnlyList<GroundworkDiagnostic> diagnostics)
    {
        Plans = Array.AsReadOnly(plans.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<PhysicalQueryPlan> Plans { get; }
    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }
    public bool IsValid => Diagnostics.All(diagnostic => !diagnostic.IsError);
}

/// <summary>Canonical diagnostic serialization for stable comparison and plan fingerprints.</summary>
public static class PhysicalQueryPlanSerializer
{
    public static string Serialize(PhysicalQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Encoding.UTF8.GetString(SerializeCore(plan, includeFingerprint: true));
    }

    internal static string CreateFingerprint(PhysicalQueryPlan plan) =>
        Convert.ToHexString(SHA256.HashData(SerializeCore(plan, includeFingerprint: false))).ToLowerInvariant();

    private static byte[] SerializeCore(PhysicalQueryPlan plan, bool includeFingerprint)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("storageUnit", plan.StorageUnit.Value);
            writer.WriteString("query", plan.QueryIdentity);
            writer.WriteString("logicalIndex", plan.LogicalIndexIdentity);
            writer.WritePropertyName("logicalIndexPaths");
            writer.WriteStartArray();
            foreach (var path in plan.LogicalIndexPaths)
                writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteString("handler", plan.HandlerIdentity);
            writer.WriteString("providerName", plan.Provider.Name);
            writer.WriteString("providerVersion", plan.Provider.Version);
            writer.WriteString("form", plan.Form.ToString());
            writer.WriteString("access", plan.AccessKind.ToString());
            writer.WriteString("lookupObject", plan.LookupObject.Identifier);
            writer.WriteString("primaryObject", plan.PrimaryObject.Identifier);
            writer.WriteString("index", plan.IndexName?.Identifier);
            WriteField(writer, "scope", plan.Scope.Field);
            writer.WriteString("scopePolicy", plan.Scope.Policy.ToString());
            writer.WriteBoolean("scopeMandatory", plan.Scope.IsMandatory);
            writer.WriteBoolean("globalSentinel", plan.Scope.UsesGlobalSentinel);
            WriteField(writer, "discriminator", plan.Discriminator);
            writer.WritePropertyName("predicates");
            writer.WriteStartArray();
            foreach (var predicate in plan.Predicates)
            {
                writer.WriteStartObject();
                writer.WriteString("path", predicate.Path);
                WriteField(writer, "field", predicate.Field);
                writer.WritePropertyName("operations");
                writer.WriteStartArray();
                foreach (var operation in predicate.Operations.Order())
                    writer.WriteStringValue(operation.ToString());
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("order");
            writer.WriteStartArray();
            foreach (var order in plan.Order)
            {
                writer.WriteStartObject();
                writer.WriteString("path", order.Path);
                WriteField(writer, "field", order.Field);
                writer.WriteString("direction", order.Direction.ToString());
                writer.WriteBoolean("identityTieBreak", order.IsIdentityTieBreak);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("requiredEqualityPrefixPaths");
            writer.WriteStartArray();
            foreach (var path in plan.RequiredEqualityPrefixPaths)
                writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteString("paging", plan.PagingSupport.ToString());
            writer.WritePropertyName("results");
            writer.WriteStartArray();
            foreach (var result in plan.ResultOperations.Order())
                writer.WriteStringValue(result.ToString());
            writer.WriteEndArray();
            writer.WriteBoolean("disjunction", plan.SupportsDisjunction);
            writer.WriteString("latestPerKey", plan.LatestPerKeyPath);
            writer.WriteBoolean("primaryLookup", plan.RequiresPrimaryLookup);
            writer.WriteBoolean("scaleBearing", plan.IsScaleBearing);
            writer.WriteString("routeFingerprint", plan.RouteFingerprint);
            if (includeFingerprint)
                writer.WriteString("fingerprint", plan.Fingerprint);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteField(Utf8JsonWriter writer, string property, PhysicalQueryField field)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteString("path", field.Path);
        writer.WriteString("identifier", field.Identifier);
        writer.WriteString("source", field.Source.ToString());
        writer.WriteString("target", field.Target.ToString());
        writer.WriteString("object", field.ObjectName.Identifier);
        writer.WriteString("valueKind", field.ValueKind.ToString());
        writer.WriteEndObject();
    }
}
