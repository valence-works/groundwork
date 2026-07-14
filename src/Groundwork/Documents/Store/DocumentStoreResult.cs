namespace Groundwork.Documents.Store;

/// <summary>
/// A request to write a document. <paramref name="ExpectedVersion"/> selects the write's concurrency semantics:
/// <list type="bullet">
/// <item><description><see langword="null"/> — unconditional upsert: creates the document at version 1 or
/// overwrites the current content, whatever its version.</description></item>
/// <item><description><c>0</c> — create-only: succeeds (at version 1) only when no document with this id exists;
/// returns <see cref="DocumentStoreWriteStatus.ConcurrencyConflict"/> when one does, including when a concurrent
/// writer creates it first. This is the first-writer-wins primitive for distributed consumers that must not
/// silently overwrite each other's initial document.</description></item>
/// <item><description>any other value — compare-and-swap update: succeeds only when the stored document's version
/// equals the expected version; returns <see cref="DocumentStoreWriteStatus.ConcurrencyConflict"/> on a version
/// mismatch and <see cref="DocumentStoreWriteStatus.NotFound"/> when no document exists (a non-zero expectation
/// can never match an absent document). Nothing is written in either failure case.</description></item>
/// </list>
/// </summary>
public sealed record SaveDocumentRequest(
    string DocumentKind,
    string Id,
    string SchemaVersion,
    string ContentJson,
    long? ExpectedVersion = null);

/// <summary>
/// A request to delete a document. <paramref name="ExpectedVersion"/> selects the delete's concurrency semantics:
/// <see langword="null"/> deletes unconditionally; any other value deletes only when the stored document's version
/// equals the expected version and returns <see cref="DocumentStoreWriteStatus.ConcurrencyConflict"/> on a mismatch.
/// Deleting an absent document returns <see cref="DocumentStoreWriteStatus.NotFound"/> regardless of the expected
/// version — there is no create-only analogue for deletes; <c>0</c> receives no special treatment here.
/// </summary>
public sealed record DeleteDocumentRequest(
    string DocumentKind,
    string Id,
    long? ExpectedVersion = null);

public sealed record DocumentStoreWriteResult(
    DocumentStoreWriteStatus Status,
    DocumentEnvelope? Document = null,
    string? AuthoritativeId = null)
{
    public static DocumentStoreWriteResult Saved(DocumentEnvelope document) => new(DocumentStoreWriteStatus.Saved, document);
    public static DocumentStoreWriteResult Deleted(string authoritativeId) =>
        new(DocumentStoreWriteStatus.Deleted, AuthoritativeId: authoritativeId);
    public static DocumentStoreWriteResult IdentityConflict(string authoritativeId) =>
        new(DocumentStoreWriteStatus.IdentityConflict, AuthoritativeId: authoritativeId);
    public static DocumentStoreWriteResult NotFound { get; } = new(DocumentStoreWriteStatus.NotFound);
    public static DocumentStoreWriteResult ConcurrencyConflict { get; } = new(DocumentStoreWriteStatus.ConcurrencyConflict);
}

public enum DocumentStoreWriteStatus
{
    Saved,
    Deleted,
    IdentityConflict,
    NotFound,
    ConcurrencyConflict
}

public sealed class UndeclaredDocumentIndexException(string documentKind, string indexName)
    : InvalidOperationException($"Index '{indexName}' is not declared or is not queryable for document kind '{documentKind}'.")
{
    public string DocumentKind { get; } = documentKind;
    public string IndexName { get; } = indexName;
}
