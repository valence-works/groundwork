using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Relational.Documents;
using Groundwork.Relational.Physicalization;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite.PhysicalStorage;

/// <summary>
/// SQLite reference executor for the provider-neutral physical-schema protocol. Semantic operation
/// acknowledgements and the complete typed applied snapshot are stored separately so a retry can
/// reconcile acknowledgement loss without claiming an unapplied target.
/// </summary>
public sealed class SqlitePhysicalSchemaExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
{
    private static readonly ConcurrentDictionary<PhysicalSchemaTargetIdentity, SemaphoreSlim> ApplicationLocks = new();
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    public SqlitePhysicalSchemaExecutor(SqliteConnection connection) =>
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var gate = ApplicationLocks.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        FileStream? fileLock = null;
        try
        {
            fileLock = await AcquireFileLockAsync(target, cancellationToken);
            await WithConnectionAsync(EnsureInfrastructureAsync, cancellationToken);
            return new ApplicationLock(target, () =>
            {
                fileLock?.Dispose();
                gate.Release();
            });
        }
        catch
        {
            fileLock?.Dispose();
            gate.Release();
            throw;
        }
    }

    public async ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async ct =>
        {
            RequireApplicationLock(applicationLock, target);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT applied_state_json
                FROM groundwork_physical_schema_state
                WHERE manifest_id = @manifestId AND provider_name = @providerName;
                """;
            command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
            command.Parameters.AddWithValue("@providerName", target.ProviderName);
            var json = await command.ExecuteScalarAsync(ct) as string;
            return json is null
                ? PhysicalSchemaHistoryState.Empty
                : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
        }, cancellationToken);

    public async ValueTask<PhysicalSchemaInspectionResult> InspectHistoryAsync(
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        if (string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal))
        {
            return await WithConnectionAsync(
                ct => ReadAndValidateInspectedHistoryAsync(this, connection, target, ct),
                cancellationToken);
        }

        builder.Mode = SqliteOpenMode.ReadOnly;
        await using var inspection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await inspection.OpenAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 14)
        {
            return new PhysicalSchemaInspectionResult(PhysicalSchemaHistoryState.Empty, IsAppliedSchemaValid: true);
        }
        return await ReadAndValidateInspectedHistoryAsync(
            new SqlitePhysicalSchemaExecutor(inspection),
            inspection,
            target,
            cancellationToken);
    }

    public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(target);
            RequireApplicationLock(applicationLock, target);
            var prior = await ReadOperationAsync(target, operation.Identity, ct);
            if (prior is not null)
            {
                if (!string.Equals(prior.Value.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                    throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, prior.Value.Fingerprint);
                if (operation is ValidatePhysicalSchemaOperation || operation is BackfillCanonicalJsonOperation)
                {
                    await using var reconciliationTransaction = await connection.BeginTransactionAsync(ct);
                    if (operation is ValidatePhysicalSchemaOperation ||
                        !await IsOperationPublishedAsync(target, operation, reconciliationTransaction, ct))
                    {
                        await ApplyOperationCoreAsync(operation, reconciliationTransaction, ct);
                    }
                    await reconciliationTransaction.CommitAsync(ct);
                }
                return new PhysicalSchemaOperationAcknowledgement(operation.Identity, prior.Value.Fingerprint, prior.Value.AppliedAt);
            }

            await using var transaction = await connection.BeginTransactionAsync(ct);
            await ApplyOperationCoreAsync(operation, transaction, ct);
            var appliedAt = DateTimeOffset.UtcNow;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO groundwork_physical_schema_operations
                    (manifest_id, provider_name, operation_id, operation_fingerprint, applied_utc)
                    VALUES (@manifestId, @providerName, @identity, @fingerprint, @appliedUtc);
                    """;
                command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
                command.Parameters.AddWithValue("@providerName", target.ProviderName);
                command.Parameters.AddWithValue("@identity", operation.Identity);
                command.Parameters.AddWithValue("@fingerprint", operation.Fingerprint);
                command.Parameters.AddWithValue("@appliedUtc", appliedAt.ToUniversalTime().ToString("O"));
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            var durable = await ReadOperationAsync(target, operation.Identity, ct)
                ?? throw new InvalidOperationException($"Physical operation '{operation.Identity}' was not durably recorded.");
            if (!string.Equals(durable.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, durable.Fingerprint);
            return new PhysicalSchemaOperationAcknowledgement(operation.Identity, durable.Fingerprint, durable.AppliedAt);
        }, cancellationToken);

    public async ValueTask RecordAppliedStateAsync(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken) =>
        await WithConnectionAsync(async ct =>
        {
            RequireApplicationLock(
                applicationLock,
                new PhysicalSchemaTargetIdentity(state.ManifestIdentity, state.Provider.Name));
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var current = await ReadTargetFingerprintAsync(state.ManifestIdentity.Value, state.Provider.Name, transaction, ct);
            if (current == state.TargetFingerprint)
            {
                await transaction.CommitAsync(ct);
                return;
            }
            if (!string.Equals(current, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Physical schema applied-state compare-and-swap failed. Expected '{expectedAppliedTargetFingerprint ?? "<empty>"}', found '{current ?? "<empty>"}'.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = current is null
                ? """
                    INSERT INTO groundwork_physical_schema_state
                    (manifest_id, provider_name, target_fingerprint, applied_state_json)
                    VALUES (@manifestId, @providerName, @fingerprint, @json);
                    """
                : """
                    UPDATE groundwork_physical_schema_state
                    SET target_fingerprint = @fingerprint, applied_state_json = @json
                    WHERE manifest_id = @manifestId AND provider_name = @providerName
                      AND target_fingerprint = @expected;
                    """;
            command.Parameters.AddWithValue("@manifestId", state.ManifestIdentity.Value);
            command.Parameters.AddWithValue("@providerName", state.Provider.Name);
            command.Parameters.AddWithValue("@fingerprint", state.TargetFingerprint);
            command.Parameters.AddWithValue("@json", PhysicalSchemaAppliedStateSerializer.Serialize(state));
            if (current is not null)
                command.Parameters.AddWithValue("@expected", expectedAppliedTargetFingerprint!);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Physical schema applied-state compare-and-swap lost a concurrent update.");
            await transaction.CommitAsync(ct);
        }, cancellationToken);

    private async Task ApplyOperationCoreAsync(
        PhysicalSchemaOperation operation,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation create:
                await CreatePrimaryAsync(create.Route, transaction, cancellationToken);
                break;
            case CreatePhysicalEntityStorageOperation create:
                await CreatePrimaryAsync(create.Route, transaction, cancellationToken);
                break;
            case CreateLinkedStorageOperation create:
                await CreateLinkedAsync(create.Route, transaction, cancellationToken);
                break;
            case AddProjectedColumnOperation add:
                RelationalPhysicalStorageColumns.Validate(add.Route);
                await AddColumnAsync(add.Storage.Name.Identifier, add.Column.Column.Identifier, add.Column.Definition, transaction, cancellationToken);
                break;
            case FinalizeProjectedColumnOperation finalize:
                RelationalPhysicalStorageColumns.Validate(finalize.Route);
                await FinalizeColumnAsync(
                    finalize.Storage.Name.Identifier,
                    finalize.Column.Column.Identifier,
                    finalize.Column.Definition,
                    transaction,
                    cancellationToken);
                break;
            case CreatePhysicalIndexOperation create:
                RelationalPhysicalStorageColumns.Validate(create.Route);
                await CreateIndexAsync(create.Index, create.Storage.Name.Identifier, transaction, cancellationToken);
                break;
            case BackfillCanonicalJsonOperation backfill:
                if (backfill.Route is not null)
                    RelationalPhysicalStorageColumns.Validate(backfill.Route);
                await BackfillAsync(backfill, transaction, cancellationToken);
                break;
            case ValidatePhysicalSchemaOperation validate:
                await ValidateAsync(validate, transaction, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"SQLite cannot execute physical schema operation '{operation.Kind}'.");
        }
    }

    private async Task CreatePrimaryAsync(ExecutableStorageRoute route, DbTransaction transaction, CancellationToken ct)
    {
        RelationalPhysicalStorageColumns.Validate(route);
        var envelope = route.Envelope;
        var columns = new[]
        {
            $"{Q(envelope.DocumentKind.Identifier)} TEXT NOT NULL",
            $"{Q(envelope.StorageScope.Identifier)} TEXT NOT NULL",
            $"{Q(envelope.Id.Identifier)} TEXT NOT NULL",
            $"{Q(envelope.SchemaVersion.Identifier)} TEXT NOT NULL",
            $"{Q(envelope.Version.Identifier)} INTEGER NOT NULL",
            $"{Q(envelope.CanonicalJson.Identifier)} TEXT NOT NULL",
            $"{Q(RelationalPhysicalStorageColumns.CreatedUtc)} TEXT NOT NULL",
            $"{Q(RelationalPhysicalStorageColumns.UpdatedUtc)} TEXT NOT NULL",
            $"PRIMARY KEY ({string.Join(", ", route.PrimaryKey.Columns.Select(column => Q(column.Identifier)))})"
        };
        await ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {Q(route.PrimaryStorage.Name.Identifier)} ({string.Join(", ", columns)});",
            transaction,
            ct);
        await ValidatePrimaryStorageAsync(route, transaction, ct);
    }

    private async Task CreateLinkedAsync(ExecutableStorageRoute route, DbTransaction transaction, CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        var key = route.AuxiliaryKey ?? throw new InvalidOperationException("Linked storage requires an auxiliary key.");
        var columns = new[]
        {
            $"{Q(relationship.DocumentKind.Identifier)} TEXT NOT NULL",
            $"{Q(relationship.StorageScope.Identifier)} TEXT NOT NULL",
            $"{Q(relationship.DocumentId.Identifier)} TEXT NOT NULL",
            $"PRIMARY KEY ({string.Join(", ", key.Columns.Select(column => Q(column.Identifier)))})"
        };
        await ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {Q(route.LinkedIndexStorage!.Name.Identifier)} ({string.Join(", ", columns)});",
            transaction,
            ct);
        await ValidateLinkedStorageAsync(route, transaction, ct);
    }

    private async Task AddColumnAsync(
        string table,
        string column,
        ProjectedColumnDefinition definition,
        DbTransaction transaction,
        CancellationToken ct)
    {
        SqlitePhysicalValueConverter.Validate(definition);
        if (await ColumnExistsAsync(table, column, transaction, ct))
        {
            await ValidateProjectedColumnStageAsync(table, column, definition, transaction, ct);
            return;
        }
        var staged = definition.IsNullable ? definition : definition with { IsNullable = true };
        await ExecuteAsync(
            $"ALTER TABLE {Q(table)} ADD COLUMN {ProjectedColumnSql(column, staged)};",
            transaction,
            ct);
        await ValidateProjectedColumnStageAsync(table, column, definition, transaction, ct);
    }

    private async Task FinalizeColumnAsync(
        string table,
        string column,
        ProjectedColumnDefinition definition,
        DbTransaction transaction,
        CancellationToken ct)
    {
        if (definition.IsNullable)
        {
            await ValidateProjectedColumnAsync(table, column, definition, transaction, ct);
            return;
        }

        var actual = await ReadColumnsAsync(table, transaction, ct);
        if (!actual.TryGetValue(column, out var found))
            throw new InvalidOperationException($"Projected column '{table}.{column}' is missing.");
        if (found.IsNotNull)
        {
            await ValidateProjectedColumnAsync(table, column, definition, transaction, ct);
            return;
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = (SqliteTransaction)transaction;
            count.CommandText = $"SELECT COUNT(*) FROM {Q(table)} WHERE {Q(column)} IS NULL;";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidDataException(
                    $"Projected column '{table}.{column}' cannot be made required because canonical backfill left null values.");
            }
        }

        await RebuildTableWithFinalizedColumnAsync(table, column, definition, actual.Keys.ToArray(), transaction, ct);
        await ValidateProjectedColumnAsync(table, column, definition, transaction, ct);
    }

    private async Task RebuildTableWithFinalizedColumnAsync(
        string table,
        string column,
        ProjectedColumnDefinition definition,
        IReadOnlyList<string> columns,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var createSql = await ReadCreateSqlAsync("table", table, transaction, ct);
        var indexSql = await ReadIndexCreationSqlAsync(table, transaction, ct);
        var temporary = $"groundwork_rebuild_{Guid.NewGuid():N}";
        var rebuiltSql = SqliteCreateTableSql.ReplaceTableAndColumn(
            createSql,
            table,
            Q(temporary),
            column,
            ProjectedColumnSql(column, definition));
        await ExecuteAsync(rebuiltSql + ";", transaction, ct);
        var columnList = string.Join(", ", columns.Select(Q));
        await ExecuteAsync(
            $"INSERT INTO {Q(temporary)} ({columnList}) SELECT {columnList} FROM {Q(table)};",
            transaction,
            ct);
        await ExecuteAsync($"DROP TABLE {Q(table)};", transaction, ct);
        await ExecuteAsync($"ALTER TABLE {Q(temporary)} RENAME TO {Q(table)};", transaction, ct);
        foreach (var sql in indexSql)
            await ExecuteAsync(sql + ";", transaction, ct);
    }

    private async Task ValidateProjectedColumnStageAsync(
        string table,
        string column,
        ProjectedColumnDefinition definition,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var staged = definition.IsNullable ? definition : definition with { IsNullable = true };
        await ValidateProjectedColumnAsync(table, column, staged, transaction, ct);
    }

    private async Task CreateIndexAsync(ExecutablePhysicalIndexRoute index, string table, DbTransaction transaction, CancellationToken ct)
    {
        var unique = index.IsUnique ? "UNIQUE " : string.Empty;
        var columns = string.Join(", ", index.Columns.Select(column =>
            $"{Q(column.Column.Identifier)} {(column.Direction == PhysicalSortDirection.Descending ? "DESC" : "ASC")}"));
        await ExecuteAsync($"CREATE {unique}INDEX IF NOT EXISTS {Q(index.Name.Identifier)} ON {Q(table)} ({columns});", transaction, ct);
        await ValidateIndexAsync(index, table, transaction, ct);
    }

    private async Task BackfillAsync(BackfillCanonicalJsonOperation operation, DbTransaction transaction, CancellationToken ct)
    {
        var route = operation.Route ?? throw new InvalidOperationException("SQLite physical backfill requires an executable route.");
        if (operation.Target == ExecutableStorageObjectRole.PrimaryStorage)
        {
            var selected = SelectBackfillColumns(route, operation, ExecutableStorageObjectRole.PrimaryStorage);
            await BackfillPrimaryAsync(route, selected, transaction, ct);
            return;
        }

        await BackfillLinkedAsync(route, transaction, ct);
    }

    private async Task BackfillLinkedAsync(
        ExecutableStorageRoute route,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        // A linked row is one unit-owned projection aggregate. Rebuild the complete row even when
        // one additive column triggered the operation; a partial INSERT cannot satisfy previously
        // declared non-null projections before SQLite evaluates the auxiliary-key conflict.
        var selected = route.ProjectedColumns
            .Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage)
            .ToArray();
        var relationColumns = new[]
        {
            relationship.DocumentKind.Identifier,
            relationship.StorageScope.Identifier,
            relationship.DocumentId.Identifier
        };
        var insertColumns = relationColumns.Concat(selected.Select(column => column.Column.Identifier)).ToArray();
        var updates = selected.Length == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", selected.Select(column =>
                $"{Q(column.Column.Identifier)} = excluded.{Q(column.Column.Identifier)}"));
        await ForEachCanonicalDocumentBatchAsync(route, transaction, ct, async document =>
        {
            var values = RelationalPhysicalProjectionValues.Read(document.CanonicalJson, selected);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                $"INSERT INTO {Q(route.LinkedIndexStorage!.Name.Identifier)} ({string.Join(", ", insertColumns.Select(Q))}) " +
                $"VALUES ({string.Join(", ", insertColumns.Select((_, index) => $"@v{index}"))}) " +
                $"ON CONFLICT ({string.Join(", ", route.AuxiliaryKey!.Columns.Select(column => Q(column.Identifier)))}) {updates};";
            command.Parameters.AddWithValue("@v0", route.Discriminator.Value);
            command.Parameters.AddWithValue("@v1", document.Scope);
            command.Parameters.AddWithValue("@v2", document.Id);
            for (var index = 0; index < selected.Length; index++)
                command.Parameters.AddWithValue(
                    $"@v{index + 3}",
                    SqlitePhysicalValueConverter.ToStorage(
                        values[selected[index].Definition.LogicalName],
                        selected[index].Definition) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        });
    }

    private async Task BackfillPrimaryAsync(
        ExecutableStorageRoute route,
        IReadOnlyList<ExecutableProjectedColumnRoute> selected,
        DbTransaction transaction,
        CancellationToken ct)
    {
        if (selected.Count == 0)
            return;
        await ForEachCanonicalDocumentBatchAsync(route, transaction, ct, async document =>
        {
            var values = RelationalPhysicalProjectionValues.Read(document.CanonicalJson, selected);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                $"UPDATE {Q(route.PrimaryStorage.Name.Identifier)} SET " +
                string.Join(", ", selected.Select((column, index) => $"{Q(column.Column.Identifier)} = @v{index}")) +
                $" WHERE {Q(route.Discriminator.Column.Identifier)} = @kind" +
                $" AND {Q(route.ScopeKey.Column.Identifier)} = @scope" +
                $" AND {Q(route.Envelope.Id.Identifier)} = @id;";
            for (var index = 0; index < selected.Count; index++)
                command.Parameters.AddWithValue(
                    $"@v{index}",
                    SqlitePhysicalValueConverter.ToStorage(
                        values[selected[index].Definition.LogicalName],
                        selected[index].Definition) ?? DBNull.Value);
            command.Parameters.AddWithValue("@kind", route.Discriminator.Value);
            command.Parameters.AddWithValue("@scope", document.Scope);
            command.Parameters.AddWithValue("@id", document.Id);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException($"Canonical backfill lost document '{document.Id}' in scope '{document.Scope}'.");
        });
    }

    private async Task ForEachCanonicalDocumentBatchAsync(
        ExecutableStorageRoute route,
        DbTransaction transaction,
        CancellationToken ct,
        Func<CanonicalDocument, Task> action)
    {
        const int batchSize = 256;
        string? afterScope = null;
        string? afterId = null;
        while (true)
        {
            var batch = new List<CanonicalDocument>(batchSize);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                var cursor = afterScope is null
                    ? string.Empty
                    : $" AND ({Q(route.ScopeKey.Column.Identifier)} > @afterScope OR " +
                      $"({Q(route.ScopeKey.Column.Identifier)} = @afterScope AND {Q(route.Envelope.Id.Identifier)} > @afterId))";
                command.CommandText =
                    $"SELECT {Q(route.ScopeKey.Column.Identifier)}, {Q(route.Envelope.Id.Identifier)}, {Q(route.Envelope.CanonicalJson.Identifier)} " +
                    $"FROM {Q(route.PrimaryStorage.Name.Identifier)} " +
                    $"WHERE {Q(route.Discriminator.Column.Identifier)} = @kind{cursor} " +
                    $"ORDER BY {Q(route.ScopeKey.Column.Identifier)}, {Q(route.Envelope.Id.Identifier)} LIMIT {batchSize};";
                command.Parameters.AddWithValue("@kind", route.Discriminator.Value);
                if (afterScope is not null)
                {
                    command.Parameters.AddWithValue("@afterScope", afterScope);
                    command.Parameters.AddWithValue("@afterId", afterId!);
                }
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    batch.Add(new CanonicalDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            foreach (var document in batch)
                await action(document);
            if (batch.Count < batchSize)
                return;
            afterScope = batch[^1].Scope;
            afterId = batch[^1].Id;
        }
    }

    private static ExecutableProjectedColumnRoute[] SelectBackfillColumns(
        ExecutableStorageRoute route,
        BackfillCanonicalJsonOperation operation,
        ExecutableStorageObjectRole target) =>
        route.ProjectedColumns.Where(column =>
                column.Target == target &&
                (operation.SubjectKind != CanonicalJsonBackfillSubjectKind.ProjectedColumn ||
                 column.Definition.LogicalName == operation.SubjectIdentity))
            .ToArray();

    private async Task ValidateAsync(ValidatePhysicalSchemaOperation operation, DbTransaction transaction, CancellationToken ct)
    {
        foreach (var route in operation.Routes)
        {
            RelationalPhysicalStorageColumns.Validate(route);
            if (!await TableExistsAsync(route.PrimaryStorage.Name.Identifier, transaction, ct))
                throw new InvalidOperationException($"Physical primary storage '{route.PrimaryStorage.Name.Identifier}' is missing.");
            await ValidatePrimaryStorageAsync(route, transaction, ct);
            if (route.LinkedIndexStorage is not null && !await TableExistsAsync(route.LinkedIndexStorage.Name.Identifier, transaction, ct))
                throw new InvalidOperationException($"Physical linked storage '{route.LinkedIndexStorage.Name.Identifier}' is missing.");
            if (route.LinkedIndexStorage is not null)
                await ValidateLinkedStorageAsync(route, transaction, ct);
            foreach (var column in route.ProjectedColumns)
            {
                var table = column.Target == ExecutableStorageObjectRole.PrimaryStorage
                    ? route.PrimaryStorage.Name.Identifier
                    : route.LinkedIndexStorage!.Name.Identifier;
                if (!await ColumnExistsAsync(table, column.Column.Identifier, transaction, ct))
                    throw new InvalidOperationException($"Projected column '{column.Column.Identifier}' is missing from '{table}'.");
                await ValidateProjectedColumnAsync(table, column.Column.Identifier, column.Definition, transaction, ct);
            }
            foreach (var index in route.Indexes)
            {
                var table = index.Target == ExecutableStorageObjectRole.PrimaryStorage
                    ? route.PrimaryStorage.Name.Identifier
                    : route.LinkedIndexStorage!.Name.Identifier;
                await ValidateIndexAsync(index, table, transaction, ct);
            }
        }
    }

    private async Task ValidatePrimaryStorageAsync(
        ExecutableStorageRoute route,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var envelope = route.Envelope;
        var keyOrder = route.PrimaryKey.Columns
            .Select((column, index) => (column.Identifier, Order: index + 1))
            .ToDictionary(item => item.Identifier, item => item.Order, StringComparer.Ordinal);
        var expected = new[]
        {
            RequiredText(envelope.DocumentKind.Identifier, keyOrder),
            RequiredText(envelope.StorageScope.Identifier, keyOrder),
            RequiredText(envelope.Id.Identifier, keyOrder),
            RequiredText(envelope.SchemaVersion.Identifier, keyOrder),
            new ExpectedColumn(envelope.Version.Identifier, "INTEGER", true, null, keyOrder.GetValueOrDefault(envelope.Version.Identifier)),
            RequiredText(envelope.CanonicalJson.Identifier, keyOrder),
            RequiredText(RelationalPhysicalStorageColumns.CreatedUtc, keyOrder),
            RequiredText(RelationalPhysicalStorageColumns.UpdatedUtc, keyOrder)
        };
        await ValidateTableColumnsAsync(route.PrimaryStorage.Name.Identifier, expected, route.PrimaryKey.Columns, transaction, ct);
    }

    private async Task ValidateLinkedStorageAsync(
        ExecutableStorageRoute route,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        var key = route.AuxiliaryKey ?? throw new InvalidOperationException("Linked storage requires an auxiliary key.");
        var keyOrder = key.Columns
            .Select((column, index) => (column.Identifier, Order: index + 1))
            .ToDictionary(item => item.Identifier, item => item.Order, StringComparer.Ordinal);
        var expected = new[]
        {
            RequiredText(relationship.DocumentKind.Identifier, keyOrder),
            RequiredText(relationship.StorageScope.Identifier, keyOrder),
            RequiredText(relationship.DocumentId.Identifier, keyOrder)
        };
        await ValidateTableColumnsAsync(route.LinkedIndexStorage!.Name.Identifier, expected, key.Columns, transaction, ct);
    }

    private async Task ValidateTableColumnsAsync(
        string table,
        IReadOnlyList<ExpectedColumn> expected,
        IReadOnlyList<ExecutableColumnRoute> expectedPrimaryKey,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var actual = await ReadColumnsAsync(table, transaction, ct);
        foreach (var column in expected)
        {
            if (!actual.TryGetValue(column.Name, out var found))
                throw new InvalidOperationException($"Physical column '{table}.{column.Name}' is missing.");
            EnsureColumnCompatible(table, column, found);
            await ValidateColumnCollationAsync(table, column.Name, expectedCollation: null, transaction, ct);
        }

        var actualPrimaryKey = actual.Values.Where(column => column.PrimaryKeyOrder > 0)
            .OrderBy(column => column.PrimaryKeyOrder)
            .Select(column => column.Name)
            .ToArray();
        if (!actualPrimaryKey.SequenceEqual(expectedPrimaryKey.Select(column => column.Identifier), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Physical table '{table}' has primary key ({string.Join(", ", actualPrimaryKey)}) but requires ({string.Join(", ", expectedPrimaryKey.Select(column => column.Identifier))}).");
        }
    }

    private async Task ValidateProjectedColumnAsync(
        string table,
        string column,
        ProjectedColumnDefinition definition,
        DbTransaction transaction,
        CancellationToken ct)
    {
        SqlitePhysicalValueConverter.Validate(definition);
        var columns = await ReadColumnsAsync(table, transaction, ct);
        if (!columns.TryGetValue(column, out var found))
            throw new InvalidOperationException($"Projected column '{table}.{column}' is missing.");
        var expected = new ExpectedColumn(
            column,
            SqlType(definition.Type),
            !definition.IsNullable,
            SqlDefaultLiteral(definition),
            0);
        EnsureColumnCompatible(table, expected, found);
        await ValidateColumnCollationAsync(table, column, SqliteCollation(definition.Collation), transaction, ct);
    }

    private async Task ValidateIndexAsync(
        ExecutablePhysicalIndexRoute expected,
        string table,
        DbTransaction transaction,
        CancellationToken ct)
    {
        bool? isUnique = null;
        var isPartial = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"PRAGMA index_list({Q(table)});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!string.Equals(reader.GetString(1), expected.Name.Identifier, StringComparison.Ordinal))
                    continue;
                isUnique = reader.GetInt64(2) != 0;
                isPartial = reader.GetInt64(4) != 0;
                break;
            }
        }
        if (isUnique is null)
            throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' is missing from '{table}'.");
        if (isUnique != expected.IsUnique || isPartial)
        {
            throw new InvalidOperationException(
                $"Physical index '{expected.Name.Identifier}' has incompatible uniqueness or partial-index semantics.");
        }

        var actualColumns = new List<ActualIndexColumn>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"PRAGMA index_xinfo({Q(expected.Name.Identifier)});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetInt64(5) == 0)
                    continue;
                actualColumns.Add(new ActualIndexColumn(
                    reader.GetString(2),
                    reader.GetInt64(3) != 0 ? PhysicalSortDirection.Descending : PhysicalSortDirection.Ascending,
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        if (actualColumns.Count != expected.Columns.Count)
            throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' has an incompatible column count.");
        for (var index = 0; index < expected.Columns.Count; index++)
        {
            var desired = expected.Columns[index];
            var actual = actualColumns[index];
            var columnCollation = await ReadColumnCollationAsync(table, desired.Column.Identifier, transaction, ct) ?? "BINARY";
            if (actual.Name != desired.Column.Identifier ||
                actual.Direction != desired.Direction ||
                !string.Equals(actual.Collation ?? "BINARY", columnCollation, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Physical index '{expected.Name.Identifier}' column {index} does not match the compiled route.");
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, ActualColumn>> ReadColumnsAsync(
        string table,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var columns = new Dictionary<string, ActualColumn>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"PRAGMA table_info({Q(table)});";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(reader.GetString(1), new ActualColumn(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                checked((int)reader.GetInt64(5))));
        }
        return columns;
    }

    private async Task<string> ReadCreateSqlAsync(
        string type,
        string name,
        DbTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = @type AND name = @name;";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException($"Physical {type} '{name}' is missing its creation SQL.");
    }

    private async Task<IReadOnlyList<string>> ReadIndexCreationSqlAsync(
        string table,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = @table AND sql IS NOT NULL ORDER BY name;";
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    private async Task ValidateColumnCollationAsync(
        string table,
        string column,
        string? expectedCollation,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var actualCollation = await ReadColumnCollationAsync(table, column, transaction, ct);
        if (!string.Equals(actualCollation, expectedCollation, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Physical column '{table}.{column}' uses collation '{actualCollation ?? "<default>"}' but requires '{expectedCollation ?? "<default>"}'.");
        }
    }

    private async Task<string?> ReadColumnCollationAsync(
        string table,
        string column,
        DbTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", table);
        var sql = await command.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException($"Physical table '{table}' is missing.");
        var declaration = SqliteCreateTableSql.ExtractColumnDeclaration(sql, column);
        const string marker = "COLLATE";
        var markerIndex = SqliteCreateTableSql.FindKeyword(declaration, marker);
        return markerIndex < 0
            ? null
            : SqliteCreateTableSql.ReadIdentifier(declaration[(markerIndex + marker.Length)..]);
    }

    private static void EnsureColumnCompatible(string table, ExpectedColumn expected, ActualColumn actual)
    {
        if (!string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase) ||
            actual.IsNotNull != expected.IsNotNull ||
            actual.PrimaryKeyOrder != expected.PrimaryKeyOrder ||
            !string.Equals(NormalizeDefault(actual.DefaultSql), NormalizeDefault(expected.DefaultSql), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Physical column '{table}.{expected.Name}' is incompatible with the compiled route " +
                $"(type '{actual.Type}', nullable '{!actual.IsNotNull}', default '{actual.DefaultSql ?? "<none>"}', key order '{actual.PrimaryKeyOrder}').");
        }
    }

    private static ExpectedColumn RequiredText(string name, IReadOnlyDictionary<string, int> keyOrder) =>
        new(name, "TEXT", true, null, keyOrder.GetValueOrDefault(name));

    private async Task EnsureInfrastructureAsync(CancellationToken ct)
    {
        await ExecuteAsync($$"""
            CREATE TABLE IF NOT EXISTS groundwork_physical_schema_operations (
                manifest_id TEXT NOT NULL,
                provider_name TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                operation_fingerprint TEXT NOT NULL,
                applied_utc TEXT NOT NULL,
                PRIMARY KEY (manifest_id, provider_name, operation_id)
            );
            CREATE TABLE IF NOT EXISTS groundwork_physical_schema_state (
                manifest_id TEXT NOT NULL,
                provider_name TEXT NOT NULL,
                target_fingerprint TEXT NOT NULL,
                applied_state_json TEXT NOT NULL,
                PRIMARY KEY (manifest_id, provider_name)
            );
            CREATE TABLE IF NOT EXISTS {{RelationalPhysicalStorageColumns.MutationOperationsTable}} (
                manifest_id TEXT NOT NULL,
                provider_name TEXT NOT NULL,
                completed_provider_version TEXT NOT NULL,
                storage_unit TEXT NOT NULL,
                storage_scope TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL,
                affected_count INTEGER NOT NULL,
                completed_utc TEXT NOT NULL,
                PRIMARY KEY (
                    manifest_id,
                    provider_name,
                    storage_unit,
                    storage_scope,
                    operation_id)
            );
            """, null, ct);
    }

    private static async Task<PhysicalSchemaInspectionResult> ReadAndValidateInspectedHistoryAsync(
        SqlitePhysicalSchemaExecutor inspector,
        SqliteConnection inspection,
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken)
    {
        await using (var exists = inspection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'groundwork_physical_schema_state';";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
                return new PhysicalSchemaInspectionResult(PhysicalSchemaHistoryState.Empty, IsAppliedSchemaValid: true);
        }

        await using var command = inspection.CreateCommand();
        command.CommandText = """
            SELECT applied_state_json
            FROM groundwork_physical_schema_state
            WHERE manifest_id = @manifestId AND provider_name = @providerName;
            """;
        command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("@providerName", target.Provider.Name);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        var history = json is null
            ? PhysicalSchemaHistoryState.Empty
            : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
        var isAppliedSchemaValid = true;
        if (history.AppliedState is { } appliedState)
        {
            try
            {
                await using var transaction = await inspection.BeginTransactionAsync(cancellationToken);
                await inspector.ValidateAsync(
                    ValidatePhysicalSchemaOperation.ForAppliedState(appliedState),
                    transaction,
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                isAppliedSchemaValid = false;
            }
        }
        return new PhysicalSchemaInspectionResult(history, isAppliedSchemaValid);
    }

    private async Task<(string Fingerprint, DateTimeOffset AppliedAt)?> ReadOperationAsync(
        PhysicalSchemaTargetIdentity target,
        string identity,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_fingerprint, applied_utc
            FROM groundwork_physical_schema_operations
            WHERE manifest_id = @manifestId AND provider_name = @providerName AND operation_id = @identity;
            """;
        command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("@providerName", target.ProviderName);
        command.Parameters.AddWithValue("@identity", identity);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)))
            : null;
    }

    private async Task<bool> IsOperationPublishedAsync(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        DbTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT applied_state_json
            FROM groundwork_physical_schema_state
            WHERE manifest_id = @manifestId AND provider_name = @providerName;
            """;
        command.Parameters.AddWithValue("@manifestId", target.ManifestIdentity.Value);
        command.Parameters.AddWithValue("@providerName", target.ProviderName);
        var json = await command.ExecuteScalarAsync(ct) as string;
        if (json is null)
            return false;
        var state = PhysicalSchemaAppliedStateSerializer.Deserialize(json);
        return state.Snapshot.SemanticOperations.Any(applied =>
            applied.Identity == operation.Identity &&
            applied.Fingerprint == operation.Fingerprint);
    }

    private async Task<string?> ReadTargetFingerprintAsync(
        string manifestId,
        string providerName,
        DbTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT target_fingerprint FROM groundwork_physical_schema_state WHERE manifest_id = @manifestId AND provider_name = @providerName;";
        command.Parameters.AddWithValue("@manifestId", manifestId);
        command.Parameters.AddWithValue("@providerName", providerName);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private Task<bool> TableExistsAsync(string table, DbTransaction transaction, CancellationToken ct) =>
        ExistsAsync("table", table, transaction, ct);

    private Task<bool> ColumnExistsAsync(string table, string column, DbTransaction transaction, CancellationToken ct) =>
        ReadColumnExistsAsync(table, column, transaction, ct);

    private async Task<bool> ReadColumnExistsAsync(string table, string column, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"PRAGMA table_info({Q(table)});";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private async Task<bool> ExistsAsync(string type, string name, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = @type AND name = @name;";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
    }

    private async Task ExecuteAsync(string sql, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<FileStream?> AcquireFileLockAsync(PhysicalSchemaTargetIdentity target, CancellationToken ct)
    {
        var dataSource = connection.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
            return null;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToString())))[..16].ToLowerInvariant();
        var lockPath = $"{Path.GetFullPath(dataSource)}.groundwork-{fingerprint}.schema.lock";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            }
        }
    }

    private async Task<T> WithConnectionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await connectionGate.WaitAsync(ct);
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(ct);
            return await action(ct);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private Task WithConnectionAsync(Func<CancellationToken, Task> action, CancellationToken ct) =>
        WithConnectionAsync(async token => { await action(token); return true; }, ct);

    private static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    private static string ProjectedColumnSql(string column, ProjectedColumnDefinition definition)
    {
        var collation = SqliteCollation(definition.Collation);
        return string.Join(" ", new[]
        {
            Q(column),
            SqlType(definition.Type),
            collation is null ? null : $"COLLATE {Q(collation)}",
            definition.IsNullable ? "NULL" : "NOT NULL",
            SqlDefaultLiteral(definition) is { } value ? $"DEFAULT {value}" : null
        }.Where(part => part is not null));
    }

    private static string? SqliteCollation(string? collation) => collation?.Trim() switch
    {
        null or "" => null,
        var value when value.Equals("ordinal", StringComparison.OrdinalIgnoreCase) => "BINARY",
        var value when value.Equals("binary", StringComparison.OrdinalIgnoreCase) => "BINARY",
        var value when value.Equals("nocase", StringComparison.OrdinalIgnoreCase) => "NOCASE",
        var value when value.Equals("rtrim", StringComparison.OrdinalIgnoreCase) => "RTRIM",
        var value => value
    };

    private static string? SqlDefaultLiteral(ProjectedColumnDefinition definition)
    {
        if (definition.DefaultValue is null)
            return null;
        var value = definition.DefaultValue;
        return definition.Type switch
        {
            PortablePhysicalType.String or PortablePhysicalType.Guid or PortablePhysicalType.Json => SqlLiteral(value),
            PortablePhysicalType.Int32 or PortablePhysicalType.Int64 =>
                Convert.ToString(
                    RelationalPhysicalProjectionValues.ConvertScalar(value, definition.Type),
                    CultureInfo.InvariantCulture),
            PortablePhysicalType.Decimal or PortablePhysicalType.DateTime =>
                SqlitePhysicalValueConverter.DefaultLiteral(definition).ToString(CultureInfo.InvariantCulture),
            PortablePhysicalType.Boolean => bool.Parse(value) ? "1" : "0",
            PortablePhysicalType.Binary => $"X'{Convert.ToHexString(Convert.FromBase64String(value))}'",
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Type, null)
        };
    }

    private static string SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string? NormalizeDefault(string? value) => value?.Trim();
    private static string SqlType(PortablePhysicalType type) => type switch
    {
        PortablePhysicalType.Int32 or PortablePhysicalType.Int64 or PortablePhysicalType.Decimal or
            PortablePhysicalType.Boolean or PortablePhysicalType.DateTime => "INTEGER",
        PortablePhysicalType.Binary => "BLOB",
        _ => "TEXT"
    };

    private static void RequireApplicationLock(
        IPhysicalSchemaApplicationLock applicationLock,
        PhysicalSchemaTargetIdentity expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(applicationLock);
        if (applicationLock is not ApplicationLock sqliteLock ||
            sqliteLock.Target != expectedTarget ||
            !sqliteLock.IsOwned)
        {
            throw new InvalidOperationException(
                $"SQLite physical schema execution requires its active application lock for target '{expectedTarget}'.");
        }
    }

    private sealed class ApplicationLock(PhysicalSchemaTargetIdentity target, Action release) : IPhysicalSchemaApplicationLock
    {
        private int disposed;
        public PhysicalSchemaTargetIdentity Target { get; } = target;
        public CancellationToken OwnershipLost => CancellationToken.None;
        public bool IsOwned => Volatile.Read(ref disposed) == 0;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record CanonicalDocument(string Scope, string Id, string CanonicalJson);
    private sealed record ExpectedColumn(string Name, string Type, bool IsNotNull, string? DefaultSql, int PrimaryKeyOrder);
    private sealed record ActualColumn(string Name, string Type, bool IsNotNull, string? DefaultSql, int PrimaryKeyOrder);
    private sealed record ActualIndexColumn(string Name, PhysicalSortDirection Direction, string? Collation);
}
