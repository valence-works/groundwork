using Groundwork.Core.Scoping;

namespace Groundwork.Documents.Store;

public sealed record DocumentEnvelope(
    string DocumentKind,
    string Id,
    string SchemaVersion,
    long Version,
    string ContentJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The storage-boundary scope. Null identifies a deliberately global unit.</summary>
    public StorageScope? Scope { get; init; }
}
