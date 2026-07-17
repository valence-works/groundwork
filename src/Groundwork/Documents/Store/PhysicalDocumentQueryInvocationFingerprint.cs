using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;

namespace Groundwork.Documents.Store;

/// <summary>
/// Creates pseudonymous correlation metadata for the complete bounded-query invocation, including
/// caller shape, compiled physical route, and inherited storage-scope selection. The digest contains
/// no raw values but low-entropy inputs can be guessed offline, so it is not a secrecy boundary.
/// </summary>
public static class PhysicalDocumentQueryInvocationFingerprint
{
    public static string Compute(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scope);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "groundwork-physical-document-query-invocation-v1");
        Append(hash, plan.RouteFingerprint);
        Append(hash, scope.AcrossScopes);
        Append(hash, scope.StorageKey);
        Append(hash, scope.Scope?.Value);
        Append(hash, query.DocumentKind);
        Append(hash, query.QueryIdentity);
        Append(hash, query.Clauses.Count);
        foreach (var clause in query.Clauses)
        {
            Append(hash, clause.Comparisons.Count);
            foreach (var comparison in clause.Comparisons)
            {
                Append(hash, comparison.Path);
                Append(hash, (int)comparison.Operator);
                Append(hash, comparison.Values.Count);
                foreach (var value in comparison.Values)
                    Append(hash, value);
            }
        }

        Append(hash, query.Order.Count);
        foreach (var order in query.Order)
        {
            Append(hash, order.Path);
            Append(hash, (int)order.Direction);
        }

        Append(hash, query.Skip);
        Append(hash, query.Take);
        Append(hash, query.Continuation);
        Append(hash, query.LatestPerKeyPath);
        Append(hash, (int)query.ResultOperation);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string ComputeContinuationBinding(
        DocumentQuery query,
        PhysicalQueryPlan plan,
        DocumentScopeSelection scope)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scope);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "groundwork-physical-document-query-continuation-v1");
        Append(hash, plan.Fingerprint);
        Append(hash, scope.AcrossScopes);
        Append(hash, scope.StorageKey);
        Append(hash, scope.Scope?.Value);
        Append(hash, query.DocumentKind);
        Append(hash, query.QueryIdentity);
        Append(hash, query.Clauses.Count);
        foreach (var clause in query.Clauses)
        {
            Append(hash, clause.Comparisons.Count);
            foreach (var comparison in clause.Comparisons)
            {
                Append(hash, comparison.Path);
                Append(hash, (int)comparison.Operator);
                Append(hash, comparison.Values.Count);
                foreach (var value in comparison.Values)
                    Append(hash, value);
            }
        }

        Append(hash, query.Order.Count);
        foreach (var order in query.Order)
        {
            Append(hash, order.Path);
            Append(hash, (int)order.Direction);
        }

        Append(hash, query.LatestPerKeyPath);
        Append(hash, (int)query.ResultOperation);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, bool value) => Append(hash, value ? 1 : 0);

    private static void Append(IncrementalHash hash, int value) => Append(hash, (int?)value);

    private static void Append(IncrementalHash hash, int? value)
    {
        Span<byte> buffer = stackalloc byte[5];
        buffer[0] = value.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[1..], value.GetValueOrDefault());
        hash.AppendData(buffer);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Append(hash, (int?)null);
            return;
        }

        Append(hash, value.Length);
        Span<byte> codeUnit = stackalloc byte[2];
        foreach (var character in value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(codeUnit, character);
            hash.AppendData(codeUnit);
        }
    }
}
