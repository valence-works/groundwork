using Groundwork.SchemaTool;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler handler = (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += handler;
try
{
    return await GroundworkSchemaCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= handler;
}
