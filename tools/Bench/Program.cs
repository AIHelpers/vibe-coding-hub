using AiCodeAgent.Bench;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first Ctrl+C stops after the current attempt is cut short; what finished is still reported.
    e.Cancel = true;
    cancel.Cancel();
};
return await BenchApp.RunAsync(args, agentOverride: null, Console.Out, Console.Error, cancel.Token);
