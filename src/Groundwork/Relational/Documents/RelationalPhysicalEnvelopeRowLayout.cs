using System.Data.Common;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;

namespace Groundwork.Relational.Documents;

internal sealed record RelationalPhysicalEnvelopeRow(
    DocumentEnvelope Envelope,
    string ComparisonKey,
    string LookupKey);

internal static class RelationalPhysicalEnvelopeRowLayout
{
    private const int DocumentKind = 0;
    private const int StorageScope = 1;
    private const int OriginalId = 2;
    private const int ComparisonKey = 3;
    private const int LookupKey = 4;
    private const int SchemaVersion = 5;
    private const int DocumentVersion = 6;
    private const int CanonicalJson = 7;
    private const int CreatedUtc = 8;
    private const int UpdatedUtc = 9;

    public static IReadOnlyList<string> PersistedColumns(ExecutableStorageRoute route) =>
    [
        route.Envelope.DocumentKind.Identifier,
        route.Envelope.StorageScope.Identifier,
        route.Envelope.Id.Identifier,
        route.Envelope.Identity.ComparisonKey.Identifier,
        route.Envelope.Identity.LookupKey.Identifier,
        route.Envelope.SchemaVersion.Identifier,
        route.Envelope.Version.Identifier,
        route.Envelope.CanonicalJson.Identifier
    ];

    public static IReadOnlyList<string> SelectionColumns(ExecutableStorageRoute route) =>
        PersistedColumns(route)
            .Concat(
            [
                RelationalPhysicalStorageColumns.CreatedUtc,
                RelationalPhysicalStorageColumns.UpdatedUtc
            ])
            .ToArray();

    public static RelationalPhysicalEnvelopeRow Read(
        DbDataReader reader,
        RelationalPhysicalDocumentDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(dialect);
        var envelope = new DocumentEnvelope(
            reader.GetString(DocumentKind),
            reader.GetString(OriginalId),
            reader.GetString(SchemaVersion),
            reader.GetInt64(DocumentVersion),
            reader.GetString(CanonicalJson),
            DateTimeOffset.Parse(reader.GetString(CreatedUtc)),
            DateTimeOffset.Parse(reader.GetString(UpdatedUtc)))
        {
            Scope = DocumentStoreScopeResolver.ReadScope(reader.GetString(StorageScope))
        };
        return new RelationalPhysicalEnvelopeRow(
            envelope,
            dialect.ReadDocumentIdentityComparison(reader, ComparisonKey),
            dialect.ReadDocumentIdentityLookup(reader, LookupKey));
    }
}
