using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Provider.Relational;
using Groundwork.SqlServer.Documents;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.RelationalProviders.Tests;

public sealed class SqlServerDocumentIdentityBoundaryTests
{
    [Fact]
    public async Task Conventional_store_rejects_overlong_identity_before_opening_a_session()
    {
        var opened = false;
        var sessions = RelationalSessionFactory.Concurrent(() =>
        {
            opened = true;
            return new SqlConnection();
        });
        var store = new SqlServerDocumentStore(
            sessions,
            RelationalTestManifests.MetadataManifest(),
            DocumentStoreAccess.Global);
        var overlong = new string('x', 451);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new SaveDocumentRequest(
            "configurationDocument",
            overlong,
            "1",
            "{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync("configurationDocument", overlong));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(new DeleteDocumentRequest(
            "configurationDocument",
            overlong)));

        Assert.False(opened);
    }
}
