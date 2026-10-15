using PlcMonitor.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var app = new CommandApp();
app.Configure(CliApp.Configure);

return await app.RunAsync(args, cts.Token);
