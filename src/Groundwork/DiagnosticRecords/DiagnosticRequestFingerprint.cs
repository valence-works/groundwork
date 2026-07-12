using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Groundwork.DiagnosticRecords;

public readonly record struct DiagnosticRequestFingerprint
{
    private const int Sha256HexLength = 64;

    public DiagnosticRequestFingerprint(string value)
    {
        if (value is null || value.Length != Sha256HexLength || value.Any(x => !Uri.IsHexDigit(x)))
            throw new ArgumentException("A request fingerprint must be a 64-character SHA-256 hexadecimal value.", nameof(value));
        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public static DiagnosticRequestFingerprint ForAppend(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        IReadOnlyList<DiagnosticRecordInput> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "diagnostic-append-v1");
        Append(hash, scope.TenantId);
        Append(hash, scope.ScopeId);
        Append(hash, stream.Value);
        Append(hash, records.Count);
        foreach (var record in records)
        {
            Append(hash, record.RecordId);
            Append(hash, record.OccurredAt.UtcTicks);
            Append(hash, record.Payload);
            var fields = record.Fields ?? new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>();
            Append(hash, fields.Count);
            foreach (var field in fields.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                Append(hash, field.Key);
                Append(hash, field.Value.Count);
                foreach (var value in field.Value)
                {
                    Append(hash, (int)value.Type);
                    Append(hash, value.IsInitialized ? 1 : 0);
                    Append(hash, value.CanonicalValue ?? "");
                }
            }
        }

        return new(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static DiagnosticRequestFingerprint ForTrim(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        int keepNewest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "diagnostic-trim-v1");
        Append(hash, scope.TenantId);
        Append(hash, scope.ScopeId);
        Append(hash, stream.Value);
        Append(hash, keepNewest);
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static DiagnosticRequestFingerprint ForQuery(
        DiagnosticRecordQuery query,
        DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(definition);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "diagnostic-query-v1");
        Append(hash, query.Scope.TenantId);
        Append(hash, query.Scope.ScopeId);
        Append(hash, query.Stream.Value);
        Append(hash, query.Limit);
        var order = query.Order ?? DiagnosticRecordOrder.CursorAscending;
        Append(hash, order.Field ?? "");
        Append(hash, (int)order.Direction);
        Append(hash, query.IncludeExactCount ? 1 : 0);
        Append(hash, query.LatestPerKeyField ?? "");
        Append(hash, query.Predicate);
        Append(hash, definition.SchemaVersion);
        Append(hash, definition.LogicalStorageName);
        Append(hash, definition.Fields.Count);
        foreach (var field in definition.Fields.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            Append(hash, field.Name);
            Append(hash, (int)field.Type);
            Append(hash, (int)field.Cardinality);
            Append(hash, field.IsRequired ? 1 : 0);
            Append(hash, field.IsOrderable ? 1 : 0);
            Append(hash, field.SupportsLatestPerKey ? 1 : 0);
            Append(hash, (int)field.CasePolicy);
            Append(hash, field.MaxValues);
            Append(hash, field.MaxStringBytes ?? -1);
            Append(hash, (int)field.MissingValueBehavior);
            Append(hash, field.SupportedPredicates.Count);
            foreach (var operation in field.SupportedPredicates.Order())
                Append(hash, (int)operation);
        }
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, DiagnosticRecordPredicate? predicate)
    {
        switch (predicate)
        {
            case null:
                Append(hash, 0);
                break;
            case DiagnosticRecordPredicate.All all:
                Append(hash, 1);
                Append(hash, all.Predicates.Count);
                foreach (var child in all.Predicates)
                    Append(hash, child);
                break;
            case DiagnosticRecordPredicate.Any any:
                Append(hash, 2);
                Append(hash, any.Predicates.Count);
                foreach (var child in any.Predicates)
                    Append(hash, child);
                break;
            case DiagnosticRecordPredicate.Comparison comparison:
                Append(hash, 3);
                Append(hash, comparison.Field);
                Append(hash, (int)comparison.Operator);
                Append(hash, comparison.Values.Count);
                foreach (var value in comparison.Values)
                {
                    Append(hash, (int)value.Type);
                    Append(hash, value.IsInitialized ? 1 : 0);
                    Append(hash, value.CanonicalValue ?? "");
                }
                break;
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
