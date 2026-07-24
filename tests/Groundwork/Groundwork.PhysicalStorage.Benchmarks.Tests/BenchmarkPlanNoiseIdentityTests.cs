using Groundwork.Core.PhysicalStorage;
using Groundwork.SqlServer;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkPlanNoiseIdentityTests
{
    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable)]
    public void Plan_noise_identity_projects_unique_canonical_keys_for_every_relational_form(
        PhysicalStorageForm form)
    {
        var route = BenchmarkModelFactory.CompileRelational(
            form,
            "plan_noise_identity",
            SqlServerGroundworkCapabilities.Provider,
            SqlServerGroundworkCapabilities.PhysicalNames).Route;
        var identities = new[] { route.Envelope.Identity, route.LinkedRelationship?.Identity }
            .OfType<ExecutableDocumentIdentityRoute>();

        foreach (var identity in identities)
        {
            var source = identity.Project("source");
            var first = BenchmarkPlanNoiseIdentity.Create(identity, "plan-noise-fixed-", 1);
            var repeated = BenchmarkPlanNoiseIdentity.Create(identity, "plan-noise-fixed-", 1);
            var second = BenchmarkPlanNoiseIdentity.Create(identity, "plan-noise-fixed-", 2);

            Assert.Equal(first, repeated);
            Assert.Equal(identity.Project(first.OriginalId), first.Projection);
            Assert.Equal(identity.Project(second.OriginalId), second.Projection);
            Assert.NotEqual(source.LookupKey, first.Projection.LookupKey);
            Assert.NotEqual(first.Projection.LookupKey, second.Projection.LookupKey);
            Assert.Equal(32, Convert.FromHexString(first.Projection.LookupKey).Length);
            Assert.InRange(Convert.FromHexString(first.Projection.ComparisonKey).Length, 1, 1350);
        }
    }
}
