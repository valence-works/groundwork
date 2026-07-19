using Groundwork.Core.Validation;

namespace Groundwork.Core.PhysicalStorage;

/// <summary>Provider-neutral validation for values derived into physical projections.</summary>
public static class PhysicalProjectionValueValidation
{
    /// <summary>
    /// Ensures that a derived string value fits the projection's declared portable length before a
    /// provider can persist it. This keeps live writes and canonical-JSON backfills equivalent.
    /// </summary>
    public static void ValidateStringLength(string value, ProjectedColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Length is not { } maximum || value.Length <= maximum)
            return;

        throw new PhysicalProjectionValueValidationException(
            GroundworkDiagnostic.Error(
                "GW-PHYSICAL-037",
                $"Projected string column '{definition.LogicalName}' exceeds its declared maximum length of {maximum}.",
                $"projectedColumns.{definition.LogicalName}"));
    }
}

/// <summary>Structured portable projection-value validation failure.</summary>
public sealed class PhysicalProjectionValueValidationException(GroundworkDiagnostic diagnostic)
    : InvalidOperationException(CreateMessage(diagnostic))
{
    public GroundworkDiagnostic Diagnostic { get; } = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));

    private static string CreateMessage(GroundworkDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return $"{diagnostic.Code}: {diagnostic.Message}";
    }
}
