using System.Text.Json;

namespace Groundwork.Documents.Store;

public static class JsonDocumentStoreExtensions
{
    public static SaveDocumentRequest ToSaveDocumentRequest<TDocument>(
        string documentKind,
        string id,
        string schemaVersion,
        TDocument document,
        JsonSerializerOptions? jsonOptions = null,
        long? expectedVersion = null)
    {
        var contentJson = JsonSerializer.Serialize(document, jsonOptions);
        return new SaveDocumentRequest(documentKind, id, schemaVersion, contentJson, expectedVersion);
    }

    public static async Task<DocumentStoreWriteResult> SaveJsonAsync<TDocument>(
        this IDocumentStore store,
        string documentKind,
        string id,
        string schemaVersion,
        TDocument document,
        JsonSerializerOptions? jsonOptions = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await store.SaveAsync(
            ToSaveDocumentRequest(documentKind, id, schemaVersion, document, jsonOptions, expectedVersion),
            cancellationToken);
    }

    public static async Task<TDocument?> LoadJsonAsync<TDocument>(
        this IDocumentStore store,
        string documentKind,
        string id,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var envelope = await store.LoadAsync(documentKind, id, cancellationToken);
        return envelope is null ? default : envelope.DeserializeJson<TDocument>(jsonOptions);
    }

    public static async Task<IReadOnlyList<TDocument>> QueryJsonAsync<TDocument>(
        this IDocumentStore store,
        DocumentStoreQuery query,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var envelopes = await store.QueryAsync(query, cancellationToken);
        return envelopes.Select(envelope => envelope.DeserializeJson<TDocument>(jsonOptions)).ToList();
    }

    public static async Task<DocumentJsonQueryResult<TDocument>> QueryJsonAsync<TDocument>(
        this IDocumentStore store,
        PortableDocumentQuery query,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var result = await store.QueryAsync(query, cancellationToken);
        var documents = result.Documents.Select(envelope => envelope.DeserializeJson<TDocument>(jsonOptions)).ToList();
        return new DocumentJsonQueryResult<TDocument>(documents, result.TotalCount);
    }

    public static async Task<TDocument?> FirstOrDefaultJsonAsync<TDocument>(
        this IDocumentStore store,
        PortableDocumentQuery query,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var envelope = await store.FirstOrDefaultAsync(query, cancellationToken);
        return envelope is null ? default : envelope.DeserializeJson<TDocument>(jsonOptions);
    }

    public static TDocument DeserializeJson<TDocument>(
        this DocumentEnvelope envelope,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return JsonSerializer.Deserialize<TDocument>(envelope.ContentJson, jsonOptions)
               ?? throw new InvalidOperationException($"Document '{envelope.Id}' of kind '{envelope.DocumentKind}' could not be deserialized as {typeof(TDocument).Name}.");
    }
}

public sealed record DocumentJsonQueryResult<TDocument>(IReadOnlyList<TDocument> Documents, long TotalCount);
