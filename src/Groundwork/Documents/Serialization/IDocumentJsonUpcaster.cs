using System.Text.Json.Nodes;

namespace Groundwork.Documents.Serialization;

/// <summary>
/// Rewrites one document kind's canonical JSON from <see cref="FromVersion"/> to the next integer version.
/// </summary>
public interface IDocumentJsonUpcaster
{
    string DocumentKind { get; }

    int FromVersion { get; }

    JsonObject Upcast(JsonObject content);
}
