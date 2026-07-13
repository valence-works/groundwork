namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = BenchmarkCommandLine.Parse(args, FindRepositoryRoot(Environment.CurrentDirectory));
            if (command.ShowHelp)
            {
                Console.WriteLine(BenchmarkCommandLine.Help);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            var result = await new BenchmarkRunner(Console.WriteLine).RunAsync(command.Request!, cancellation.Token);
            Console.WriteLine($"Benchmark artifacts: {result.RunDirectory}");
            return result.ConfirmedRegression ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark run cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Groundwork.slnx was not found in the current directory hierarchy.");
    }
}
