using ApiTester.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };   // Ctrl+C cancels the request, not the process mid-write

return CliApp.Run(args, Console.Out, Console.Error, Console.OpenStandardOutput(),
                  new CliServices { Cancel = cts.Token });
