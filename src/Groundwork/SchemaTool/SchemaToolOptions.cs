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
    bool AuthorizeDestructive,
    IReadOnlySet<string> AuthorizedSemanticMigrations)
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
        var semantic = new HashSet<string>(StringComparer.Ordinal);
        var destructive = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (option == "--authorize-destructive")
            {
                if (destructive)
                {
                    diagnostic = "Option '--authorize-destructive' was supplied more than once.";
                    return false;
                }
                destructive = true;
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
            if (option == "--authorize-semantic")
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
        options = new SchemaToolOptions(
            command,
            assembly,
            manifestType,
            provider,
            connection,
            connectionEnvironmentVariable,
            database,
            output,
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
        "--authorize-semantic"
    };

    private static bool TryCommand(string value, out SchemaToolCommand command) =>
        Enum.TryParse(value, ignoreCase: true, out command) &&
        string.Equals(value, command.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool TryOutput(string value, out SchemaToolOutput output) =>
        Enum.TryParse(value, ignoreCase: true, out output) &&
        string.Equals(value, output.ToString(), StringComparison.OrdinalIgnoreCase);
}
