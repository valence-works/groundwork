using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Provider.Relational;
using Groundwork.Relational.Documents;
using Groundwork.Relational.Physicalization;

namespace Groundwork.Relational.PhysicalStorage;

/// <summary>
/// Shared server-relational executor for the physical-schema protocol. Provider dialects own DDL,
/// metadata inspection, advisory locks, and native value adaptation; operation ordering, durable
/// acknowledgements, CAS state, backfill, and exact compatibility checks remain common.
/// </summary>
public class RelationalServerPhysicalSchemaExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
{
    private const string BootstrapLockResource = "groundwork:physical:bootstrap";
    private readonly Func<DbConnection> createLockConnection;
    private readonly RelationalServerPhysicalSchemaDialect dialect;
    private readonly Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperationEvidence;
    private readonly Func<PhysicalSchemaAppliedState, CancellationToken, Task>? beforeAppliedStateFence;

    public RelationalServerPhysicalSchemaExecutor(
        Func<DbConnection> createLockConnection,
        RelationalServerPhysicalSchemaDialect dialect)
        : this(createLockConnection, dialect, null, null)
    {
    }

    protected RelationalServerPhysicalSchemaExecutor(
        Func<DbConnection> createLockConnection,
        RelationalServerPhysicalSchemaDialect dialect,
        Func<PhysicalSchemaOperation, CancellationToken, Task>? beforeOperationEvidence,
        Func<PhysicalSchemaAppliedState, CancellationToken, Task>? beforeAppliedStateFence)
    {
        this.createLockConnection = createLockConnection ?? throw new ArgumentNullException(nameof(createLockConnection));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        this.beforeOperationEvidence = beforeOperationEvidence;
        this.beforeAppliedStateFence = beforeAppliedStateFence;
    }

    public async ValueTask<IPhysicalSchemaApplicationLock> AcquireApplicationLockAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connection = createLockConnection()
            ?? throw new InvalidOperationException("The physical-schema lock connection factory returned null.");
        string? acquiredResource = null;
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);
            await dialect.AcquireApplicationLockAsync(connection, BootstrapLockResource, cancellationToken);
            try
            {
                await dialect.EnsureInfrastructureAsync(connection, cancellationToken);
            }
            catch (Exception exception)
            {
                try
                {
                    await dialect.ReleaseApplicationLockAsync(connection, BootstrapLockResource, CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    RelationalCleanupFailures.Attach(exception, cleanupFailure);
                }
                throw;
            }
            await dialect.ReleaseApplicationLockAsync(connection, BootstrapLockResource, CancellationToken.None);
            var resource = LockResource(target);
            await dialect.AcquireApplicationLockAsync(connection, resource, cancellationToken);
            acquiredResource = resource;
            var owner = Guid.NewGuid().ToString("N");
            var fence = await dialect.AcquireFenceAsync(connection, target, owner, cancellationToken);
            var sessionId = await dialect.ReadServerSessionIdAsync(connection, cancellationToken);
            return new ApplicationLock(target, connection, resource, owner, fence, sessionId, dialect);
        }
        catch (Exception exception)
        {
            if (acquiredResource is not null && connection.State == ConnectionState.Open)
            {
                try
                {
                    await dialect.ReleaseApplicationLockAsync(connection, acquiredResource, CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    RelationalCleanupFailures.Attach(exception, cleanupFailure);
                }
            }
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                RelationalCleanupFailures.Attach(exception, cleanupFailure);
            }
            if (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
            {
                throw new OperationCanceledException(
                    "Physical-schema application-lock acquisition was canceled.",
                    exception,
                    cancellationToken);
            }
            throw;
        }
    }

    public async ValueTask<PhysicalSchemaHistoryState> ReadHistoryAsync(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken) =>
        await RequireApplicationLock(applicationLock, target).ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var lease = RequireApplicationLock(applicationLock, target);
            await dialect.AssertFenceAsync(connection, transaction, target, lease.Owner, lease.Fence, ct);
            await using var command = Command(connection, transaction, """
                SELECT applied_state_json
                FROM groundwork_physical_schema_state
                WHERE manifest_id = @manifestId AND provider_name = @providerName;
                """);
            Add(command, "manifestId", target.ManifestIdentity.Value);
            Add(command, "providerName", target.ProviderName);
            var json = await command.ExecuteScalarAsync(ct) as string;
            await transaction.CommitAsync(ct);
            return json is null
                ? PhysicalSchemaHistoryState.Empty
                : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
        }, cancellationToken);

    public async ValueTask<PhysicalSchemaHistoryState> InspectHistoryAsync(
        PhysicalSchemaTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var connection = createLockConnection()
            ?? throw new InvalidOperationException("The physical-schema inspection connection factory returned null.");
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await dialect.TableExistsAsync(
                connection,
                transaction,
                "groundwork_physical_schema_state",
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return PhysicalSchemaHistoryState.Empty;
        }

        await using var command = Command(connection, transaction, """
            SELECT applied_state_json
            FROM groundwork_physical_schema_state
            WHERE manifest_id = @manifestId AND provider_name = @providerName;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.Provider.Name);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        var history = json is null
            ? PhysicalSchemaHistoryState.Empty
            : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
        if (history.AppliedState?.TargetFingerprint == target.Fingerprint)
        {
            await ValidateAsync(
                connection,
                transaction,
                ValidatePhysicalSchemaOperation.ForTarget(target),
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return history;
    }

    public async ValueTask<PhysicalSchemaOperationAcknowledgement> ApplyOperationAsync(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken) =>
        await RequireApplicationLock(applicationLock, target).ExecuteAsync(async (connection, ct) =>
        {
            ArgumentNullException.ThrowIfNull(operation);
            var lease = RequireApplicationLock(applicationLock, target);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var prior = await ReadOperationAsync(connection, transaction, target, operation.Identity, ct);
            if (prior is not null)
            {
                if (!string.Equals(prior.Value.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                    throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, prior.Value.Fingerprint);
                if (operation is ValidatePhysicalSchemaOperation ||
                    operation is BackfillCanonicalJsonOperation &&
                    !await IsOperationPublishedAsync(connection, transaction, target, operation, ct))
                    await ApplyOperationCoreAsync(connection, transaction, operation, ct);
                await dialect.AssertFenceAsync(connection, transaction, target, lease.Owner, lease.Fence, ct);
                await transaction.CommitAsync(ct);
                return new PhysicalSchemaOperationAcknowledgement(operation.Identity, prior.Value.Fingerprint, prior.Value.AppliedAt);
            }

            await ApplyOperationCoreAsync(connection, transaction, operation, ct);
            if (beforeOperationEvidence is not null)
                await beforeOperationEvidence(operation, ct);
            await dialect.AssertFenceAsync(connection, transaction, target, lease.Owner, lease.Fence, ct);
            var appliedAt = DateTimeOffset.UtcNow;
            await using (var command = Command(connection, transaction, """
                INSERT INTO groundwork_physical_schema_operations
                    (manifest_id, provider_name, operation_id, operation_fingerprint, applied_utc)
                VALUES (@manifestId, @providerName, @identity, @fingerprint, @appliedUtc);
                """))
            {
                Add(command, "manifestId", target.ManifestIdentity.Value);
                Add(command, "providerName", target.ProviderName);
                Add(command, "identity", operation.Identity);
                Add(command, "fingerprint", operation.Fingerprint);
                Add(command, "appliedUtc", appliedAt);
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            var durable = await ReadOperationAsync(connection, null, target, operation.Identity, ct)
                ?? throw new InvalidOperationException($"Physical operation '{operation.Identity}' was not durably recorded.");
            if (!string.Equals(durable.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, durable.Fingerprint);
            return new PhysicalSchemaOperationAcknowledgement(operation.Identity, durable.Fingerprint, durable.AppliedAt);
        }, cancellationToken);

    public async ValueTask RecordAppliedStateAsync(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await RequireApplicationLock(
            applicationLock,
            new PhysicalSchemaTargetIdentity(state.ManifestIdentity, state.Provider.Name)).ExecuteAsync(async (connection, ct) =>
        {
            var lease = RequireApplicationLock(
                applicationLock,
                new PhysicalSchemaTargetIdentity(state.ManifestIdentity, state.Provider.Name));
            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (beforeAppliedStateFence is not null)
                await beforeAppliedStateFence(state, ct);
            await dialect.AssertFenceAsync(
                connection,
                transaction,
                new PhysicalSchemaTargetIdentity(state.ManifestIdentity, state.Provider.Name),
                lease.Owner,
                lease.Fence,
                ct);
            var current = await ReadTargetFingerprintAsync(connection, transaction, state.ManifestIdentity.Value, state.Provider.Name, ct);
            if (current == state.TargetFingerprint)
            {
                await transaction.CommitAsync(ct);
                return true;
            }
            if (!string.Equals(current, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Physical schema applied-state compare-and-swap failed. Expected '{expectedAppliedTargetFingerprint ?? "<empty>"}', found '{current ?? "<empty>"}'.");
            }

            var sql = current is null
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
            await using var command = Command(connection, transaction, sql);
            Add(command, "manifestId", state.ManifestIdentity.Value);
            Add(command, "providerName", state.Provider.Name);
            Add(command, "fingerprint", state.TargetFingerprint);
            Add(command, "json", PhysicalSchemaAppliedStateSerializer.Serialize(state));
            if (current is not null)
                Add(command, "expected", expectedAppliedTargetFingerprint!);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Physical schema applied-state compare-and-swap lost a concurrent update.");
            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task ApplyOperationCoreAsync(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaOperation operation,
        CancellationToken ct)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation create:
                await CreatePrimaryAsync(connection, transaction, create.Route, ct);
                break;
            case CreatePhysicalEntityStorageOperation create:
                await CreatePrimaryAsync(connection, transaction, create.Route, ct);
                break;
            case CreateLinkedStorageOperation create:
                await CreateLinkedAsync(connection, transaction, create.Route, ct);
                break;
            case AddProjectedColumnOperation add:
                ValidateRoute(add.Route);
                await AddColumnAsync(connection, transaction, add.Storage.Name.Identifier, add.Column, ct);
                break;
            case FinalizeProjectedColumnOperation finalize:
                ValidateRoute(finalize.Route);
                await FinalizeColumnAsync(connection, transaction, finalize.Storage.Name.Identifier, finalize.Column, ct);
                break;
            case CreatePhysicalIndexOperation create:
                ValidateRoute(create.Route);
                await CreateIndexAsync(connection, transaction, create.Route, create.Storage.Name.Identifier, create.Index, ct);
                break;
            case BackfillCanonicalJsonOperation backfill:
                if (backfill.Route is not null)
                    ValidateRoute(backfill.Route);
                await BackfillAsync(connection, transaction, backfill, ct);
                break;
            case ValidatePhysicalSchemaOperation validate:
                await ValidateAsync(connection, transaction, validate, ct);
                break;
            default:
                throw new InvalidOperationException($"{dialect.ProviderDisplayName} cannot execute physical schema operation '{operation.Kind}'.");
        }
    }

    private async Task CreatePrimaryAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        ValidateRoute(route);
        var envelope = route.Envelope;
        var identity = dialect.IdentityLayout(
            PrimaryIdentityColumns(route),
            route.PrimaryKey.Columns.Select(column => column.Identifier).ToArray());
        var columns = new[]
        {
            dialect.EnvelopeColumn(envelope.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            dialect.EnvelopeColumn(envelope.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            dialect.EnvelopeColumn(envelope.Id.Identifier, RelationalEnvelopeColumnKind.Id),
            dialect.EnvelopeColumn(envelope.SchemaVersion.Identifier, RelationalEnvelopeColumnKind.SchemaVersion),
            dialect.EnvelopeColumn(envelope.Version.Identifier, RelationalEnvelopeColumnKind.Version),
            dialect.EnvelopeColumn(envelope.CanonicalJson.Identifier, RelationalEnvelopeColumnKind.CanonicalJson),
            dialect.EnvelopeColumn(RelationalPhysicalStorageColumns.CreatedUtc, RelationalEnvelopeColumnKind.Timestamp),
            dialect.EnvelopeColumn(RelationalPhysicalStorageColumns.UpdatedUtc, RelationalEnvelopeColumnKind.Timestamp)
        }.Concat(identity.ProviderColumns.Select(column => column.Definition)).ToArray();
        if (!await dialect.TableExistsAsync(connection, transaction, route.PrimaryStorage.Name.Identifier, ct))
        {
            await ExecuteAsync(connection, transaction, dialect.CreateTableSql(
                route.PrimaryStorage.Name.Identifier,
                columns,
                identity.PrimaryKey), ct);
        }
        await ValidatePrimaryAsync(connection, transaction, route, ct);
    }

    private async Task CreateLinkedAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        ValidateRoute(route);
        var relationship = route.LinkedRelationship!;
        var key = route.AuxiliaryKey ?? throw new InvalidOperationException("Linked storage requires an auxiliary key.");
        var identity = dialect.IdentityLayout(
            LinkedIdentityColumns(route),
            key.Columns.Select(column => column.Identifier).ToArray());
        var columns = new[]
        {
            dialect.EnvelopeColumn(relationship.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            dialect.EnvelopeColumn(relationship.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            dialect.EnvelopeColumn(relationship.DocumentId.Identifier, RelationalEnvelopeColumnKind.Id)
        }.Concat(identity.ProviderColumns.Select(column => column.Definition)).ToArray();
        var table = route.LinkedIndexStorage!.Name.Identifier;
        if (!await dialect.TableExistsAsync(connection, transaction, table, ct))
            await ExecuteAsync(connection, transaction, dialect.CreateTableSql(table, columns, identity.PrimaryKey), ct);
        await ValidateLinkedAsync(connection, transaction, route, ct);
    }

    private async Task AddColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ExecutableProjectedColumnRoute column,
        CancellationToken ct)
    {
        dialect.Validate(column.Definition);
        var existing = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        var staged = column.Definition.IsNullable ? column.Definition : column.Definition with { IsNullable = true };
        if (!existing.ContainsKey(column.Column.Identifier))
            await ExecuteAsync(connection, transaction, dialect.AddColumnSql(table, column.Column.Identifier, staged), ct);
        await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, staged, ct);
    }

    private async Task FinalizeColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ExecutableProjectedColumnRoute column,
        CancellationToken ct)
    {
        if (column.Definition.IsNullable)
        {
            await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
            return;
        }
        var columns = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        if (!columns.TryGetValue(column.Column.Identifier, out var found))
            throw new InvalidOperationException($"Projected column '{table}.{column.Column.Identifier}' is missing.");
        if (!found.IsNullable)
        {
            await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
            return;
        }
        await using (var count = Command(connection, transaction,
                         $"SELECT COUNT(*) FROM {dialect.Q(table)} WHERE {dialect.Q(column.Column.Identifier)} IS NULL;"))
        {
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException($"Projected column '{table}.{column.Column.Identifier}' cannot be made required because canonical backfill left null values.");
        }
        await ExecuteAsync(connection, transaction, dialect.FinalizeColumnSql(table, column.Column.Identifier, column.Definition), ct);
        await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
    }

    private async Task CreateIndexAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        string table,
        ExecutablePhysicalIndexRoute index,
        CancellationToken ct)
    {
        var nullableColumns = NullableIndexColumns(route, index);
        var existing = await dialect.ReadIndexAsync(connection, transaction, table, index.Name.Identifier, ct);
        if (existing is null)
            await ExecuteAsync(connection, transaction, dialect.CreateIndexSql(table, index, nullableColumns), ct);
        await ValidateIndexAsync(connection, transaction, route, table, index, ct);
    }

    private async Task BackfillAsync(
        DbConnection connection,
        DbTransaction transaction,
        BackfillCanonicalJsonOperation operation,
        CancellationToken ct)
    {
        var route = operation.Route ?? throw new InvalidOperationException($"{dialect.ProviderDisplayName} physical backfill requires an executable route.");
        if (operation.Target == ExecutableStorageObjectRole.PrimaryStorage)
        {
            var selected = SelectBackfillColumns(route, operation, ExecutableStorageObjectRole.PrimaryStorage);
            await ForEachCanonicalDocumentBatchAsync(connection, transaction, route, ct, async document =>
            {
                if (selected.Length == 0)
                    return;
                var values = RelationalPhysicalProjectionValues.Read(document.CanonicalJson, selected);
                var assignments = string.Join(", ", selected.Select((column, index) => $"{dialect.Q(column.Column.Identifier)} = @v{index}"));
                await using var command = Command(connection, transaction,
                    $"UPDATE {dialect.Q(route.PrimaryStorage.Name.Identifier)} SET {assignments} WHERE " +
                    dialect.ExactIdentityPredicate(
                    [
                        new(route.Discriminator.Column.Identifier, null, "@kind"),
                        new(route.ScopeKey.Column.Identifier, null, "@scope"),
                        new(route.Envelope.Id.Identifier, null, "@id")
                    ]) + ";");
                for (var index = 0; index < selected.Length; index++)
                    Add(command, $"v{index}", dialect.ConvertStorageValue(values[selected[index].Definition.LogicalName], selected[index].Definition));
                Add(command, "kind", route.Discriminator.Value);
                Add(command, "scope", document.Scope);
                Add(command, "id", document.Id);
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException($"Canonical backfill lost document '{document.Id}' in scope '{document.Scope}'.");
            });
            return;
        }

        var relationship = route.LinkedRelationship!;
        var linked = route.ProjectedColumns.Where(column => column.Target == ExecutableStorageObjectRole.LinkedIndexStorage).ToArray();
        await ForEachCanonicalDocumentBatchAsync(connection, transaction, route, ct, async document =>
        {
            var values = RelationalPhysicalProjectionValues.Read(document.CanonicalJson, linked);
            var relationColumns = new[]
            {
                relationship.DocumentKind.Identifier,
                relationship.StorageScope.Identifier,
                relationship.DocumentId.Identifier
            };
            var insertColumns = relationColumns.Concat(linked.Select(column => column.Column.Identifier)).ToArray();
            await using var command = Command(connection, transaction, dialect.UpsertLinkedSql(
                route.LinkedIndexStorage!.Name.Identifier,
                insertColumns,
                route.AuxiliaryKey!.Columns.Select(column => column.Identifier).ToArray(),
                linked.Select(column => column.Column.Identifier).ToArray()));
            Add(command, "v0", route.Discriminator.Value);
            Add(command, "v1", document.Scope);
            Add(command, "v2", document.Id);
            for (var index = 0; index < linked.Length; index++)
                Add(command, $"v{index + 3}", dialect.ConvertStorageValue(values[linked[index].Definition.LogicalName], linked[index].Definition));
            try
            {
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (DbException exception) when (dialect.IsUniqueConstraintException(exception))
            {
                await ThrowIfIdentityHashCollisionAsync(
                    connection,
                    transaction,
                    route.LinkedIndexStorage.Name.Identifier,
                    [
                        (relationship.DocumentKind.Identifier, route.Discriminator.Value, "collisionKind"),
                        (relationship.StorageScope.Identifier, document.Scope, "collisionScope"),
                        (relationship.DocumentId.Identifier, document.Id, "collisionId")
                    ],
                    ct);
                throw;
            }
        });
    }

    private async Task ThrowIfIdentityHashCollisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<(string Column, string Value, string Parameter)> identity,
        CancellationToken ct)
    {
        var parts = identity.Select(item => new RelationalPhysicalIdentityPredicatePart(
            item.Column,
            null,
            $"@{item.Parameter}")).ToArray();
        var predicate = dialect.HashOnlyIdentityPredicate(parts);
        if (predicate is null)
            return;
        await using var command = Command(
            connection,
            transaction,
            $"SELECT {string.Join(", ", identity.Select(item => dialect.Q(item.Column)))} FROM {dialect.Q(table)} WHERE {predicate};");
        foreach (var item in identity)
            Add(command, item.Parameter, item.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return;
        var retained = Enumerable.Range(0, identity.Count).Select(reader.GetString).ToArray();
        if (!retained.SequenceEqual(identity.Select(item => item.Value), StringComparer.Ordinal))
            throw new PhysicalIdentityHashCollisionException(table, identity.Select(item => item.Column).ToArray());
    }

    private async Task ForEachCanonicalDocumentBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        CancellationToken ct,
        Func<CanonicalDocument, Task> action)
    {
        const int batchSize = 256;
        string? afterScope = null;
        string? afterId = null;
        while (true)
        {
            var batch = new List<CanonicalDocument>(batchSize);
            await using (var command = Command(connection, transaction, dialect.SelectCanonicalBatchSql(route, batchSize, afterScope is not null)))
            {
                Add(command, "kind", route.Discriminator.Value);
                if (afterScope is not null)
                {
                    Add(command, "afterScope", afterScope);
                    Add(command, "afterId", afterId!);
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

    private async Task ValidateAsync(DbConnection connection, DbTransaction transaction, ValidatePhysicalSchemaOperation operation, CancellationToken ct)
    {
        foreach (var route in operation.Routes)
        {
            ValidateRoute(route);
            await ValidatePrimaryAsync(connection, transaction, route, ct);
            if (route.LinkedIndexStorage is not null)
                await ValidateLinkedAsync(connection, transaction, route, ct);
            foreach (var column in route.ProjectedColumns)
            {
                var table = column.Target == ExecutableStorageObjectRole.PrimaryStorage
                    ? route.PrimaryStorage.Name.Identifier
                    : route.LinkedIndexStorage!.Name.Identifier;
                await ValidateProjectedColumnAsync(connection, transaction, table, column.Column.Identifier, column.Definition, ct);
            }
            foreach (var index in route.Indexes)
            {
                var table = index.Target == ExecutableStorageObjectRole.PrimaryStorage
                    ? route.PrimaryStorage.Name.Identifier
                    : route.LinkedIndexStorage!.Name.Identifier;
                await ValidateIndexAsync(connection, transaction, route, table, index, ct);
            }
        }
    }

    private Task ValidatePrimaryAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        var envelope = route.Envelope;
        var identity = dialect.IdentityLayout(
            PrimaryIdentityColumns(route),
            route.PrimaryKey.Columns.Select(column => column.Identifier).ToArray());
        return ValidateTableAsync(connection, transaction, route.PrimaryStorage.Name.Identifier,
        [
            Envelope(envelope.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            Envelope(envelope.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            Envelope(envelope.Id.Identifier, RelationalEnvelopeColumnKind.Id),
            Envelope(envelope.SchemaVersion.Identifier, RelationalEnvelopeColumnKind.SchemaVersion),
            Envelope(envelope.Version.Identifier, RelationalEnvelopeColumnKind.Version),
            Envelope(envelope.CanonicalJson.Identifier, RelationalEnvelopeColumnKind.CanonicalJson),
            Envelope(RelationalPhysicalStorageColumns.CreatedUtc, RelationalEnvelopeColumnKind.Timestamp),
            Envelope(RelationalPhysicalStorageColumns.UpdatedUtc, RelationalEnvelopeColumnKind.Timestamp),
            .. identity.ProviderColumns.Select(ProviderColumn)
        ], identity.PrimaryKey, ct);

        ExpectedColumn Envelope(string name, RelationalEnvelopeColumnKind kind) =>
            new(name, dialect.EnvelopeType(kind), false, null, dialect.EnvelopeCollation(kind));
        static ExpectedColumn ProviderColumn(RelationalProviderOwnedPhysicalColumn column) =>
            new(
                column.Name,
                column.Type,
                column.IsNullable,
                column.DefaultValue,
                column.Collation,
                column.IsComputed,
                column.IsPersisted,
                column.ComputedDefinition);
    }

    private Task ValidateLinkedAsync(DbConnection connection, DbTransaction transaction, ExecutableStorageRoute route, CancellationToken ct)
    {
        var relationship = route.LinkedRelationship!;
        var identity = dialect.IdentityLayout(
            LinkedIdentityColumns(route),
            route.AuxiliaryKey!.Columns.Select(column => column.Identifier).ToArray());
        return ValidateTableAsync(connection, transaction, route.LinkedIndexStorage!.Name.Identifier,
        [
            Envelope(relationship.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
            Envelope(relationship.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
            Envelope(relationship.DocumentId.Identifier, RelationalEnvelopeColumnKind.Id),
            .. identity.ProviderColumns.Select(ProviderColumn)
        ], identity.PrimaryKey, ct);

        ExpectedColumn Envelope(string name, RelationalEnvelopeColumnKind kind) =>
            new(name, dialect.EnvelopeType(kind), false, null, dialect.EnvelopeCollation(kind));
        static ExpectedColumn ProviderColumn(RelationalProviderOwnedPhysicalColumn column) =>
            new(
                column.Name,
                column.Type,
                column.IsNullable,
                column.DefaultValue,
                column.Collation,
                column.IsComputed,
                column.IsPersisted,
                column.ComputedDefinition);
    }

    private async Task ValidateTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<ExpectedColumn> expected,
        IReadOnlyList<string> primaryKey,
        CancellationToken ct)
    {
        var actual = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        foreach (var desired in expected)
            EnsureColumnCompatible(table, desired, actual.GetValueOrDefault(desired.Name));
        var actualKey = actual.Values.Where(column => column.PrimaryKeyOrder > 0)
            .OrderBy(column => column.PrimaryKeyOrder).Select(column => column.Name).ToArray();
        if (!actualKey.SequenceEqual(primaryKey, StringComparer.Ordinal))
            throw new InvalidOperationException($"Physical table '{table}' has primary key ({string.Join(", ", actualKey)}) but requires ({string.Join(", ", primaryKey)}).");
    }

    private async Task ValidateProjectedColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        ProjectedColumnDefinition definition,
        CancellationToken ct)
    {
        dialect.Validate(definition);
        var actual = await dialect.ReadColumnsAsync(connection, transaction, table, ct);
        EnsureColumnCompatible(table, new ExpectedColumn(
            column,
            dialect.ProjectedType(definition),
            definition.IsNullable,
            dialect.NormalizeDefault(definition),
            dialect.ProjectedCollation(definition)), actual.GetValueOrDefault(column));
    }

    private async Task ValidateIndexAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExecutableStorageRoute route,
        string table,
        ExecutablePhysicalIndexRoute expected,
        CancellationToken ct)
    {
        var actual = await dialect.ReadIndexAsync(connection, transaction, table, expected.Name.Identifier, ct)
            ?? throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' is missing from '{table}'.");
        var expectedFilter = dialect.IndexFilter(expected, NullableIndexColumns(route, expected));
        if (actual.IsUnique != expected.IsUnique || actual.Columns.Count != expected.Columns.Count ||
            !string.Equals(actual.Filter, expectedFilter, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' has incompatible uniqueness, filter, or column count.");
        for (var index = 0; index < expected.Columns.Count; index++)
        {
            if (actual.Columns[index].Name != expected.Columns[index].Column.Identifier ||
                actual.Columns[index].Direction != expected.Columns[index].Direction)
                throw new InvalidOperationException($"Physical index '{expected.Name.Identifier}' column {index} does not match the compiled route.");
        }
    }

    private void EnsureColumnCompatible(string table, ExpectedColumn expected, RelationalPhysicalColumnMetadata? actual)
    {
        if (actual is null)
            throw new InvalidOperationException($"Physical column '{table}.{expected.Name}' is missing.");
        if (!string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase) ||
            actual.IsNullable != expected.IsNullable ||
            !string.Equals(actual.DefaultValue, expected.DefaultValue, StringComparison.Ordinal) ||
            !string.Equals(
                dialect.NormalizeCollationIdentity(actual.Collation),
                dialect.NormalizeCollationIdentity(expected.Collation),
                StringComparison.OrdinalIgnoreCase) ||
            actual.IsComputed != expected.IsComputed ||
            actual.IsPersisted != expected.IsPersisted ||
            !string.Equals(
                dialect.NormalizeComputedDefinition(actual.ComputedDefinition),
                dialect.NormalizeComputedDefinition(expected.ComputedDefinition),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Physical column '{table}.{expected.Name}' is incompatible with the compiled route " +
                $"(type '{actual.Type}', nullable '{actual.IsNullable}', default '{actual.DefaultValue ?? "<none>"}', " +
                $"collation '{actual.Collation ?? "<default>"}', computed '{actual.IsComputed}', persisted '{actual.IsPersisted}', " +
                $"expression '{actual.ComputedDefinition ?? "<none>"}').");
        }
    }

    private async Task<(string Fingerprint, DateTimeOffset AppliedAt)?> ReadOperationAsync(
        DbConnection connection,
        DbTransaction? transaction,
        PhysicalSchemaTargetIdentity target,
        string identity,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT operation_fingerprint, applied_utc
            FROM groundwork_physical_schema_operations
            WHERE manifest_id = @manifestId AND provider_name = @providerName AND operation_id = @identity;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        Add(command, "identity", identity);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var applied = reader.GetValue(1) switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            var value => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
        };
        return (reader.GetString(0), applied);
    }

    private static async Task<bool> IsOperationPublishedAsync(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT applied_state_json
            FROM groundwork_physical_schema_state
            WHERE manifest_id = @manifestId AND provider_name = @providerName;
            """);
        Add(command, "manifestId", target.ManifestIdentity.Value);
        Add(command, "providerName", target.ProviderName);
        var json = await command.ExecuteScalarAsync(ct) as string;
        if (json is null)
            return false;
        var state = PhysicalSchemaAppliedStateSerializer.Deserialize(json);
        return state.Snapshot.SemanticOperations.Any(applied =>
            applied.Identity == operation.Identity &&
            applied.Fingerprint == operation.Fingerprint);
    }

    private static async Task<string?> ReadTargetFingerprintAsync(
        DbConnection connection,
        DbTransaction transaction,
        string manifestId,
        string providerName,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT target_fingerprint FROM groundwork_physical_schema_state WHERE manifest_id = @manifestId AND provider_name = @providerName;");
        Add(command, "manifestId", manifestId);
        Add(command, "providerName", providerName);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{name}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(ct);
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

    private void ValidateRoute(ExecutableStorageRoute route)
    {
        RelationalPhysicalStorageColumns.Validate(route);
        dialect.ValidateRoute(route);
    }

    private static RelationalPhysicalIdentityColumn[] PrimaryIdentityColumns(ExecutableStorageRoute route) =>
    [
        new(route.Envelope.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
        new(route.Envelope.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
        new(route.Envelope.Id.Identifier, RelationalEnvelopeColumnKind.Id)
    ];

    private static RelationalPhysicalIdentityColumn[] LinkedIdentityColumns(ExecutableStorageRoute route) =>
    [
        new(route.LinkedRelationship!.DocumentKind.Identifier, RelationalEnvelopeColumnKind.DocumentKind),
        new(route.LinkedRelationship.StorageScope.Identifier, RelationalEnvelopeColumnKind.StorageScope),
        new(route.LinkedRelationship.DocumentId.Identifier, RelationalEnvelopeColumnKind.Id)
    ];

    private static string[] NullableIndexColumns(ExecutableStorageRoute route, ExecutablePhysicalIndexRoute index)
    {
        var indexed = index.Columns.Select(column => column.Column.Identifier).ToHashSet(StringComparer.Ordinal);
        return route.ProjectedColumns
            .Where(column => column.Target == index.Target && column.Definition.IsNullable && indexed.Contains(column.Column.Identifier))
            .Select(column => column.Column.Identifier)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string LockResource(PhysicalSchemaTargetIdentity target)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(target.ToString()));
        return $"groundwork:physical:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    protected static long ReadLockSessionId(IPhysicalSchemaApplicationLock applicationLock) =>
        applicationLock is ApplicationLock relationalLock
            ? relationalLock.ServerSessionId
            : throw new ArgumentException("The lock was not created by a relational server physical-schema executor.", nameof(applicationLock));

    private static ApplicationLock RequireApplicationLock(
        IPhysicalSchemaApplicationLock applicationLock,
        PhysicalSchemaTargetIdentity expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(applicationLock);
        if (applicationLock is not ApplicationLock relationalLock ||
            relationalLock.Target != expectedTarget)
        {
            throw new InvalidOperationException(
                $"Relational physical schema execution requires its active application lock for target '{expectedTarget}'.");
        }
        return relationalLock;
    }

    private sealed class ApplicationLock : IPhysicalSchemaApplicationLock
    {
        private static readonly TimeSpan OwnershipVerificationTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        private readonly DbConnection connection;
        private readonly string resource;
        private readonly CancellationTokenSource heartbeatStop = new();
        private readonly RelationalServerPhysicalSchemaDialect dialect;
        private readonly SemaphoreSlim sessionGate = new(1, 1);
        private readonly CancellationTokenSource ownershipLost = new();
        private readonly Task heartbeat;
        private int disposed;

        public ApplicationLock(
            PhysicalSchemaTargetIdentity target,
            DbConnection connection,
            string resource,
            string owner,
            long fence,
            long serverSessionId,
            RelationalServerPhysicalSchemaDialect dialect)
        {
            Target = target;
            this.connection = connection;
            this.resource = resource;
            this.dialect = dialect;
            Owner = owner;
            Fence = fence;
            ServerSessionId = serverSessionId;
            connection.StateChange += OnConnectionStateChanged;
            heartbeat = HeartbeatAsync();
        }

        public PhysicalSchemaTargetIdentity Target { get; }
        public string Owner { get; }
        public long Fence { get; }
        public long ServerSessionId { get; }
        public CancellationToken OwnershipLost => ownershipLost.Token;

        public async Task<T> ExecuteAsync<T>(
            Func<DbConnection, CancellationToken, Task<T>> action,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            await sessionGate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref disposed) != 0 || ownershipLost.IsCancellationRequested ||
                    connection.State != ConnectionState.Open)
                {
                    MarkOwnershipLost();
                    throw OwnershipLostException();
                }

                try
                {
                    return await action(connection, cancellationToken);
                }
                catch (Exception exception) when (exception is DbException or InvalidOperationException)
                {
                    var verificationFailure = default(Exception);
                    var isOwned = false;
                    if (Volatile.Read(ref disposed) == 0 && !ownershipLost.IsCancellationRequested &&
                        connection.State == ConnectionState.Open)
                    {
                        using var verificationTimeout = new CancellationTokenSource(OwnershipVerificationTimeout);
                        try
                        {
                            isOwned = await dialect.VerifyApplicationLockAsync(
                                connection,
                                resource,
                                verificationTimeout.Token);
                        }
                        catch (Exception ownershipException) when (
                            ownershipException is DbException or InvalidOperationException or OperationCanceledException)
                        {
                            verificationFailure = ownershipException;
                        }
                    }

                    if (isOwned)
                        throw;
                    MarkOwnershipLost();
                    throw OwnershipLostException(exception, verificationFailure);
                }
            }
            finally
            {
                sessionGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            connection.StateChange -= OnConnectionStateChanged;
            await heartbeatStop.CancelAsync();
            try
            {
                try
                {
                    await heartbeat;
                }
                catch
                {
                    MarkOwnershipLost();
                }

                await sessionGate.WaitAsync();
                try
                {
                    if (connection.State == ConnectionState.Open)
                        await dialect.ReleaseApplicationLockAsync(connection, resource, CancellationToken.None);
                }
                catch
                {
                    MarkOwnershipLost();
                }
                finally
                {
                    sessionGate.Release();
                }
            }
            finally
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch
                {
                    MarkOwnershipLost();
                }
                heartbeatStop.Dispose();
                sessionGate.Dispose();
                ownershipLost.Dispose();
            }
        }

        private void OnConnectionStateChanged(object sender, StateChangeEventArgs args)
        {
            if (args.CurrentState != ConnectionState.Open && Volatile.Read(ref disposed) == 0)
                MarkOwnershipLost();
        }

        private async Task HeartbeatAsync()
        {
            try
            {
                while (!heartbeatStop.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatInterval, heartbeatStop.Token);
                    await sessionGate.WaitAsync(heartbeatStop.Token);
                    try
                    {
                        if (!await dialect.VerifyApplicationLockAsync(connection, resource, heartbeatStop.Token))
                        {
                            MarkOwnershipLost();
                            return;
                        }
                    }
                    finally
                    {
                        sessionGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested)
            {
            }
            catch
            {
                MarkOwnershipLost();
            }
        }

        private void MarkOwnershipLost()
        {
            if (Volatile.Read(ref disposed) == 0 && !ownershipLost.IsCancellationRequested)
                ownershipLost.Cancel();
        }

        private InvalidOperationException OwnershipLostException(
            Exception? executionFailure = null,
            Exception? verificationFailure = null)
        {
            Exception? inner = executionFailure;
            if (verificationFailure is not null)
                inner = new AggregateException(executionFailure!, verificationFailure);
            return new InvalidOperationException(
                $"The relational physical-schema lock session for target '{Target}' was lost during schema execution.",
                inner);
        }
    }

    private sealed record CanonicalDocument(string Scope, string Id, string CanonicalJson);
    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool IsNullable,
        string? DefaultValue,
        string? Collation,
        bool IsComputed = false,
        bool IsPersisted = false,
        string? ComputedDefinition = null);
}


public enum RelationalEnvelopeColumnKind
{
    DocumentKind,
    StorageScope,
    Id,
    SchemaVersion,
    Version,
    CanonicalJson,
    Timestamp
}

public sealed record RelationalPhysicalColumnMetadata(
    string Name,
    string Type,
    bool IsNullable,
    string? DefaultValue,
    string? Collation,
    int PrimaryKeyOrder,
    bool IsComputed = false,
    bool IsPersisted = false,
    string? ComputedDefinition = null);

public sealed record RelationalPhysicalIndexColumnMetadata(string Name, PhysicalSortDirection Direction);

public sealed record RelationalPhysicalIndexMetadata(
    bool IsUnique,
    IReadOnlyList<RelationalPhysicalIndexColumnMetadata> Columns,
    string? Filter);

/// <summary>A retained provider column that participates in a document identity.</summary>
public sealed record RelationalPhysicalIdentityColumn(string Name, RelationalEnvelopeColumnKind Kind);

/// <summary>A provider-owned column added behind the portable physical-storage interface.</summary>
public sealed record RelationalProviderOwnedPhysicalColumn(
    string Name,
    string Definition,
    string Type,
    bool IsNullable,
    string? DefaultValue = null,
    string? Collation = null,
    bool IsComputed = false,
    bool IsPersisted = false,
    string? ComputedDefinition = null);

/// <summary>Maps the retained logical identity to the provider's physical key representation.</summary>
public sealed record RelationalPhysicalIdentityLayout(
    IReadOnlyList<RelationalProviderOwnedPhysicalColumn> ProviderColumns,
    IReadOnlyList<string> PrimaryKey);

/// <summary>Provider-owned SQL and metadata behavior behind the shared physical-schema executor.</summary>
public abstract class RelationalServerPhysicalSchemaDialect
{
    protected sealed record InfrastructureColumn(
        string Name,
        string Type,
        bool IsNullable,
        string? Collation,
        int PrimaryKeyOrder = 0,
        bool IsComputed = false,
        bool IsPersisted = false,
        string? ComputedDefinition = null);

    public abstract string ProviderDisplayName { get; }
    public abstract string Q(string identifier);
    public abstract string EnvelopeType(RelationalEnvelopeColumnKind kind);
    public abstract string? EnvelopeCollation(RelationalEnvelopeColumnKind kind);
    public abstract string ProjectedType(ProjectedColumnDefinition definition);
    public abstract string? Collation(string? portableCollation);
    public virtual string? NormalizeCollationIdentity(string? collation) => collation;
    public virtual string? ProjectedCollation(ProjectedColumnDefinition definition) =>
        definition.Type is PortablePhysicalType.String or PortablePhysicalType.Json
            ? Collation(definition.Collation)
            : null;
    public abstract string? NormalizeDefault(ProjectedColumnDefinition definition);
    public virtual string? NormalizeComputedDefinition(string? definition) => definition?.Trim();
    public virtual bool IsProviderOwnedColumnCompatible(
        RelationalProviderOwnedPhysicalColumn expected,
        RelationalPhysicalColumnMetadata actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        return string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase) &&
               actual.IsNullable == expected.IsNullable &&
               actual.IsComputed == expected.IsComputed &&
               actual.IsPersisted == expected.IsPersisted &&
               string.Equals(
                   NormalizeComputedDefinition(actual.ComputedDefinition),
                   NormalizeComputedDefinition(expected.ComputedDefinition),
                   StringComparison.Ordinal);
    }
    public virtual void ValidateRoute(ExecutableStorageRoute route) => ArgumentNullException.ThrowIfNull(route);
    public virtual string ExactIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join(" AND ", parts.Select(part =>
        {
            var column = part.Alias is null ? Q(part.ColumnIdentifier) : $"{part.Alias}.{Q(part.ColumnIdentifier)}";
            return $"{column} = {part.ValueExpression}";
        }));
    }
    public virtual string? HashOnlyIdentityPredicate(IReadOnlyList<RelationalPhysicalIdentityPredicatePart> parts) => null;
    public virtual bool IsUniqueConstraintException(DbException exception) => false;
    public abstract string EnvelopeColumn(string name, RelationalEnvelopeColumnKind kind);
    public virtual RelationalPhysicalIdentityLayout IdentityLayout(
        IReadOnlyList<RelationalPhysicalIdentityColumn> identityColumns,
        IReadOnlyList<string> logicalPrimaryKey)
    {
        ArgumentNullException.ThrowIfNull(identityColumns);
        ArgumentNullException.ThrowIfNull(logicalPrimaryKey);
        var identityNames = identityColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        if (logicalPrimaryKey.Any(column => !identityNames.Contains(column)))
            throw new ArgumentException("Every logical primary-key column must be a retained identity column.", nameof(logicalPrimaryKey));
        return new RelationalPhysicalIdentityLayout([], Array.AsReadOnly(logicalPrimaryKey.ToArray()));
    }
    public abstract string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey);
    public abstract string AddColumnSql(string table, string column, ProjectedColumnDefinition definition);
    public abstract string FinalizeColumnSql(string table, string column, ProjectedColumnDefinition definition);
    public abstract string? IndexFilter(ExecutablePhysicalIndexRoute index, IReadOnlyList<string> nullableColumns);
    public abstract string CreateIndexSql(string table, ExecutablePhysicalIndexRoute index, IReadOnlyList<string> nullableColumns);
    public abstract string UpsertLinkedSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, IReadOnlyList<string> updateColumns);
    public abstract string SelectCanonicalBatchSql(ExecutableStorageRoute route, int batchSize, bool hasCursor);
    public abstract object? ConvertStorageValue(object? value, ProjectedColumnDefinition definition);
    public abstract void Validate(ProjectedColumnDefinition definition);
    public abstract Task AcquireApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken);
    public abstract Task ReleaseApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken);
    public abstract Task<bool> VerifyApplicationLockAsync(DbConnection connection, string resource, CancellationToken cancellationToken);
    public abstract Task<long> ReadServerSessionIdAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract Task<long> AcquireFenceAsync(
        DbConnection connection,
        PhysicalSchemaTargetIdentity target,
        string owner,
        CancellationToken cancellationToken);
    public abstract Task AssertFenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        string owner,
        long fence,
        CancellationToken cancellationToken);
    public abstract Task EnsureInfrastructureAsync(DbConnection connection, CancellationToken cancellationToken);
    public abstract Task<bool> TableExistsAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyDictionary<string, RelationalPhysicalColumnMetadata>> ReadColumnsAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken);
    public abstract Task<RelationalPhysicalIndexMetadata?> ReadIndexAsync(DbConnection connection, DbTransaction transaction, string table, string index, CancellationToken cancellationToken);

    protected async Task ValidateInfrastructureTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<InfrastructureColumn> expectedColumns,
        CancellationToken cancellationToken)
    {
        var actualColumns = await ReadColumnsAsync(connection, transaction, table, cancellationToken);
        if (actualColumns.Count != expectedColumns.Count ||
            expectedColumns.Any(expected => !actualColumns.ContainsKey(expected.Name)))
        {
            throw new InvalidOperationException(
                $"Physical-schema infrastructure table '{table}' does not contain the exact required column set.");
        }

        foreach (var expected in expectedColumns)
        {
            var actual = actualColumns[expected.Name];
            if (!string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase) ||
                actual.IsNullable != expected.IsNullable ||
                actual.DefaultValue is not null ||
                !string.Equals(
                    NormalizeCollationIdentity(actual.Collation),
                    NormalizeCollationIdentity(expected.Collation),
                    StringComparison.Ordinal) ||
                actual.PrimaryKeyOrder != expected.PrimaryKeyOrder ||
                actual.IsComputed != expected.IsComputed ||
                actual.IsPersisted != expected.IsPersisted ||
                !string.Equals(
                    NormalizeComputedDefinition(actual.ComputedDefinition),
                    NormalizeComputedDefinition(expected.ComputedDefinition),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Physical-schema infrastructure column '{table}.{expected.Name}' is incompatible " +
                    $"(type '{actual.Type}', nullable '{actual.IsNullable}', default '{actual.DefaultValue ?? "<none>"}', " +
                    $"collation '{actual.Collation ?? "<none>"}', primary-key order '{actual.PrimaryKeyOrder}', " +
                    $"computed '{actual.IsComputed}', persisted '{actual.IsPersisted}', " +
                    $"expression '{actual.ComputedDefinition ?? "<none>"}').");
            }
        }
    }
}
