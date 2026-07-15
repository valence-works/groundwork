using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.Physicalization;
using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PostgreSql;

/// <summary>
/// Applies PostgreSQL's schema-global relation namespace and 63-byte UTF-8 identifier limit.
/// Index identifiers include their owning storage unit because PostgreSQL does not scope index
/// names to their table.
/// </summary>
public sealed class PostgreSqlPhysicalNameNormalizer : IProviderPhysicalNameNormalizer
{
    public static PostgreSqlPhysicalNameNormalizer Instance { get; } = new();

    private PostgreSqlPhysicalNameNormalizer()
    {
    }

    public string Normalize(ProviderPhysicalNameContext context)
    {
        var logicalName = context.ObjectKind switch
        {
            PhysicalObjectKind.PhysicalIndex => PhysicalizationNameEncoder.Encode(
                $"{context.StorageUnit.Value}\\u001f{context.LogicalName}"),
            _ => context.LogicalName
        };
        return NormalizePhysicalName(logicalName);
    }

    public string GetCollisionScope(ProviderPhysicalNameContext context) => context.ObjectKind switch
    {
        PhysicalObjectKind.PrimaryStorage or
        PhysicalObjectKind.LinkedIndexStorage or
        PhysicalObjectKind.PhysicalIndex or
        PhysicalObjectKind.SchemaHistory => "schema-relations",
        PhysicalObjectKind.EnvelopeField or PhysicalObjectKind.ProjectedField =>
            $"{context.StorageUnit.Value}:columns",
        PhysicalObjectKind.LinkedIndexField or PhysicalObjectKind.LinkedProjectedField =>
            $"{context.StorageUnit.Value}:linked-columns",
        _ => throw new ArgumentOutOfRangeException(nameof(context), context.ObjectKind, null)
    };

    private static string NormalizePhysicalName(string value)
    {
        const int maximumBytes = 63;
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;
        var suffix = "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        var prefixBudget = maximumBytes - Encoding.UTF8.GetByteCount(suffix);
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (used + bytes > prefixBudget)
                break;
            builder.Append(rune);
            used += bytes;
        }
        return builder.Append(suffix).ToString();
    }
}
