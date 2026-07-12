namespace Groundwork.DiagnosticRecords.Relational;

/// <summary>Native SQL differences used by the shared relational diagnostic-record kernel.</summary>
public abstract class RelationalDiagnosticRecordDialect
{
    public virtual string Parameter(string name) => $"@{name}";

    public abstract string ApplyLimit(string selectSql, string parameterName);

    public abstract string Contains(string expression, string parameterName);

    public abstract string BuildOperationCleanup(
        string table,
        string highWaterParameterName,
        string batchSizeParameterName);

    public virtual string TableReference(string table, string alias) => $"{table} {alias}";

}

internal enum RelationalDiagnosticRecordExecutionPoint
{
    AppendBeforeCommit,
    AppendAfterRecordStagedBeforeCommit,
    AppendAfterCommitBeforeAcknowledgement,
    TrimBeforeCommit,
    TrimAfterRecordDeletedBeforeCommit,
    TrimAfterCommitBeforeAcknowledgement
}

internal sealed record RelationalDiagnosticCommand(
    string CommandText,
    IReadOnlyDictionary<string, object> Parameters);
