using Keel.Import;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await ImportCli.RunAsync(args, Console.Out, Console.Error, null, cts.Token);
