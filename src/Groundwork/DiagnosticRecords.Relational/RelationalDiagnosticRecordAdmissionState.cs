using System.Data.Common;

namespace Groundwork.DiagnosticRecords.Relational;

internal static class RelationalDiagnosticRecordAdmissionState
{
    public static async Task<bool> ValidateIfPresentAsync(
        Func<DbConnection> createConnection,
        DiagnosticRecordStreamDefinition definition,
        string provider,
        CancellationToken cancellationToken)
    {
        var state = DiagnosticRecordPhysicalSchemaState.Capture(definition);
        await using var connection = createConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT definition_fingerprint, algorithm_manifest_fingerprint FROM {RelationalDiagnosticRecordSchema.DefinitionsTable} WHERE stream_id = @stream;";
        var stream = command.CreateParameter();
        stream.ParameterName = "@stream";
        stream.Value = definition.Stream.Value;
        command.Parameters.Add(stream);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return false;
        if (StringComparer.Ordinal.Equals(reader.GetString(0), state.DefinitionFingerprint) &&
            StringComparer.Ordinal.Equals(reader.GetString(1), state.ComparisonAlgorithmManifestFingerprint))
            return true;
        throw new InvalidOperationException(
            $"{provider} diagnostic stream '{definition.Stream.Value}' has an incompatible persisted definition or comparison-key algorithm state.");
    }
}
