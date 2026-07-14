using Groundwork.Core.Manifests;
using Groundwork.Relational.Documents;
using Groundwork.Provider.Relational;
using Groundwork.Documents.Scoping;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.Documents;

public sealed class SqliteDocumentStore : RelationalDocumentStore
{
    internal SqliteDocumentStore(SqliteConnection connection, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(connection, manifest, new SqliteDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal SqliteDocumentStore(RelationalSessionFactory sessions, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(sessions, manifest, new SqliteDocumentStoreDialect(), access, scopeObserver)
    {
    }

    internal SqliteDocumentStore(string connectionString, StorageManifest manifest, DocumentStoreAccess access, IStorageScopeObserver? scopeObserver = null)
        : base(
            SqliteRelationalSessions.CreateSerialized(connectionString),
            manifest,
            new SqliteDocumentStoreDialect(),
            access,
            scopeObserver)
    {
    }
}
