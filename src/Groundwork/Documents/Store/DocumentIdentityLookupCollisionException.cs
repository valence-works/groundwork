namespace Groundwork.Documents.Store;

/// <summary>
/// Raised when two distinct document identity comparison keys produce the same fixed lookup key.
/// This is a storage-integrity failure, not an optimistic-concurrency or identity-spelling conflict.
/// </summary>
public sealed class DocumentIdentityLookupCollisionException(
    string documentKind,
    string requestedId,
    string retainedId,
    string lookupKey)
    : InvalidOperationException(
        $"Document identity lookup collision for kind '{documentKind}': requested ID '{requestedId}' and retained ID '{retainedId}' share lookup key '{lookupKey}'.")
{
    public string DocumentKind { get; } = documentKind;

    public string RequestedId { get; } = requestedId;

    public string RetainedId { get; } = retainedId;

    public string LookupKey { get; } = lookupKey;
}
