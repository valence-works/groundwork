namespace Groundwork.DiagnosticRecords;

public readonly record struct DiagnosticStorageScope(string TenantId, string ScopeId);

public readonly record struct DiagnosticStreamId(string Value);

public readonly record struct DiagnosticCursor(string Value);

public readonly record struct DiagnosticOperationId(DateTimeOffset IssuedAt, string Nonce);

public sealed record DiagnosticRecordInput(
    string RecordId,
    DateTimeOffset OccurredAt,
    string Payload,
    IReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>>? Fields = null);

public sealed record DiagnosticRecordBatch(
    DiagnosticStorageScope Scope,
    DiagnosticStreamId Stream,
    DiagnosticOperationId OperationId,
    DiagnosticRequestFingerprint RequestFingerprint,
    IReadOnlyList<DiagnosticRecordInput> Records)
{
    public static DiagnosticRecordBatch Create(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticOperationId operationId,
        IReadOnlyList<DiagnosticRecordInput> records)
    {
        var snapshot = DiagnosticRecordRequestSnapshot.CaptureRecords(records);
        return new(scope, stream, operationId, DiagnosticRequestFingerprint.ForAppend(scope, stream, snapshot), snapshot);
    }
}

public static class DiagnosticRecordRequestSnapshot
{
    public static DiagnosticRecordBatch Capture(DiagnosticRecordBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch with { Records = CaptureRecords(batch.Records) };
    }

    public static IReadOnlyList<DiagnosticRecordInput> CaptureRecords(IReadOnlyList<DiagnosticRecordInput> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Array.AsReadOnly(records.Select(record => record with { Fields = DiagnosticFieldCollectionSnapshot.Capture(record.Fields) }).ToArray());
    }
}

public sealed record DiagnosticRecord(
    string RecordId,
    DateTimeOffset OccurredAt,
    string Payload,
    DiagnosticCursor Cursor,
    IReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>>? Fields = null);

public static class DiagnosticRecordSnapshot
{
    public static IReadOnlyList<DiagnosticRecord> Capture(IReadOnlyList<DiagnosticRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Array.AsReadOnly(records.Select(record => record with
        {
            Fields = DiagnosticFieldCollectionSnapshot.Capture(record.Fields)
        }).ToArray());
    }
}

internal static class DiagnosticFieldCollectionSnapshot
{
    public static IReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>>? Capture(
        IReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>>? fields)
    {
        if (fields is null)
            return null;
        var snapshot = fields.ToDictionary(
            x => x.Key,
            x => x.Value is null
                ? null!
                : (IReadOnlyList<DiagnosticFieldValue>)Array.AsReadOnly(x.Value.ToArray()),
            StringComparer.Ordinal);
        return new DiagnosticFieldSnapshotDictionary(snapshot);
    }

    private sealed class DiagnosticFieldSnapshotDictionary(
        IDictionary<string, IReadOnlyList<DiagnosticFieldValue>> dictionary)
        : System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>>(dictionary)
    {
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(this, obj))
                return true;
            if (obj is not IReadOnlyDictionary<string, IReadOnlyList<DiagnosticFieldValue>> other || Count != other.Count)
                return false;
            return this.All(entry => other.TryGetValue(entry.Key, out var values) && entry.Value.SequenceEqual(values));
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var entry in this.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                hash.Add(entry.Key, StringComparer.Ordinal);
                foreach (var value in entry.Value)
                    hash.Add(value);
            }
            return hash.ToHashCode();
        }
    }
}

public enum DiagnosticAppendStatus
{
    Committed,
    Replayed
}

public enum DiagnosticSortDirection
{
    Ascending,
    Descending
}

public sealed record DiagnosticRecordOrder(
    string? Field = null,
    DiagnosticSortDirection Direction = DiagnosticSortDirection.Ascending)
{
    public static DiagnosticRecordOrder CursorAscending { get; } = new();
    public static DiagnosticRecordOrder CursorDescending { get; } = new(Direction: DiagnosticSortDirection.Descending);
}

public sealed record DiagnosticRecordQuery(
    DiagnosticStorageScope Scope,
    DiagnosticStreamId Stream,
    int Limit,
    DiagnosticRecordOrder? Order = null,
    bool IncludeExactCount = false,
    DiagnosticRecordContinuation? Continuation = null,
    DiagnosticRecordPredicate? Predicate = null,
    string? LatestPerKeyField = null);

public sealed record DiagnosticRecordContinuation(
    DiagnosticCursor SnapshotHighWater,
    DiagnosticCursor LastCursor,
    DiagnosticRequestFingerprint QueryFingerprint,
    DiagnosticFieldValue? LastOrderValue = null);

public sealed record DiagnosticRecordPage(
    IReadOnlyList<DiagnosticRecord> Records,
    DiagnosticRecordContinuation? Continuation,
    long? ExactCount);

public enum DiagnosticPredicateOperator
{
    Equal,
    In,
    RangeInclusive,
    Contains
}

public sealed record DiagnosticQueryHandlerCapabilities(
    IReadOnlySet<DiagnosticPredicateOperator> SupportedPredicates,
    bool SupportsCursorOrder,
    bool SupportsFieldOrder,
    bool SupportsSnapshotContinuation,
    bool SupportsExactCount,
    bool SupportsLatestPerKey);

public sealed record DiagnosticRecordStoreCapabilities(
    DiagnosticQueryHandlerCapabilities Query,
    DiagnosticGroupedQueryHandlerCapabilities GroupedQuery);

public enum DiagnosticOperationKind
{
    Append,
    Trim
}

public sealed class DiagnosticOperationConflictException : InvalidOperationException
{
    public DiagnosticOperationConflictException(
        DiagnosticOperationKind operationKind,
        DiagnosticOperationId operationId)
        : base($"{operationKind} operation '{operationId.Nonce}' was already used for a different request.")
    {
        OperationKind = operationKind;
        OperationId = operationId;
    }

    public DiagnosticOperationKind OperationKind { get; }
    public DiagnosticOperationId OperationId { get; }
}

public sealed class DiagnosticOperationExpiredException : InvalidOperationException
{
    public DiagnosticOperationExpiredException(
        DiagnosticOperationKind operationKind,
        DiagnosticOperationId operationId)
        : base($"{operationKind} operation '{operationId.Nonce}' is outside the declared idempotency window.")
    {
        OperationKind = operationKind;
        OperationId = operationId;
    }

    public DiagnosticOperationKind OperationKind { get; }
    public DiagnosticOperationId OperationId { get; }
}

public sealed class DiagnosticOperationClockSkewException : InvalidOperationException
{
    public DiagnosticOperationClockSkewException(
        DiagnosticOperationKind operationKind,
        DiagnosticOperationId operationId,
        TimeSpan maxClockSkew)
        : base($"{operationKind} operation '{operationId.Nonce}' was issued beyond the declared future clock-skew allowance of {maxClockSkew}.")
    {
        OperationKind = operationKind;
        OperationId = operationId;
        MaxClockSkew = maxClockSkew;
    }

    public DiagnosticOperationKind OperationKind { get; }
    public DiagnosticOperationId OperationId { get; }
    public TimeSpan MaxClockSkew { get; }
}

public sealed class DiagnosticAcknowledgementLostException : IOException
{
    public DiagnosticAcknowledgementLostException(
        DiagnosticOperationKind operationKind,
        DiagnosticStreamId stream,
        DiagnosticOperationId operationId,
        Exception? innerException = null)
        : base(
            $"The {operationKind.ToString().ToLowerInvariant()} operation for stream '{stream.Value}' may have committed before acknowledgement was lost.",
            innerException)
    {
        OperationKind = operationKind;
        Stream = stream;
        OperationId = operationId;
    }

    public DiagnosticOperationKind OperationKind { get; }
    public DiagnosticStreamId Stream { get; }
    public DiagnosticOperationId OperationId { get; }
}

public sealed class DiagnosticRequestFingerprintMismatchException : ArgumentException
{
    public DiagnosticRequestFingerprintMismatchException(DiagnosticOperationKind operationKind)
        : base($"The supplied {operationKind.ToString().ToLowerInvariant()} request fingerprint does not match the canonical request.")
    {
        OperationKind = operationKind;
    }

    public DiagnosticOperationKind OperationKind { get; }
}

public sealed record DiagnosticValidationError(string Code, string Message, string Path);

public sealed class DiagnosticRecordValidationException : ArgumentException
{
    public DiagnosticRecordValidationException(IReadOnlyList<DiagnosticValidationError> errors)
        : base("The diagnostic record request is invalid.")
    {
        Errors = errors;
    }

    public IReadOnlyList<DiagnosticValidationError> Errors { get; }
}

public static class DiagnosticRecordRequestValidator
{
    /// <summary>
    /// Validates append shape and canonical fingerprint without applying caller-time admission.
    /// Providers call this before consulting the durable operation ledger.
    /// </summary>
    public static void Validate(
        DiagnosticRecordBatch batch,
        DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<DiagnosticValidationError>();
        if (string.IsNullOrWhiteSpace(batch.Scope.TenantId))
            errors.Add(new("scope.tenant.required", "Tenant identity is required.", "scope.tenantId"));
        if (string.IsNullOrWhiteSpace(batch.Scope.ScopeId))
            errors.Add(new("scope.id.required", "Storage scope identity is required.", "scope.scopeId"));
        if (string.IsNullOrWhiteSpace(batch.Stream.Value))
            errors.Add(new("stream.required", "Stream identity is required.", "stream"));
        else if (batch.Stream != definition.Stream)
            errors.Add(new("stream.unknown", $"Stream '{batch.Stream.Value}' is not declared.", "stream"));
        if (string.IsNullOrWhiteSpace(batch.OperationId.Nonce))
            errors.Add(new("operation.nonce.required", "Operation nonce is required.", "operationId.nonce"));
        if (batch.Records is null || batch.Records.Count == 0)
            errors.Add(new("append.records.required", "An append batch must contain at least one record.", "records"));
        else
        {
            if (batch.Records.Count > definition.Limits.MaxBatchRecords)
                errors.Add(new("append.batch.too_large", $"The batch exceeds the declared maximum of {definition.Limits.MaxBatchRecords} records.", "records"));
            var duplicateIds = batch.Records
                .GroupBy(x => x.RecordId, StringComparer.Ordinal)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key);
            foreach (var id in duplicateIds)
                errors.Add(new("append.record_id.duplicate", $"Record id '{id}' occurs more than once in the batch.", "records"));

            var fields = definition.Fields.ToDictionary(x => x.Name, StringComparer.Ordinal);
            for (var index = 0; index < batch.Records.Count; index++)
            {
                var record = batch.Records[index];
                var path = $"records[{index}]";
                if (string.IsNullOrWhiteSpace(record.RecordId))
                    errors.Add(new("append.record_id.required", "Record identity is required.", $"{path}.recordId"));
                else if (System.Text.Encoding.UTF8.GetByteCount(record.RecordId) > definition.Limits.MaxRecordIdBytes)
                    errors.Add(new("append.record_id.too_large", "Record identity exceeds the declared byte bound.", $"{path}.recordId"));
                if (record.Payload is null)
                    errors.Add(new("append.payload.required", "Canonical JSON payload is required.", $"{path}.payload"));
                else if (System.Text.Encoding.UTF8.GetByteCount(record.Payload) > definition.Limits.MaxPayloadBytes)
                    errors.Add(new("append.payload.too_large", "Canonical JSON payload exceeds the declared byte bound.", $"{path}.payload"));
                else
                {
                    try
                    {
                        using var _ = System.Text.Json.JsonDocument.Parse(
                            record.Payload,
                            new System.Text.Json.JsonDocumentOptions { MaxDepth = definition.Limits.MaxJsonDepth });
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        errors.Add(new("append.payload.invalid_json", "Payload must be well-formed canonical JSON within the declared depth bound.", $"{path}.payload"));
                    }
                }

                var suppliedFields = record.Fields ?? new Dictionary<string, IReadOnlyList<DiagnosticFieldValue>>();
                if (suppliedFields.Count > definition.Limits.MaxFieldsPerRecord)
                    errors.Add(new("append.fields.too_many", "The record has more fields than the stream permits.", $"{path}.fields"));
                foreach (var required in definition.Fields.Where(x => x.IsRequired && !suppliedFields.ContainsKey(x.Name)))
                    errors.Add(new("append.field.required", $"Required field '{required.Name}' is missing.", $"{path}.fields.{required.Name}"));
                foreach (var supplied in suppliedFields)
                {
                    if (!fields.TryGetValue(supplied.Key, out var field))
                    {
                        errors.Add(new("append.field.undeclared", $"Field '{supplied.Key}' is not declared by stream '{definition.Stream.Value}'.", $"{path}.fields.{supplied.Key}"));
                        continue;
                    }

                    if (supplied.Value is null || supplied.Value.Count == 0 || supplied.Value.Count > field.MaxValues)
                    {
                        errors.Add(new("append.field.cardinality", $"Field '{field.Name}' violates its declared cardinality.", $"{path}.fields.{field.Name}"));
                        continue;
                    }

                    foreach (var value in supplied.Value)
                    {
                        if (!value.IsInitialized)
                            errors.Add(new("append.field.value_invalid", $"Field '{field.Name}' contains an uninitialized value.", $"{path}.fields.{field.Name}"));
                        else if (value.Type != field.Type)
                            errors.Add(new("append.field.type", $"Field '{field.Name}' requires {field.Type} values.", $"{path}.fields.{field.Name}"));
                        else if (field.Type == DiagnosticFieldType.String &&
                                 field.CasePolicy == DiagnosticStringCasePolicy.AsciiIgnoreCase &&
                                 !DiagnosticStringComparisonKey.IsAsciiIgnoreCaseValue(value.CanonicalValue))
                            errors.Add(new(
                                "append.field.case_domain",
                                $"Field '{field.Name}' uses {DiagnosticStringComparisonKey.AsciiIgnoreCaseAlgorithmId} and accepts only U+0020 through U+007E.",
                                $"{path}.fields.{field.Name}"));
                        if (value.IsInitialized && field.Type == DiagnosticFieldType.String &&
                            System.Text.Encoding.UTF8.GetByteCount(value.CanonicalValue) > field.MaxStringBytes)
                            errors.Add(new("append.field.string_too_large", $"Field '{field.Name}' exceeds its declared byte bound.", $"{path}.fields.{field.Name}"));
                    }
                }
            }
        }

        DiagnosticStringProjectionBudget.AddAppendError(batch, errors);

        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
        if (batch.RequestFingerprint != DiagnosticRequestFingerprint.ForAppend(batch.Scope, batch.Stream, batch.Records!))
            throw new DiagnosticRequestFingerprintMismatchException(DiagnosticOperationKind.Append);
    }

    /// <summary>
    /// Validates caller-time admission only after a validated append request has no unexpired ledger entry.
    /// Durable replay expiry is based on the provider-recorded commit time, not <see cref="DiagnosticOperationId.IssuedAt"/>.
    /// </summary>
    public static void ValidateNewOperationAdmission(
        DiagnosticRecordBatch batch,
        DiagnosticRecordStreamDefinition definition,
        DateTimeOffset providerClockHighWater)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(definition);
        ValidateOperationWindow(
            DiagnosticOperationKind.Append,
            batch.OperationId,
            definition.AppendIdempotencyWindow,
            definition.MaxOperationClockSkew,
            providerClockHighWater);
    }

    public static void Validate(
        DiagnosticStreamInspectionRequest request,
        DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(definition);
        var errors = ValidateScopeAndStream(request.Scope, request.Stream, definition, "inspect");
        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
    }

    public static void Validate(
        DiagnosticTrimRequest request,
        DiagnosticRecordStreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(definition);
        var errors = ValidateScopeAndStream(request.Scope, request.Stream, definition, "trim");
        if (string.IsNullOrWhiteSpace(request.OperationId.Nonce))
            errors.Add(new("trim.operation.required", "Trim operation nonce is required.", "operationId.nonce"));
        if (request.KeepNewest < 0)
            errors.Add(new("trim.keep_newest.invalid", "KeepNewest cannot be negative.", "keepNewest"));
        if (errors.Count > 0)
            throw new DiagnosticRecordValidationException(errors);
        if (request.RequestFingerprint != DiagnosticRequestFingerprint.ForTrim(request.Scope, request.Stream, request.KeepNewest))
            throw new DiagnosticRequestFingerprintMismatchException(DiagnosticOperationKind.Trim);
    }

    /// <summary>
    /// Validates caller-time admission only after a validated trim request has no unexpired ledger entry.
    /// Durable replay expiry is based on the provider-recorded commit time, not <see cref="DiagnosticOperationId.IssuedAt"/>.
    /// </summary>
    public static void ValidateNewOperationAdmission(
        DiagnosticTrimRequest request,
        DiagnosticRecordStreamDefinition definition,
        DateTimeOffset providerClockHighWater)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(definition);
        ValidateOperationWindow(
            DiagnosticOperationKind.Trim,
            request.OperationId,
            definition.TrimIdempotencyWindow,
            definition.MaxOperationClockSkew,
            providerClockHighWater);
    }

    private static List<DiagnosticValidationError> ValidateScopeAndStream(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticRecordStreamDefinition definition,
        string operation)
    {
        var errors = new List<DiagnosticValidationError>();
        if (string.IsNullOrWhiteSpace(scope.TenantId) || string.IsNullOrWhiteSpace(scope.ScopeId))
            errors.Add(new($"{operation}.scope.required", "An explicit tenant and storage scope are required.", "scope"));
        if (stream != definition.Stream)
            errors.Add(new($"{operation}.stream.unknown", $"Stream '{stream.Value}' is not declared.", "stream"));
        return errors;
    }

    private static void ValidateOperationWindow(
        DiagnosticOperationKind kind,
        DiagnosticOperationId operationId,
        TimeSpan window,
        TimeSpan maxClockSkew,
        DateTimeOffset providerClockHighWater)
    {
        if (operationId.IssuedAt > providerClockHighWater + maxClockSkew)
            throw new DiagnosticOperationClockSkewException(kind, operationId, maxClockSkew);
        if (operationId.IssuedAt < providerClockHighWater - window - maxClockSkew)
            throw new DiagnosticOperationExpiredException(kind, operationId);
    }
}

public sealed record DiagnosticAppendResult(
    DiagnosticAppendStatus Status,
    IReadOnlyList<DiagnosticRecord> Records,
    DiagnosticCursor CommittedCursorHighWater,
    DiagnosticFieldValue? LogicalHighWater);

public sealed record DiagnosticStreamInspectionRequest(
    DiagnosticStorageScope Scope,
    DiagnosticStreamId Stream);

public readonly record struct DiagnosticRecordCount(long Value);

public sealed record DiagnosticStreamStatistics(
    DiagnosticRecordCount RetainedCount,
    DiagnosticCursor? MaxRetainedCursor,
    DiagnosticCursor? LifetimeCommittedCursorHighWater,
    DiagnosticFieldValue? LifetimeLogicalHighWater);

public sealed record DiagnosticTrimRequest(
    DiagnosticStorageScope Scope,
    DiagnosticStreamId Stream,
    DiagnosticOperationId OperationId,
    DiagnosticRequestFingerprint RequestFingerprint,
    int KeepNewest)
{
    public static DiagnosticTrimRequest Create(
        DiagnosticStorageScope scope,
        DiagnosticStreamId stream,
        DiagnosticOperationId operationId,
        int keepNewest) =>
        new(scope, stream, operationId, DiagnosticRequestFingerprint.ForTrim(scope, stream, keepNewest), keepNewest);
}

public enum DiagnosticTrimStatus
{
    Completed,
    Replayed
}

public sealed record DiagnosticTrimResult(
    DiagnosticTrimStatus Status,
    DiagnosticRecordCount ExaminedCount,
    DiagnosticRecordCount DeletedCount,
    DiagnosticStreamStatistics Statistics);

public interface IDiagnosticAppendHandler
{
    ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticQueryHandler
{
    DiagnosticQueryHandlerCapabilities Capabilities { get; }

    ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticInspectHandler
{
    ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticTrimHandler
{
    ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DiagnosticRecordStoreHandlers(
    IDiagnosticAppendHandler Append,
    IDiagnosticQueryHandler Query,
    IDiagnosticInspectHandler Inspect,
    IDiagnosticTrimHandler Trim)
{
    /// <summary>
    /// A provider installs this only with a native grouped-reduction executor. The default rejects
    /// requests before any provider I/O, so ordinary query handlers can never accidentally group
    /// in the client.
    /// </summary>
    public IDiagnosticGroupedQueryHandler GroupedQuery { get; init; } = UnsupportedDiagnosticGroupedQueryHandler.Instance;

    public DiagnosticRecordStoreCapabilities Capabilities => new(Query.Capabilities, GroupedQuery.Capabilities);
}

public interface IDiagnosticRecordStore
{
    DiagnosticRecordStoreHandlers Handlers { get; }

    ValueTask<DiagnosticAppendResult> AppendAsync(
        DiagnosticRecordBatch batch,
        CancellationToken cancellationToken = default) =>
        Handlers.Append.AppendAsync(batch, cancellationToken);

    ValueTask<DiagnosticRecordPage> QueryAsync(
        DiagnosticRecordQuery query,
        CancellationToken cancellationToken = default) =>
        Handlers.Query.QueryAsync(query, cancellationToken);

    ValueTask<DiagnosticRecordGroupPage> QueryGroupsAsync(
        DiagnosticRecordGroupQuery query,
        CancellationToken cancellationToken = default) =>
        Handlers.GroupedQuery.QueryGroupsAsync(query, cancellationToken);

    ValueTask<DiagnosticStreamStatistics> InspectAsync(
        DiagnosticStreamInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        Handlers.Inspect.InspectAsync(request, cancellationToken);

    ValueTask<DiagnosticTrimResult> TrimAsync(
        DiagnosticTrimRequest request,
        CancellationToken cancellationToken = default) =>
        Handlers.Trim.TrimAsync(request, cancellationToken);
}
