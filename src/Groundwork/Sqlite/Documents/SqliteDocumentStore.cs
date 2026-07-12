using Groundwork.Core.Manifests;
using Groundwork.Relational.Documents;
using Groundwork.Provider.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

public sealed class SqliteDocumentStore : RelationalDocumentStore
{
    public SqliteDocumentStore(SqliteConnection connection, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(connection, manifest, new SqliteDocumentStoreDialect(), ambientTenantId)
    {
    }

    internal SqliteDocumentStore(RelationalSessionFactory sessions, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(sessions, manifest, new SqliteDocumentStoreDialect(), ambientTenantId)
    {
    }

    public SqliteDocumentStore(string connectionString, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(
            SqliteRelationalSessions.CreateSerialized(connectionString),
            manifest,
            new SqliteDocumentStoreDialect(),
            ambientTenantId)
    {
    }
}
