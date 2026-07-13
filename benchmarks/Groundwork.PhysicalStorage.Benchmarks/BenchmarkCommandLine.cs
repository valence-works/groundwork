using Groundwork.Core.PhysicalStorage;

namespace Groundwork.PhysicalStorage.Benchmarks;

public sealed record BenchmarkCommand(bool ShowHelp, BenchmarkRunRequest? Request);

public static class BenchmarkCommandLine
{
    public const string Help = """
        Groundwork physical-storage benchmark harness

        Usage:
          dotnet run --project benchmarks/Groundwork.PhysicalStorage.Benchmarks -- run [options]

        Options:
          --profile smoke|scheduled       Fixed reproducibility profile (default: smoke)
          --providers <list>              sqlite,sqlserver,postgresql,mongodb or all
          --forms <list>                  shared,dedicated,entity or all
          --workloads <list>              Kebab-case workload names or all
          --output <directory>            Artifact run directory
          --baseline <run-or-jsonl>       Compare with a v1 run; raw JSONL is smoke-only
          --confirm-regression            Treat statistically confirmed regressions as exit code 2
          --no-containers                 Require external provider connection strings
          --help                          Show this help

        External provider variables:
          GROUNDWORK_BENCHMARK_SQLSERVER_CONNECTION_STRING
          GROUNDWORK_BENCHMARK_POSTGRESQL_CONNECTION_STRING
          GROUNDWORK_BENCHMARK_MONGODB_CONNECTION_STRING
        """;

    public static BenchmarkCommand Parse(IReadOnlyList<string> args, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (args.Count == 0 || args.Any(argument => argument is "--help" or "-h" or "help"))
            return new BenchmarkCommand(true, null);
        var index = args[0].Equals("run", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var profile = "smoke";
        string? providers = null;
        string? forms = null;
        string? workloads = null;
        string? output = null;
        string? baseline = null;
        var allowContainers = true;
        var confirmation = false;
        while (index < args.Count)
        {
            var argument = args[index++];
            switch (argument)
            {
                case "--profile":
                    profile = Value(args, ref index, argument);
                    break;
                case "--providers":
                    providers = Value(args, ref index, argument);
                    break;
                case "--forms":
                    forms = Value(args, ref index, argument);
                    break;
                case "--workloads":
                    workloads = Value(args, ref index, argument);
                    break;
                case "--output":
                    output = Value(args, ref index, argument);
                    break;
                case "--baseline":
                    baseline = Value(args, ref index, argument);
                    break;
                case "--no-containers":
                    allowContainers = false;
                    break;
                case "--confirm-regression":
                    confirmation = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown benchmark option '{argument}'.");
            }
        }

        var configuration = profile.ToLowerInvariant() switch
        {
            "smoke" => BenchmarkProfiles.Smoke,
            "scheduled" or "full" => BenchmarkProfiles.Scheduled,
            _ => throw new ArgumentException($"Unknown benchmark profile '{profile}'.")
        };
        configuration = configuration with
        {
            Providers = ParseProviders(providers, configuration.Providers),
            StorageForms = ParseForms(forms, configuration.StorageForms)
        };
        var selectedWorkloads = ParseEnumList(
            workloads,
            Enum.GetValues<BenchmarkWorkload>(),
            value => Normalize(value.ToString()));
        return new BenchmarkCommand(
            false,
            new BenchmarkRunRequest(
                Path.GetFullPath(repositoryRoot),
                configuration,
                selectedWorkloads,
                output,
                baseline,
                allowContainers,
                confirmation));
    }

    private static IReadOnlyList<BenchmarkProvider> ParseProviders(
        string? value,
        IReadOnlyList<BenchmarkProvider> defaults) =>
        ParseEnumList(value, defaults, provider => Normalize(provider.ToString()));

    private static IReadOnlyList<PhysicalStorageForm> ParseForms(
        string? value,
        IReadOnlyList<PhysicalStorageForm> defaults)
    {
        if (value is null)
            return defaults;
        var aliases = new Dictionary<string, PhysicalStorageForm>(StringComparer.OrdinalIgnoreCase)
        {
            ["shared"] = PhysicalStorageForm.SharedDocuments,
            ["shareddocuments"] = PhysicalStorageForm.SharedDocuments,
            ["dedicated"] = PhysicalStorageForm.DedicatedDocumentTable,
            ["dedicateddocumenttable"] = PhysicalStorageForm.DedicatedDocumentTable,
            ["entity"] = PhysicalStorageForm.PhysicalEntityTable,
            ["physicalentitytable"] = PhysicalStorageForm.PhysicalEntityTable
        };
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enum.GetValues<PhysicalStorageForm>();
        return Split(value).Select(item => aliases.TryGetValue(Normalize(item), out var form)
                ? form
                : throw new ArgumentException($"Unknown storage form '{item}'."))
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<T> ParseEnumList<T>(
        string? value,
        IReadOnlyList<T> defaults,
        Func<T, string> name) where T : struct, Enum
    {
        if (value is null)
            return defaults;
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enum.GetValues<T>();
        var values = Enum.GetValues<T>().ToDictionary(name, item => item, StringComparer.OrdinalIgnoreCase);
        return Split(value).Select(item => values.TryGetValue(Normalize(item), out var parsed)
                ? parsed
                : throw new ArgumentException($"Unknown {typeof(T).Name} value '{item}'."))
            .Distinct()
            .ToArray();
    }

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Value(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index >= args.Count)
            throw new ArgumentException($"Option '{option}' requires a value.");
        return args[index++];
    }
}
