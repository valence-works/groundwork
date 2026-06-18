using Groundwork.Core.Manifests;
using Groundwork.Relational.Documents;
using Npgsql;

namespace Groundwork.PostgreSql.Documents;

public sealed class PostgreSqlDocumentStore(NpgsqlConnection connection, StorageManifest manifest, Func<string?>? ambientTenantId = null)
    : RelationalDocumentStore(connection, manifest, new PostgreSqlDocumentStoreDialect(), ambientTenantId);
