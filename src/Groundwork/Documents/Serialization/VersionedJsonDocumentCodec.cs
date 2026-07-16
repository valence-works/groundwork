using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Documents.Store;

namespace Groundwork.Documents.Serialization;

public sealed record VersionedJsonContent(string SchemaVersion, string ContentJson);

/// <summary>
/// Serializes canonical JSON at a document kind's current version and validates/upcasts persisted JSON before typed deserialization.
/// </summary>
public sealed class VersionedJsonDocumentCodec
{
    private readonly DocumentJsonUpcasterRegistry _registry;
    private readonly DocumentSchemaVersionFormat _versionFormat;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly JsonDocumentOptions _jsonDocumentOptions;

    public VersionedJsonDocumentCodec(
        IEnumerable<DocumentSchemaVersionPolicy> policies,
        IEnumerable<IDocumentJsonUpcaster> upcasters,
        DocumentSchemaVersionFormat versionFormat,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(upcasters);
        ArgumentNullException.ThrowIfNull(versionFormat);
        _registry = new DocumentJsonUpcasterRegistry(policies, upcasters);
        _versionFormat = versionFormat;
        _jsonOptions = jsonOptions;
        _jsonDocumentOptions = jsonOptions is null
            ? default
            : new JsonDocumentOptions
            {
                AllowTrailingCommas = jsonOptions.AllowTrailingCommas,
                CommentHandling = jsonOptions.ReadCommentHandling,
                MaxDepth = jsonOptions.MaxDepth
            };

        foreach (var policy in _registry.Policies)
            _versionFormat.ValidateRoundTrips(policy);
    }

    public VersionedJsonContent Serialize<TDocument>(string documentKind, TDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var policy = _registry.GetPolicy(documentKind);
        var schemaVersion = _versionFormat.Stamp(policy, policy.CurrentVersion);
        var contentJson = JsonSerializer.Serialize(document, _jsonOptions);
        return new VersionedJsonContent(schemaVersion, contentJson);
    }

    public SaveDocumentRequest CreateSaveRequest<TDocument>(
        string documentKind,
        string id,
        TDocument document,
        long? expectedVersion = null)
    {
        var serialized = Serialize(documentKind, document);
        return new SaveDocumentRequest(documentKind, id, serialized.SchemaVersion, serialized.ContentJson, expectedVersion);
    }

    public bool IsCurrentVersion(DocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var policy = _registry.GetPolicy(envelope.DocumentKind);
        var version = _versionFormat.Parse(
            envelope.DocumentKind,
            envelope.Id,
            envelope.SchemaVersion,
            policy.MinimumReadableVersion,
            policy.CurrentVersion);
        return version == policy.CurrentVersion;
    }

    public TDocument Deserialize<TDocument>(DocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var policy = _registry.GetPolicy(envelope.DocumentKind);
        var version = ReadSupportedVersion(envelope, policy);

        if (version == policy.CurrentVersion)
            return DeserializeContent<TDocument>(envelope, policy, version, envelope.ContentJson);

        JsonObject content;
        try
        {
            content = JsonNode.Parse(envelope.ContentJson, documentOptions: _jsonDocumentOptions) as JsonObject
                      ?? throw InvalidContent(envelope, policy, version, "does not contain a JSON object and cannot be upcasted");
        }
        catch (DocumentSchemaVersionException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidContent(envelope, policy, version, "does not contain valid JSON and cannot be upcasted", exception);
        }

        var upcasted = _registry.UpcastToCurrent(envelope.DocumentKind, version, content);
        return DeserializeContent<TDocument>(envelope, policy, version, upcasted);
    }

    private TDocument DeserializeContent<TDocument>(
        DocumentEnvelope envelope,
        DocumentSchemaVersionPolicy policy,
        int version,
        string contentJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TDocument>(contentJson, _jsonOptions)
                   ?? throw InvalidContent(envelope, policy, version, "deserialized to null content");
        }
        catch (DocumentSchemaVersionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw InvalidContent(envelope, policy, version, "could not be deserialized", exception);
        }
    }

    private TDocument DeserializeContent<TDocument>(
        DocumentEnvelope envelope,
        DocumentSchemaVersionPolicy policy,
        int version,
        JsonObject content)
    {
        try
        {
            return content.Deserialize<TDocument>(_jsonOptions)
                   ?? throw InvalidContent(envelope, policy, version, "upcasted to null content");
        }
        catch (DocumentSchemaVersionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw InvalidContent(envelope, policy, version, "could not be deserialized after upcasting", exception);
        }
    }

    private int ReadSupportedVersion(DocumentEnvelope envelope, DocumentSchemaVersionPolicy policy)
    {
        var version = _versionFormat.Parse(
            envelope.DocumentKind,
            envelope.Id,
            envelope.SchemaVersion,
            policy.MinimumReadableVersion,
            policy.CurrentVersion);
        if (version < policy.MinimumReadableVersion)
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.TooOld,
                $"Document '{envelope.Id}' of kind '{envelope.DocumentKind}' carries schema version {version}, below minimum readable version {policy.MinimumReadableVersion}.",
                envelope.DocumentKind,
                envelope.Id,
                envelope.SchemaVersion,
                version,
                policy.MinimumReadableVersion,
                policy.CurrentVersion);
        }

        if (version > policy.CurrentVersion)
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.Future,
                $"Document '{envelope.Id}' of kind '{envelope.DocumentKind}' carries future schema version {version}; this build supports up to {policy.CurrentVersion}.",
                envelope.DocumentKind,
                envelope.Id,
                envelope.SchemaVersion,
                version,
                policy.MinimumReadableVersion,
                policy.CurrentVersion);
        }

        return version;
    }

    private static DocumentSchemaVersionException InvalidContent(
        DocumentEnvelope envelope,
        DocumentSchemaVersionPolicy policy,
        int version,
        string detail,
        Exception? innerException = null) =>
        new(
            DocumentSchemaVersionFailure.InvalidContent,
            $"Document '{envelope.Id}' of kind '{envelope.DocumentKind}' at schema version {version} {detail}.",
            envelope.DocumentKind,
            envelope.Id,
            envelope.SchemaVersion,
            version,
            policy.MinimumReadableVersion,
            policy.CurrentVersion,
            innerException: innerException);
}
