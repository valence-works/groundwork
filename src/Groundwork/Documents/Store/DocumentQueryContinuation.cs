using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;

namespace Groundwork.Documents.Store;

/// <summary>
/// Raised when an opaque bounded-query continuation is malformed or does not belong to the current
/// compiled query route, scope, predicate values, or ordering.
/// </summary>
public sealed class InvalidDocumentQueryContinuationException : Exception
{
    public InvalidDocumentQueryContinuationException(string message)
        : base(message)
    {
    }

    public InvalidDocumentQueryContinuationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal enum DocumentQueryContinuationScalarKind
{
    Null,
    String,
    Int64,
    Decimal,
    Double,
    Boolean,
    DateTimeOffset,
    Binary
}

internal sealed record DocumentQueryContinuationValue(
    IndexValueKind Kind,
    DocumentQueryContinuationScalarKind ScalarKind,
    string? Value);

internal static class DocumentQueryOrderResolver
{
    public static IReadOnlyList<PhysicalQueryOrder> Resolve(DocumentQuery query, PhysicalQueryPlan plan) =>
        query.Order.Count == 0
            ? plan.Order
            : query.Order.Select(order => new PhysicalQueryOrder(
                    order.Path,
                    plan.Order.Single(planned => planned.Path == order.Path).Field,
                    order.Direction,
                    IsIdentityTieBreak: false))
                .Concat(plan.Order.Skip(query.Order.Count))
                .ToArray();
}

/// <summary>
/// The token checksum detects corruption and accidental rewriting. It is deliberately not an
/// authenticity or confidentiality boundary; applications must treat continuations as opaque.
/// </summary>
internal static class DocumentQueryContinuationCodec
{
    private const string Prefix = "gwq1";
    private const int Version = 1;
    private const int MaximumTokenLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8
    };

    public static string Encode(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope,
        IReadOnlyList<DocumentQueryContinuationValue> values)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(plan, query, values);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(
                Version,
                PhysicalDocumentQueryInvocationFingerprint.ComputeContinuationBinding(query, plan, scope),
                values.Select(value => new ValuePayload(
                    (int)value.Kind,
                    (int)value.ScalarKind,
                    value.Value)).ToArray()),
            JsonOptions);
        var checksum = SHA256.HashData(payload);
        var token = $"{Prefix}.{Base64UrlEncode(payload)}.{Base64UrlEncode(checksum)}";
        return token.Length <= MaximumTokenLength ? token : throw Invalid();
    }

    public static void ValidateScope(PhysicalQueryPlan plan, DocumentScopeSelection scope)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scope);
        if (plan.PagingSupport == QueryPagingSupport.Cursor &&
            plan.Scope.Policy == StorageScopePolicy.Scoped &&
            scope.AcrossScopes)
        {
            throw new InvalidOperationException(
                "A scale-bearing cursor query over a scoped storage unit must execute within one inherited storage scope.");
        }
    }

    public static IReadOnlyList<DocumentQueryContinuationValue> Decode(
        string token,
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
                throw Invalid();
            var parts = token.Split('.');
            if (parts is not [Prefix, var encodedPayload, var encodedChecksum])
                throw Invalid();
            var payloadBytes = Base64UrlDecode(encodedPayload);
            var suppliedChecksum = Base64UrlDecode(encodedChecksum);
            var expectedChecksum = SHA256.HashData(payloadBytes);
            if (suppliedChecksum.Length != expectedChecksum.Length ||
                !CryptographicOperations.FixedTimeEquals(suppliedChecksum, expectedChecksum))
            {
                throw Invalid();
            }

            var payload = JsonSerializer.Deserialize<Payload>(payloadBytes, JsonOptions) ?? throw Invalid();
            if (payload.Version != Version ||
                payload.Binding != PhysicalDocumentQueryInvocationFingerprint.ComputeContinuationBinding(query, plan, scope) ||
                payload.Values is null)
            {
                throw Invalid();
            }

            var values = payload.Values
                .Select(value => new DocumentQueryContinuationValue(
                    (IndexValueKind)value.Kind,
                    (DocumentQueryContinuationScalarKind)value.ScalarKind,
                    value.Value))
                .ToArray();
            ValidateValues(plan, query, values);
            return values;
        }
        catch (InvalidDocumentQueryContinuationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                                           FormatException or
                                           JsonException or
                                           ArgumentException or
                                           OverflowException)
        {
            throw Invalid(exception);
        }
    }

    private static void ValidateValues(
        PhysicalQueryPlan plan,
        DocumentQuery query,
        IReadOnlyList<DocumentQueryContinuationValue> values)
    {
        var order = DocumentQueryOrderResolver.Resolve(query, plan);
        if (values.Count != order.Count ||
            values.Where((value, index) =>
                !Enum.IsDefined(value.Kind) ||
                !Enum.IsDefined(value.ScalarKind) ||
                value.Kind != order[index].Field.ValueKind ||
                (value.ScalarKind == DocumentQueryContinuationScalarKind.Null) != (value.Value is null) ||
                !ScalarKindMatches(value) ||
                !ScalarValueParses(value)).Any())
        {
            throw Invalid();
        }
    }

    private static bool ScalarKindMatches(DocumentQueryContinuationValue value) =>
        value.ScalarKind == DocumentQueryContinuationScalarKind.Null ||
        value.Kind switch
        {
            IndexValueKind.String or IndexValueKind.Keyword =>
                value.ScalarKind is DocumentQueryContinuationScalarKind.String or
                    DocumentQueryContinuationScalarKind.Binary,
            IndexValueKind.Number =>
                value.ScalarKind is DocumentQueryContinuationScalarKind.Int64 or
                    DocumentQueryContinuationScalarKind.Decimal or
                    DocumentQueryContinuationScalarKind.Double,
            IndexValueKind.Boolean => value.ScalarKind == DocumentQueryContinuationScalarKind.Boolean,
            IndexValueKind.DateTime =>
                value.ScalarKind is DocumentQueryContinuationScalarKind.DateTimeOffset or
                    DocumentQueryContinuationScalarKind.Int64,
            _ => false
        };

    private static bool ScalarValueParses(DocumentQueryContinuationValue value)
    {
        if (value.ScalarKind == DocumentQueryContinuationScalarKind.Null)
            return true;
        try
        {
            object parsed = value.ScalarKind switch
            {
                DocumentQueryContinuationScalarKind.String => value.Value!,
                DocumentQueryContinuationScalarKind.Int64 =>
                    long.Parse(value.Value!, NumberStyles.Integer, CultureInfo.InvariantCulture),
                DocumentQueryContinuationScalarKind.Decimal =>
                    decimal.Parse(value.Value!, NumberStyles.Number, CultureInfo.InvariantCulture),
                DocumentQueryContinuationScalarKind.Double =>
                    double.Parse(value.Value!, NumberStyles.Float, CultureInfo.InvariantCulture),
                DocumentQueryContinuationScalarKind.Boolean => bool.Parse(value.Value!),
                DocumentQueryContinuationScalarKind.DateTimeOffset => DateTimeOffset.Parse(
                    value.Value!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                DocumentQueryContinuationScalarKind.Binary => Convert.FromBase64String(value.Value!),
                _ => throw new FormatException("Unsupported cursor scalar.")
            };
            _ = parsed;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new FormatException("Invalid Base64Url length.")
        };
        return Convert.FromBase64String(padded);
    }

    private static InvalidDocumentQueryContinuationException Invalid(Exception? inner = null) =>
        inner is null
            ? new("The document-query continuation is malformed or does not belong to this query.")
            : new("The document-query continuation is malformed or does not belong to this query.", inner);

    private sealed record Payload(int Version, string Binding, IReadOnlyList<ValuePayload> Values);
    private sealed record ValuePayload(int Kind, int ScalarKind, string? Value);
}
