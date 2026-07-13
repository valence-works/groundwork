namespace Groundwork.SchemaTool;

internal enum SchemaToolCommand
{
    Plan,
    Validate,
    Status,
    Apply
}

internal enum SchemaToolOutput
{
    Human,
    Json
}

internal sealed record SchemaToolOptions(
    SchemaToolCommand Command,
    string ManifestAssembly,
    string? ManifestType,
    string Provider,
    string? Connection,
    string? ConnectionEnvironmentVariable,
    string? Database,
    SchemaToolOutput Output,
    bool Offline,
    bool ApplySafe,
    string? ExpectedPlanFingerprint,
    IReadOnlySet<string> AllowedDestructiveOperations,
    IReadOnlySet<string> AllowedSemanticMigrations)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out SchemaToolOptions? options,
        out string diagnostic)
    {
        options = null;
        diagnostic = string.Empty;
        if (arguments.Count == 0 || !TryCommand(arguments[0], out var command))
        {
            diagnostic = "Expected one command: plan, validate, status, or apply.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var destructive = new HashSet<string>(StringComparer.Ordinal);
        var semantic = new HashSet<string>(StringComparer.Ordinal);
        var offline = false;
        var safe = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (option is "--offline" or "--safe")
            {
                if (option == "--offline" && offline || option == "--safe" && safe)
                {
                    diagnostic = $"Option '{option}' was supplied more than once.";
                    return false;
                }
                offline |= option == "--offline";
                safe |= option == "--safe";
                continue;
            }

            if (!ValueOptions.Contains(option) || index + 1 >= arguments.Count)
            {
                diagnostic = "An option is unknown or missing its value.";
                return false;
            }

            var value = arguments[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                diagnostic = $"Option '{option}' requires a non-empty value.";
                return false;
            }
            if (option == "--allow-destructive")
            {
                destructive.Add(value);
                continue;
            }
            if (option == "--allow-semantic")
            {
                semantic.Add(value);
                continue;
            }
            if (!values.TryAdd(option, value))
            {
                diagnostic = $"Option '{option}' was supplied more than once.";
                return false;
            }
        }

        if (!values.TryGetValue("--manifest-assembly", out var assembly) ||
            !values.TryGetValue("--provider", out var provider))
        {
            diagnostic = "Options '--manifest-assembly' and '--provider' are required.";
            return false;
        }

        values.TryGetValue("--connection", out var connection);
        values.TryGetValue("--connection-env", out var connectionEnvironmentVariable);
        if (connection is not null && connectionEnvironmentVariable is not null)
        {
            diagnostic = "Use either '--connection' or '--connection-env', not both.";
            return false;
        }

        var output = SchemaToolOutput.Human;
        if (values.TryGetValue("--output", out var outputValue) && !TryOutput(outputValue, out output))
        {
            diagnostic = "Option '--output' must be 'human' or 'json'.";
            return false;
        }

        values.TryGetValue("--manifest-type", out var manifestType);
        values.TryGetValue("--database", out var database);
        values.TryGetValue("--expected-plan", out var expectedPlanFingerprint);
        if (expectedPlanFingerprint is not null &&
            (expectedPlanFingerprint.Length != 64 || expectedPlanFingerprint.Any(character => !Uri.IsHexDigit(character))))
        {
            diagnostic = "Option '--expected-plan' must be a 64-character hexadecimal plan fingerprint.";
            return false;
        }
        if (offline && command != SchemaToolCommand.Validate)
        {
            diagnostic = "Option '--offline' is valid only for the validate command.";
            return false;
        }
        if (offline && (connection is not null || connectionEnvironmentVariable is not null || database is not null))
        {
            diagnostic = "Offline validation cannot accept connection input.";
            return false;
        }
        var hasBoundAuthorization = expectedPlanFingerprint is not null || destructive.Count != 0 || semantic.Count != 0;
        if (command == SchemaToolCommand.Apply)
        {
            if (safe == hasBoundAuthorization)
            {
                diagnostic = "Apply requires exactly one mode: '--safe' or '--expected-plan' with exact authorization.";
                return false;
            }
            if (hasBoundAuthorization && expectedPlanFingerprint is null)
            {
                diagnostic = "Exact authorization requires '--expected-plan'.";
                return false;
            }
        }
        else if (safe || hasBoundAuthorization)
        {
            diagnostic = "Apply mode and authorization options are valid only for the apply command.";
            return false;
        }
        options = new SchemaToolOptions(
            command,
            assembly,
            manifestType,
            provider,
            connection,
            connectionEnvironmentVariable,
            database,
            output,
            offline,
            safe,
            expectedPlanFingerprint,
            destructive,
            semantic);
        return true;
    }

    private static readonly IReadOnlySet<string> ValueOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "--manifest-assembly",
        "--manifest-type",
        "--provider",
        "--connection",
        "--connection-env",
        "--database",
        "--output",
        "--expected-plan",
        "--allow-destructive",
        "--allow-semantic"
    };

    private static bool TryCommand(string value, out SchemaToolCommand command) =>
        Enum.TryParse(value, ignoreCase: true, out command) &&
        string.Equals(value, command.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool TryOutput(string value, out SchemaToolOutput output) =>
        Enum.TryParse(value, ignoreCase: true, out output) &&
        string.Equals(value, output.ToString(), StringComparison.OrdinalIgnoreCase);
}
