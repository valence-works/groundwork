namespace Groundwork.Core.Validation;

public sealed record ManifestValidationResult(IReadOnlyList<GroundworkDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(diagnostic => !diagnostic.IsError);

    public IReadOnlyList<GroundworkDiagnostic> Errors => Diagnostics.Where(diagnostic => diagnostic.IsError).ToList();

    public ManifestValidationResult RequireValid()
    {
        if (IsValid)
            return this;

        throw new InvalidOperationException(
            $"Cannot compile an invalid storage manifest: {string.Join("; ", Errors.Select(error =>
                $"{error.Code}: {error.Message}"))}");
    }

    public static ManifestValidationResult Success { get; } = new([]);
}
