namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("worker", StringComparison.OrdinalIgnoreCase))
            return await RunWorkerAsync(args);
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
            var result = await new BenchmarkSubprocessCoordinator(Console.WriteLine)
                .RunAsync(command.Request!, cancellation.Token);
            Console.WriteLine($"Benchmark artifact group: {result.RunDirectory}");
            Console.WriteLine($"Independent workers: {result.WorkerCount}");
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

    private static async Task<int> RunWorkerAsync(IReadOnlyList<string> args)
    {
        string? requestPath = null;
        string? responsePath = null;
        for (var index = 1; index < args.Count; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Count)
                return 1;
            switch (option)
            {
                case "--request":
                    requestPath = args[++index];
                    break;
                case "--response":
                    responsePath = args[++index];
                    break;
                default:
                    return 1;
            }
        }
        if (requestPath is null || responsePath is null)
            return 1;
        var invocation = await BenchmarkSubprocessCoordinator.ReadAsync<BenchmarkWorkerInvocation>(
            requestPath,
            CancellationToken.None);
        return await BenchmarkSubprocessCoordinator.RunWorkerAsync(
            invocation,
            responsePath,
            CancellationToken.None,
            BenchmarkSubprocessCoordinator.DigestFile(requestPath));
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
