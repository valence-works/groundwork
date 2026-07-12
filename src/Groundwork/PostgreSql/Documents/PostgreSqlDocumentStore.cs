using Groundwork.Core.Manifests;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

public sealed class PostgreSqlDocumentStore : RelationalDocumentStore
{
    public PostgreSqlDocumentStore(NpgsqlConnection connection, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(connection, manifest, new PostgreSqlDocumentStoreDialect(), ambientTenantId)
    {
    }

    internal PostgreSqlDocumentStore(RelationalSessionFactory sessions, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(sessions, manifest, new PostgreSqlDocumentStoreDialect(), ambientTenantId)
    {
    }

    public PostgreSqlDocumentStore(string connectionString, StorageManifest manifest, Func<string?>? ambientTenantId = null)
        : base(
            RelationalSessionFactory.Concurrent(() => new NpgsqlConnection(connectionString)),
            manifest,
            new PostgreSqlDocumentStoreDialect(),
            ambientTenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }
}
