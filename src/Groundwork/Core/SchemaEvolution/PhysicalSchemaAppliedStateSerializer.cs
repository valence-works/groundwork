using System.Text;
using System.Text.Json;
using Groundwork.Core.Capabilities;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.SchemaEvolution;

/// <summary>Canonical durable serialization for provider schema-history stores.</summary>
public static class PhysicalSchemaAppliedStateSerializer
{
    public static string Serialize(PhysicalSchemaAppliedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("manifestIdentity", state.ManifestIdentity.Value);
            writer.WriteString("manifestVersion", state.ManifestVersion.Value);
            writer.WritePropertyName("provider");
            writer.WriteStartObject();
            writer.WriteString("name", state.Provider.Name);
            writer.WriteString("version", state.Provider.Version);
            writer.WriteEndObject();
            writer.WriteString("targetFingerprint", state.TargetFingerprint);
            writer.WriteString("plannedAt", state.PlannedAt.ToUniversalTime().ToString("O"));
            writer.WriteString("appliedAt", state.AppliedAt.ToUniversalTime().ToString("O"));
            writer.WriteString("snapshotFingerprint", state.Snapshot.Fingerprint);
            writer.WritePropertyName("snapshot");
            writer.WriteRawValue(state.Snapshot.CanonicalJson, skipInputValidation: false);
            writer.WritePropertyName("appliedOperations");
            writer.WriteStartArray();
            foreach (var operation in state.AppliedOperations)
            {
                writer.WriteStartObject();
                writer.WriteString("identity", operation.Identity);
                writer.WriteString("fingerprint", operation.Fingerprint);
                writer.WriteString("kind", operation.Kind.ToString());
                if (operation.StorageUnit is null)
                    writer.WriteNull("storageUnit");
                else
                    writer.WriteString("storageUnit", operation.StorageUnit.Value);
                writer.WriteString("subjectIdentity", operation.SubjectIdentity);
                writer.WriteString("slotIdentity", operation.SlotIdentity);
                writer.WriteString("appliedAt", operation.AppliedAt.ToUniversalTime().ToString("O"));
                writer.WriteString("canonicalPayload", operation.CanonicalPayload);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static PhysicalSchemaAppliedState Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var provider = root.GetProperty("provider");
        var snapshotJson = root.GetProperty("snapshot").GetRawText();
        var snapshot = PhysicalSchemaAppliedSnapshot.Deserialize(snapshotJson);
        if (root.GetProperty("snapshotFingerprint").GetString() != snapshot.Fingerprint)
            throw new InvalidOperationException("Applied physical schema snapshot fingerprint is invalid.");
        var state = new PhysicalSchemaAppliedState(
            new StorageManifestIdentity(root.GetProperty("manifestIdentity").GetString()!),
            new StorageManifestVersion(root.GetProperty("manifestVersion").GetString()!),
            new ProviderIdentity(
                provider.GetProperty("name").GetString()!,
                provider.GetProperty("version").GetString()!),
            root.GetProperty("targetFingerprint").GetString()!,
            root.GetProperty("plannedAt").GetDateTimeOffset(),
            root.GetProperty("appliedAt").GetDateTimeOffset(),
            snapshot,
            root.GetProperty("appliedOperations").EnumerateArray().Select(operation =>
                new PhysicalSchemaAppliedOperation(
                    operation.GetProperty("identity").GetString()!,
                    operation.GetProperty("fingerprint").GetString()!,
                    Enum.Parse<PhysicalSchemaOperationKind>(operation.GetProperty("kind").GetString()!),
                    operation.GetProperty("storageUnit").ValueKind == JsonValueKind.Null
                        ? null
                        : new StorageUnitIdentity(operation.GetProperty("storageUnit").GetString()!),
                    operation.GetProperty("subjectIdentity").GetString()!,
                    operation.GetProperty("slotIdentity").GetString()!,
                    operation.GetProperty("appliedAt").GetDateTimeOffset(),
                    operation.GetProperty("canonicalPayload").GetString()!))
                .ToArray());

        if (!string.Equals(Serialize(state), json, StringComparison.Ordinal))
            throw new InvalidOperationException("Applied physical schema state is not in canonical form.");
        return state;
    }
}
