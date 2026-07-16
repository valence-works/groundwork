using System.Text.Json.Nodes;

namespace Groundwork.Documents.Serialization;

/// <summary>
/// Eagerly validates and applies contiguous JSON upcaster chains for declared document-kind policies.
/// </summary>
internal sealed class DocumentJsonUpcasterRegistry
{
    private readonly Dictionary<string, DocumentSchemaVersionPolicy> _policies = new(StringComparer.Ordinal);
    private readonly Dictionary<(string DocumentKind, int FromVersion), IDocumentJsonUpcaster> _steps = new();

    public DocumentJsonUpcasterRegistry(
        IEnumerable<DocumentSchemaVersionPolicy> policies,
        IEnumerable<IDocumentJsonUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(upcasters);

        foreach (var policy in policies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            if (!_policies.TryAdd(policy.DocumentKind, policy))
            {
                throw new DocumentSchemaVersionException(
                    DocumentSchemaVersionFailure.InvalidPolicy,
                    $"Multiple schema-version policies are registered for document kind '{policy.DocumentKind}'.",
                    policy.DocumentKind,
                    minimumReadableVersion: policy.MinimumReadableVersion,
                    currentVersion: policy.CurrentVersion);
            }
        }

        foreach (var upcaster in upcasters)
        {
            ArgumentNullException.ThrowIfNull(upcaster);
            var documentKind = upcaster.DocumentKind;
            ArgumentException.ThrowIfNullOrWhiteSpace(documentKind, $"{nameof(IDocumentJsonUpcaster)}.{nameof(IDocumentJsonUpcaster.DocumentKind)}");

            if (!_policies.TryGetValue(documentKind, out var policy))
            {
                throw new DocumentSchemaVersionException(
                    DocumentSchemaVersionFailure.InvalidUpcasterChain,
                    $"Upcaster '{upcaster.GetType().FullName}' targets document kind '{documentKind}', but no schema-version policy declares that kind.",
                    documentKind,
                    parsedVersion: upcaster.FromVersion);
            }

            if (upcaster.FromVersion < policy.MinimumReadableVersion)
            {
                throw InvalidChain(
                    policy,
                    documentKind,
                    upcaster.FromVersion,
                    $"Upcaster '{upcaster.GetType().FullName}' for document kind '{documentKind}' starts at version {upcaster.FromVersion}, below its minimum readable version {policy.MinimumReadableVersion}.");
            }

            if (upcaster.FromVersion >= policy.CurrentVersion)
            {
                throw InvalidChain(
                    policy,
                    documentKind,
                    upcaster.FromVersion,
                    $"Upcaster '{upcaster.GetType().FullName}' for document kind '{documentKind}' starts at version {upcaster.FromVersion}, but its current version is {policy.CurrentVersion}.");
            }

            var key = (documentKind, upcaster.FromVersion);
            if (!_steps.TryAdd(key, upcaster))
            {
                throw InvalidChain(
                    policy,
                    documentKind,
                    upcaster.FromVersion,
                    $"Multiple upcasters are registered for document kind '{documentKind}' from version {upcaster.FromVersion} to {upcaster.FromVersion + 1}.");
            }
        }

        foreach (var policy in _policies.Values)
        {
            for (var version = policy.MinimumReadableVersion; version < policy.CurrentVersion; version++)
            {
                if (!_steps.ContainsKey((policy.DocumentKind, version)))
                {
                    throw InvalidChain(
                        policy,
                        policy.DocumentKind,
                        version,
                        $"Document kind '{policy.DocumentKind}' has no upcaster from version {version} to {version + 1}; every supported version must reach current version {policy.CurrentVersion}.");
                }
            }
        }
    }

    internal IEnumerable<DocumentSchemaVersionPolicy> Policies => _policies.Values;

    public DocumentSchemaVersionPolicy GetPolicy(string documentKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKind);
        if (_policies.TryGetValue(documentKind, out var policy))
            return policy;

        throw new DocumentSchemaVersionException(
            DocumentSchemaVersionFailure.UnknownDocumentKind,
            $"No schema-version policy is registered for document kind '{documentKind}'.",
            documentKind);
    }

    public JsonObject UpcastToCurrent(string documentKind, int fromVersion, JsonObject content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var policy = GetPolicy(documentKind);

        if (fromVersion < policy.MinimumReadableVersion)
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.TooOld,
                $"Cannot upcast document kind '{documentKind}' from version {fromVersion}; its minimum readable version is {policy.MinimumReadableVersion}.",
                documentKind,
                parsedVersion: fromVersion,
                minimumReadableVersion: policy.MinimumReadableVersion,
                currentVersion: policy.CurrentVersion);
        }

        if (fromVersion > policy.CurrentVersion)
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.Future,
                $"Cannot upcast document kind '{documentKind}' from future version {fromVersion}; current version is {policy.CurrentVersion}.",
                documentKind,
                parsedVersion: fromVersion,
                minimumReadableVersion: policy.MinimumReadableVersion,
                currentVersion: policy.CurrentVersion);
        }

        var current = content;
        for (var version = fromVersion; version < policy.CurrentVersion; version++)
        {
            var step = _steps[(documentKind, version)];
            try
            {
                current = step.Upcast(current)
                          ?? throw UpcastFailed(
                              policy,
                              version,
                              $"Upcaster '{step.GetType().FullName}' returned null for document kind '{documentKind}' at version {version}.");
            }
            catch (DocumentSchemaVersionException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                throw UpcastFailed(
                    policy,
                    version,
                    $"Upcaster '{step.GetType().FullName}' failed for document kind '{documentKind}' at version {version}.",
                    exception);
            }
        }

        return current;
    }

    private static DocumentSchemaVersionException InvalidChain(
        DocumentSchemaVersionPolicy policy,
        string documentKind,
        int version,
        string message) =>
        new(
            DocumentSchemaVersionFailure.InvalidUpcasterChain,
            message,
            documentKind,
            parsedVersion: version,
            minimumReadableVersion: policy.MinimumReadableVersion,
            currentVersion: policy.CurrentVersion);

    private static DocumentSchemaVersionException UpcastFailed(
        DocumentSchemaVersionPolicy policy,
        int version,
        string message,
        Exception? innerException = null) =>
        new(
            DocumentSchemaVersionFailure.UpcastFailed,
            message,
            policy.DocumentKind,
            parsedVersion: version,
            minimumReadableVersion: policy.MinimumReadableVersion,
            currentVersion: policy.CurrentVersion,
            innerException: innerException);
}
