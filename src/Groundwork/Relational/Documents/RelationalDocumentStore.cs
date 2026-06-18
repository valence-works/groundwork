using System.Data;
using System.Data.Common;
using System.Text.Json;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Physicalization;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.Documents;

public class RelationalDocumentStore(DbConnection connection, StorageManifest manifest, RelationalDocumentStoreDialect dialect, Func<string?>? ambientTenantId = null) : IDocumentStore
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(request.DocumentKind);
        return await ExecuteWithConnectionAsync(async ct =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var result = await SaveCoreAsync(request, unit, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Saved)
                await transaction.CommitAsync(ct);

            return result;
        }, cancellationToken);
    }

    private async Task<DocumentStoreWriteResult> SaveCoreAsync(
        SaveDocumentRequest request,
        StorageUnit unit,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var existing = await LoadCoreAsync(request.DocumentKind, request.Id, transaction, ct);
        if (existing is not null && request.ExpectedVersion is not null && existing.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        if (existing is null && request.ExpectedVersion is not null)
            return DocumentStoreWriteResult.NotFound;

        var now = DateTimeOffset.UtcNow;
        var version = existing is null ? 1 : existing.Version + 1;
        var createdAt = existing?.CreatedAt ?? now;

        if (existing is null)
        {
            try
            {
                await InsertDocumentAsync(request, version, createdAt, now, transaction, ct);
            }
            catch (DbException exception) when (dialect.IsDuplicateDocumentKeyException(exception))
            {
                return DocumentStoreWriteResult.ConcurrencyConflict;
            }
        }
        else
        {
            var updated = await UpdateDocumentAsync(request, version, now, transaction, ct);
            if (!updated)
                return WriteMissResult(request.ExpectedVersion);
        }

        try
        {
            await DeleteIndexesAsync(request.DocumentKind, request.Id, transaction, ct);
            await InsertIndexesAsync(unit, request.Id, request.ContentJson, transaction, ct);
            await RefreshPhysicalizedAsync(unit, request.Id, version, request.ContentJson, transaction, ct);
        }
        catch (DbException exception) when (dialect.IsUniqueIndexException(exception))
        {
            return DocumentStoreWriteResult.ConcurrencyConflict;
        }
        catch (DbException exception) when (dialect.IsWriteDependencyException(exception))
        {
            return WriteMissResult(request.ExpectedVersion);
        }

        return DocumentStoreWriteResult.Saved(new DocumentEnvelope(
            request.DocumentKind,
            request.Id,
            request.SchemaVersion,
            version,
            request.ContentJson,
            createdAt,
            now));
    }

    public async Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default)
    {
        _ = GetUnit(documentKind);
        return await ExecuteWithConnectionAsync(
            ct => LoadCoreAsync(documentKind, id, null, ct),
            cancellationToken);
    }

    public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(request.DocumentKind);
        return await ExecuteWithConnectionAsync(async ct =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var result = await DeleteCoreAsync(request, unit, transaction, ct);
            if (result.Status == DocumentStoreWriteStatus.Deleted)
                await transaction.CommitAsync(ct);

            return result;
        }, cancellationToken);
    }

    private async Task<DocumentStoreWriteResult> DeleteCoreAsync(
        DeleteDocumentRequest request,
        StorageUnit unit,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var existing = await LoadCoreAsync(request.DocumentKind, request.Id, transaction, ct);
        if (existing is null)
            return DocumentStoreWriteResult.NotFound;

        if (request.ExpectedVersion is not null && existing.Version != request.ExpectedVersion)
            return DocumentStoreWriteResult.ConcurrencyConflict;

        await using var command = CreateCommand(dialect.DeleteDocumentCommandSql(request.ExpectedVersion is not null), transaction);
        AddParameter(command, "kind", request.DocumentKind);
        AddParameter(command, "id", request.Id);
        if (request.ExpectedVersion is not null)
            AddParameter(command, "expectedVersion", request.ExpectedVersion.Value);

        var deletedRows = await command.ExecuteNonQueryAsync(ct);
        if (deletedRows == 0)
            return WriteMissResult(request.ExpectedVersion);

        await DeleteIndexesAsync(request.DocumentKind, request.Id, transaction, ct);
        await DeletePhysicalizedAsync(unit, request.Id, transaction, ct);

        return DocumentStoreWriteResult.Deleted;
    }

    public TransactionBoundary TransactionBoundary => TransactionBoundary.CrossUnitAtomic;

    public async Task<IDocumentUnitOfWork> BeginAsync(DocumentCommitScope scope, CancellationToken cancellationToken = default)
    {
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            return new RelationalDocumentUnitOfWork(this, transaction);
        }
        catch
        {
            connectionGate.Release();
            throw;
        }
    }

    public async Task<IReadOnlyList<DocumentEnvelope>> QueryAsync(DocumentStoreQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var (skip, take) = NormalizePaging(query);
        var index = unit.Indexes.SingleOrDefault(index => index.Identity == query.IndexName)
            ?? throw new UndeclaredDocumentIndexException(query.DocumentKind, query.IndexName);

        if (!index.SupportedOperations.Contains(PortableQueryOperation.Equal))
            throw new UndeclaredDocumentIndexException(query.DocumentKind, query.IndexName);

        if (index.Fields.Count != 1)
            throw new UndeclaredDocumentIndexException(query.DocumentKind, query.IndexName);

        return await ExecuteWithConnectionAsync(async ct =>
        {
            await using var command = CreateQueryCommand(unit, query);
            AddParameter(command, "kind", query.DocumentKind);
            AddParameter(command, "index", query.IndexName);
            AddParameter(command, "value", query.Value);
            AddParameter(command, "take", take);
            AddParameter(command, "skip", skip);

            var documents = new List<DocumentEnvelope>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                documents.Add(ReadEnvelope(reader));

            return documents;
        }, cancellationToken);
    }

    public async Task<DocumentQueryResult> QueryAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var translator = RelationalClosedQueryTranslator.Translate(unit, query, dialect, ambientTenantId?.Invoke(), out var whereSql, out var orderSql);

        return await ExecuteWithConnectionAsync(async ct =>
        {
            var total = await CountClosedAsync(translator, whereSql, ct);
            if (total == 0 || query.Take == 0)
                return new DocumentQueryResult(Array.Empty<DocumentEnvelope>(), total);

            await using var command = CreateCommand(translator.SelectSql(whereSql, orderSql, query.Skip ?? 0, query.Take));
            AddClosedQueryParameters(command, translator.Parameters);

            var documents = new List<DocumentEnvelope>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                documents.Add(ReadEnvelope(reader));

            return new DocumentQueryResult(documents, total);
        }, cancellationToken);
    }

    public async Task<DocumentEnvelope?> FirstOrDefaultAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var translator = RelationalClosedQueryTranslator.Translate(unit, query, dialect, ambientTenantId?.Invoke(), out var whereSql, out var orderSql);

        return await ExecuteWithConnectionAsync(async ct =>
        {
            await using var command = CreateCommand(translator.SelectSql(whereSql, orderSql, 0, 1));
            AddClosedQueryParameters(command, translator.Parameters);

            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadEnvelope(reader) : null;
        }, cancellationToken);
    }

    public async Task<bool> AnyAsync(PortableDocumentQuery query, CancellationToken cancellationToken = default)
    {
        var unit = GetUnit(query.DocumentKind);
        var translator = RelationalClosedQueryTranslator.Translate(unit, query, dialect, ambientTenantId?.Invoke(), out var whereSql, out _);

        return await ExecuteWithConnectionAsync(
            async ct => await CountClosedAsync(translator, whereSql, ct) > 0,
            cancellationToken);
    }

    private async Task<long> CountClosedAsync(RelationalClosedQueryTranslator translator, string whereSql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(translator.CountSql(whereSql));
        AddClosedQueryParameters(command, translator.WhereParameters);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar);
    }

    private void AddClosedQueryParameters(DbCommand command, IEnumerable<KeyValuePair<string, object>> values)
    {
        foreach (var (name, value) in values)
            AddParameter(command, name, value);
    }

    private DbCommand CreateQueryCommand(StorageUnit unit, DocumentStoreQuery query)
    {
        var physicalizedField = PhysicalizationProjection.EligibleFields(unit).SingleOrDefault(field => field.Name == query.IndexName);
        if (physicalizedField is null)
            return CreateCommand(dialect.QueryByIndexSql);

        var table = RelationalPhysicalizationNames.TableName(unit);
        var column = RelationalPhysicalizationNames.ColumnName(physicalizedField);
        return CreateCommand(dialect.QueryByPhysicalizedSql(table, column));
    }

    private async Task InsertDocumentAsync(
        SaveDocumentRequest request,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dialect.InsertDocumentSql, transaction);
        AddDocumentParameters(command, request, version, createdAt, updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> UpdateDocumentAsync(
        SaveDocumentRequest request,
        long version,
        DateTimeOffset updatedAt,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dialect.UpdateDocumentCommandSql(request.ExpectedVersion is not null), transaction);
        AddDocumentParameters(command, request, version, updatedAt);
        if (request.ExpectedVersion is not null)
            AddParameter(command, "expectedVersion", request.ExpectedVersion.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private void AddDocumentParameters(DbCommand command, SaveDocumentRequest request, long version, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        AddDocumentParameters(command, request, version);
        AddParameter(command, "createdUtc", createdAt.ToString("O"));
        AddParameter(command, "updatedUtc", updatedAt.ToString("O"));
    }

    private void AddDocumentParameters(DbCommand command, SaveDocumentRequest request, long version, DateTimeOffset updatedAt)
    {
        AddDocumentParameters(command, request, version);
        AddParameter(command, "updatedUtc", updatedAt.ToString("O"));
    }

    private void AddDocumentParameters(DbCommand command, SaveDocumentRequest request, long version)
    {
        AddParameter(command, "kind", request.DocumentKind);
        AddParameter(command, "id", request.Id);
        AddParameter(command, "schemaVersion", request.SchemaVersion);
        AddParameter(command, "version", version);
        AddParameter(command, "content", request.ContentJson);
    }

    private async Task<DocumentEnvelope?> LoadCoreAsync(string documentKind, string id, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dialect.LoadDocumentSql, transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnvelope(reader) : null;
    }

    private async Task DeleteIndexesAsync(string documentKind, string id, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(dialect.DeleteIndexesSql, transaction);
        AddParameter(command, "kind", documentKind);
        AddParameter(command, "id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertIndexesAsync(StorageUnit unit, string id, string contentJson, DbTransaction transaction, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(contentJson);
        foreach (var index in unit.Indexes)
        {
            if (!TryGetIndexValue(document.RootElement, index, out var value))
                continue;

            await using var command = CreateCommand(dialect.InsertIndexSql, transaction);
            AddParameter(command, "kind", unit.Identity.Value);
            AddParameter(command, "index", index.Identity);
            AddParameter(command, "value", value);
            AddParameter(command, "documentId", id);
            AddParameter(command, "isUnique", dialect.Boolean(index.IsUnique));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task RefreshPhysicalizedAsync(
        StorageUnit unit,
        string id,
        long version,
        string contentJson,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var fields = PhysicalizationProjection.EligibleFields(unit);
        if (fields.Count == 0)
            return;

        await DeletePhysicalizedAsync(unit, id, transaction, cancellationToken);

        using var document = JsonDocument.Parse(contentJson);
        var table = RelationalPhysicalizationNames.TableName(unit);
        var columnNames = fields.Select(RelationalPhysicalizationNames.ColumnName).ToList();
        await using var command = CreateCommand(dialect.InsertPhysicalizedSql(table, columnNames), transaction);
        AddParameter(command, "kind", unit.Identity.Value);
        AddParameter(command, "id", id);
        AddParameter(command, "version", version);

        for (var index = 0; index < fields.Count; index++)
        {
            var value = TryGetPropertyPath(document.RootElement, fields[index].Path, out var element)
                ? NormalizeValue(element)
                : null;
            AddParameter(command, $"physicalized{index}", value is null ? DBNull.Value : value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeletePhysicalizedAsync(StorageUnit unit, string id, DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (PhysicalizationProjection.EligibleFields(unit).Count == 0)
            return;

        await using var command = CreateCommand(dialect.DeletePhysicalizedSql(RelationalPhysicalizationNames.TableName(unit)), transaction);
        AddParameter(command, "kind", unit.Identity.Value);
        AddParameter(command, "id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(string commandText, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        return command;
    }

    private void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = dialect.Parameter(name);
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private StorageUnit GetUnit(string documentKind) =>
        manifest.StorageUnits.SingleOrDefault(unit => unit.Identity.Value == documentKind)
        ?? throw new InvalidOperationException($"Document kind '{documentKind}' is not declared by manifest '{manifest.Identity}'.");

    private static bool TryGetIndexValue(JsonElement root, IndexDeclaration index, out string value)
    {
        value = "";
        if (index.Fields.Count != 1)
            return false;

        if (!TryGetPropertyPath(root, index.Fields[0].Path, out var element))
            return false;

        value = NormalizeValue(element);
        return value.Length > 0 || element.ValueKind == JsonValueKind.String;
    }

    private static bool TryGetPropertyPath(JsonElement root, string path, out JsonElement element)
    {
        element = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return false;
        }

        return element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private static string NormalizeValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };

    private static DocumentEnvelope ReadEnvelope(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            DateTimeOffset.Parse(reader.GetString(6)));

    private static DocumentStoreWriteResult WriteMissResult(long? expectedVersion) =>
        expectedVersion is null
            ? DocumentStoreWriteResult.NotFound
            : DocumentStoreWriteResult.ConcurrencyConflict;

    private static (int Skip, int Take) NormalizePaging(DocumentStoreQuery query)
    {
        var skip = query.Skip ?? 0;
        var take = query.Take ?? 100;

        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(DocumentStoreQuery.Skip), skip, "Skip must be greater than or equal to 0.");

        if (take < 0)
            throw new ArgumentOutOfRangeException(nameof(DocumentStoreQuery.Take), take, "Take must be greater than or equal to 0.");

        return (skip, take);
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
    }

    private async Task<T> ExecuteWithConnectionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);
            return await operation(cancellationToken);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private sealed class RelationalDocumentUnitOfWork(RelationalDocumentStore store, DbTransaction transaction) : IDocumentUnitOfWork
    {
        private bool completed;

        public async Task<DocumentStoreWriteResult> SaveAsync(SaveDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            var unit = store.GetUnit(request.DocumentKind);
            return await store.SaveCoreAsync(request, unit, transaction, cancellationToken);
        }

        public async Task<DocumentStoreWriteResult> DeleteAsync(DeleteDocumentRequest request, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            var unit = store.GetUnit(request.DocumentKind);
            return await store.DeleteCoreAsync(request, unit, transaction, cancellationToken);
        }

        public async Task<DocumentEnvelope?> LoadAsync(string documentKind, string id, CancellationToken cancellationToken = default)
        {
            EnsureActive();
            _ = store.GetUnit(documentKind);
            return await store.LoadCoreAsync(documentKind, id, transaction, cancellationToken);
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            finally
            {
                await CompleteAsync();
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            EnsureActive();
            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            finally
            {
                await CompleteAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (completed)
                return;

            try
            {
                await transaction.RollbackAsync();
            }
            catch
            {
                // Best-effort rollback when disposed without an explicit commit/rollback.
            }
            finally
            {
                await CompleteAsync();
            }
        }

        private async Task CompleteAsync()
        {
            if (completed)
                return;

            completed = true;
            await transaction.DisposeAsync();
            store.connectionGate.Release();
        }

        private void EnsureActive()
        {
            if (completed)
                throw new InvalidOperationException("The document transaction has already been committed or rolled back.");
        }
    }
}
