using Keel.Import;

// An explicit entry point (not top-level statements) so the executable does not declare a type named Program: the integration tests also see the API host's Program.
internal static class ImportEntryPoint
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return await ImportCli.RunAsync(args, Console.Out, Console.Error, null, cts.Token);
    }
}
