using System.Globalization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Text;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal sealed record BenchmarkPlanNoiseIdentity(PortableStringIdentityProjection Projection)
{
    public string OriginalId => Projection.OriginalValue;

    public static BenchmarkPlanNoiseIdentity Create(
        ExecutableDocumentIdentityRoute identity,
        string prefix,
        int sequence)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        var originalId = string.Concat(prefix, sequence.ToString(CultureInfo.InvariantCulture));
        return new BenchmarkPlanNoiseIdentity(identity.Project(originalId));
    }
}
