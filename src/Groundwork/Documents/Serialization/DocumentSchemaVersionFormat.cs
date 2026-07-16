namespace Groundwork.Documents.Serialization;

/// <summary>
/// Maps caller-owned persisted schema-version stamps to Groundwork's positive, contiguous migration steps.
/// </summary>
public sealed class DocumentSchemaVersionFormat
{
    private readonly Func<string, string, int?> _parser;
    private readonly Func<string, int, string> _formatter;

    public DocumentSchemaVersionFormat(
        Func<string, string, int?> parser,
        Func<string, int, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(formatter);
        _parser = parser;
        _formatter = formatter;
    }

    internal int Parse(
        string documentKind,
        string documentId,
        string schemaVersion,
        int minimumReadableVersion,
        int currentVersion)
    {
        int? parsedVersion;
        try
        {
            parsedVersion = _parser(documentKind, schemaVersion);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.MalformedStamp,
                $"Document '{documentId}' of kind '{documentKind}' carries schema-version stamp '{schemaVersion}', which the configured format could not parse.",
                documentKind,
                documentId,
                schemaVersion,
                minimumReadableVersion: minimumReadableVersion,
                currentVersion: currentVersion,
                innerException: exception);
        }

        if (parsedVersion is null or < 1)
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.MalformedStamp,
                $"Document '{documentId}' of kind '{documentKind}' carries unrecognized schema-version stamp '{schemaVersion}'.",
                documentKind,
                documentId,
                schemaVersion,
                parsedVersion: parsedVersion,
                minimumReadableVersion: minimumReadableVersion,
                currentVersion: currentVersion);
        }

        return parsedVersion.Value;
    }

    internal string Stamp(DocumentSchemaVersionPolicy policy, int version)
    {
        string stamp;
        try
        {
            stamp = _formatter(policy.DocumentKind, version);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.InvalidVersionFormat,
                $"The configured schema-version format could not stamp version {version} for document kind '{policy.DocumentKind}'.",
                policy.DocumentKind,
                parsedVersion: version,
                minimumReadableVersion: policy.MinimumReadableVersion,
                currentVersion: policy.CurrentVersion,
                innerException: exception);
        }

        if (string.IsNullOrWhiteSpace(stamp))
        {
            throw new DocumentSchemaVersionException(
                DocumentSchemaVersionFailure.InvalidVersionFormat,
                $"The configured schema-version format produced an empty stamp for version {version} of document kind '{policy.DocumentKind}'.",
                policy.DocumentKind,
                schemaVersion: stamp,
                parsedVersion: version,
                minimumReadableVersion: policy.MinimumReadableVersion,
                currentVersion: policy.CurrentVersion);
        }

        return stamp;
    }

    internal void ValidateRoundTrips(DocumentSchemaVersionPolicy policy)
    {
        for (var version = policy.MinimumReadableVersion;; version++)
        {
            var stamp = Stamp(policy, version);
            int? parsed;
            try
            {
                parsed = _parser(policy.DocumentKind, stamp);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                throw new DocumentSchemaVersionException(
                    DocumentSchemaVersionFailure.InvalidVersionFormat,
                    $"The configured schema-version format cannot parse its own stamp '{stamp}' for version {version} of document kind '{policy.DocumentKind}'.",
                    policy.DocumentKind,
                    schemaVersion: stamp,
                    parsedVersion: version,
                    minimumReadableVersion: policy.MinimumReadableVersion,
                    currentVersion: policy.CurrentVersion,
                    innerException: exception);
            }

            if (parsed != version)
            {
                throw new DocumentSchemaVersionException(
                    DocumentSchemaVersionFailure.InvalidVersionFormat,
                    $"The configured schema-version format stamps version {version} of document kind '{policy.DocumentKind}' as '{stamp}', but parses that stamp as {parsed?.ToString() ?? "unrecognized"}.",
                    policy.DocumentKind,
                    schemaVersion: stamp,
                    parsedVersion: parsed,
                    minimumReadableVersion: policy.MinimumReadableVersion,
                    currentVersion: policy.CurrentVersion);
            }

            if (version == policy.CurrentVersion)
                break;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OperationCanceledException and not OutOfMemoryException;
}
