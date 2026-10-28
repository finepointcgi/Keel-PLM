namespace Keel.VaultTool;

// An explicit entry point (not top-level statements): a generated `Program` type would collide with Keel.Api's in the test assemblies that reference both.
internal static class EntryPoint
{
    private static Task<int> Main(string[] args) =>
        VaultCli.RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, CancellationToken.None);
}
