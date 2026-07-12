using Groundwork.Core.Manifests;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer.Documents;

public sealed class SqlServerDocumentStore : RelationalDocumentStore
{
    public SqlServerDocumentStore(SqlConnection connection, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(connection, manifest, new SqlServerDocumentStoreDialect(), ambientTenantId)
    {
    }

    public SqlServerDocumentStore(string connectionString, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(
            RelationalSessionFactory.Concurrent(() => new SqlConnection(connectionString)),
            manifest,
            new SqlServerDocumentStoreDialect(),
            ambientTenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }
}
