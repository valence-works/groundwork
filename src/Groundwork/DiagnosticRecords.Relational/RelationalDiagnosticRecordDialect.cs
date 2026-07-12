namespace Groundwork.DiagnosticRecords.Relational;

using System.Data.Common;

/// <summary>Native SQL differences used by the shared relational diagnostic-record kernel.</summary>
public abstract class RelationalDiagnosticRecordDialect
{
    public virtual int MaxParametersPerCommand => int.MaxValue;

    /// <summary>Gets whether the provider holds its stream lock across consecutive transactions.</summary>
    public virtual bool UsesSessionScopedStreamLock => false;

    public virtual string Parameter(string name) => $"@{name}";

    public abstract string ApplyLimit(string selectSql, string parameterName);

    public abstract string Contains(string expression, string parameterName);

    public abstract string BuildOperationCleanup(
        string table,
        string highWaterParameterName,
        string batchSizeParameterName);

    /// <summary>Atomically advances and returns the provider clock in its own short transaction.</summary>
    public virtual string BuildProviderClockAdvance(string table, string currentParameterName) =>
        $"UPDATE {table} SET clock_high_water_ticks = MAX(clock_high_water_ticks, {Parameter(currentParameterName)}) WHERE id = 1 RETURNING clock_high_water_ticks;";

    /// <summary>Acquires the provider's writer boundary for one diagnostic stream.</summary>
    public virtual string BuildStreamLock() => "SELECT 1;";

    /// <summary>Releases a session-scoped writer boundary acquired by <see cref="BuildStreamLock"/>.</summary>
    public virtual string BuildStreamUnlock() => throw new NotSupportedException();

    /// <summary>Rejects a provider unlock result that reports no lock was released.</summary>
    public virtual void ValidateStreamUnlockResult(object? result)
    {
    }

    /// <summary>
    /// Invalidates the provider pool containing a connection whose session lock could not be
    /// released, so that physical session cannot be leased again.
    /// </summary>
    public virtual void InvalidateConnectionPool(DbConnection connection) => throw new NotSupportedException();

    public virtual string TableReference(string table, string alias) => $"{table} {alias}";

    /// <summary>Applies provider-specific lexical quoting to generated portable SQL.</summary>
    public virtual string PrepareCommandText(string commandText) => commandText;

    public virtual void ConfigureParameter(DbParameter parameter, object value)
    {
    }

    public virtual string BuildCountFromPage(string pageSql)
    {
        const string marker = "SELECT * FROM selected";
        var selectIndex = pageSql.LastIndexOf(marker, StringComparison.Ordinal);
        if (selectIndex < 0)
            throw new InvalidOperationException("The relational diagnostic page query has no final selected projection.");
        return $"{pageSql[..selectIndex]}SELECT COUNT(*) FROM selected;";
    }

}

internal enum RelationalDiagnosticRecordExecutionPoint
{
    AppendBeforeCommit,
    AppendBeforeStreamLock,
    AppendAfterRecordStagedBeforeCommit,
    AppendAfterCommitBeforeAcknowledgement,
    TrimBeforeCommit,
    TrimAfterRecordDeletedBeforeCommit,
    TrimAfterCommitBeforeAcknowledgement
}

internal sealed record RelationalDiagnosticCommand(
    string CommandText,
    IReadOnlyDictionary<string, object> Parameters);
