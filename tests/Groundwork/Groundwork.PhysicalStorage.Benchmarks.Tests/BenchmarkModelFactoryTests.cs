using Groundwork.Core.PhysicalStorage;
using Groundwork.Sqlite;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class BenchmarkModelFactoryTests
{
    [Theory]
    [InlineData(PhysicalStorageForm.SharedDocuments, true)]
    [InlineData(PhysicalStorageForm.DedicatedDocumentTable, true)]
    [InlineData(PhysicalStorageForm.PhysicalEntityTable, false)]
    public void Model_compiles_the_scoped_mixed_direction_query_for_every_form(
        PhysicalStorageForm form,
        bool expectsLinkedStorage)
    {
        var model = BenchmarkModelFactory.CompileRelational(
            form,
            "fixed",
            SqliteGroundworkCapabilities.Provider,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.Equal(form, model.Route.Form);
        Assert.Equal(expectsLinkedStorage, model.Route.LinkedIndexStorage is not null);
        Assert.Collection(
            Assert.Single(model.Route.Indexes).Columns,
            scope => Assert.Equal("storage_scope", scope.Column.LogicalName),
            status => Assert.Equal(PhysicalSortDirection.Ascending, status.Direction),
            rank => Assert.Equal(PhysicalSortDirection.Descending, rank.Direction));
        Assert.Equal(BenchmarkModelFactory.QueryIdentity,
            Assert.Single(model.Manifest.StorageUnits.Single().PhysicalStorage!.BoundedQueries).Identity);
    }
}
