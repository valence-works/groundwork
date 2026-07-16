namespace Groundwork.Documents.Serialization;

/// <summary>
/// Declares the schema-version compatibility window for one document kind.
/// </summary>
public sealed record DocumentSchemaVersionPolicy
{
    public DocumentSchemaVersionPolicy(
        string documentKind,
        int minimumReadableVersion,
        int currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKind);

        if (minimumReadableVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumReadableVersion), minimumReadableVersion, "Schema versions start at 1.");

        if (currentVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(currentVersion), currentVersion, "Schema versions start at 1.");

        if (minimumReadableVersion > currentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumReadableVersion),
                minimumReadableVersion,
                "The minimum readable version cannot exceed the current version.");
        }

        DocumentKind = documentKind;
        MinimumReadableVersion = minimumReadableVersion;
        CurrentVersion = currentVersion;
    }

    public string DocumentKind { get; }

    public int MinimumReadableVersion { get; }

    public int CurrentVersion { get; }
}
