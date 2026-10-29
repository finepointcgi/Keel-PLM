using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.CadAgent;

// keel-cad: reference command-line connector for Keel PLM (docs/integration/cad-connector.md).
namespace Keel.CadAgent
{
    internal static class AgentMain
    {
        private static Task<int> Main(string[] args) => Cli.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
    }

    internal static class Cli
    {
        private const string Usage = """
            keel-cad: reference CAD connector for Keel PLM

              keel-cad checkout <top-document> --dir <folder> [--roles model,drawing,derived-pdf,step] [--allow-partial]
              keel-cad add <file> --dir <folder> --name <name> --role <role> [--type CAD|Drawing] [--references D-000001,D-000002]
              keel-cad push --dir <folder>               upload changed files to the working copy
              keel-cad checkin --dir <folder> [--comment text] [--skip-unchanged] [--refs-file refs.json]
              keel-cad undo --dir <folder>
              keel-cad watch --dir <folder> [--interval seconds]
              keel-cad bom push <file> --title <text> (--markings-from <document> | --markings-file <json>) [--format step|csv|xml|json] [--root <number>] [--apply-removals]

            Connection: --server <url> or KEEL_CAD_URL; token: --token, --token-file or KEEL_CAD_TOKEN (a JWT bearer token of your Keel identity provider).
            Exit codes: 0 done, 1 usage, 2 refused by the server (the error code is printed), 3 integrity check failed, 4 server not reachable.
            """;

        public static async Task<int> RunAsync(string[] args, TextWriter @out, TextWriter err, CancellationToken ct, HttpClient? client = null)
        {
            try
            {
                if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { await @out.WriteLineAsync(Usage); return args.Length == 0 ? 1 : 0; }
                var (positional, options) = Parse(args);
                var server = Opt(options, "server") ?? Environment.GetEnvironmentVariable("KEEL_CAD_URL");
                var token = Opt(options, "token") ?? (Opt(options, "token-file") is { } tf ? (await File.ReadAllTextAsync(tf, ct)).Trim() : null) ?? Environment.GetEnvironmentVariable("KEEL_CAD_TOKEN");
                if (client is null)
                {
                    if (string.IsNullOrWhiteSpace(server)) { await err.WriteLineAsync("A server is required (--server or KEEL_CAD_URL)."); return 1; }
                    client = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromHours(2) };
                }
                if (string.IsNullOrWhiteSpace(token) && client.DefaultRequestHeaders.Authorization is null) { await err.WriteLineAsync("A token is required (--token, --token-file or KEEL_CAD_TOKEN)."); return 1; }
                if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var agent = new CadConnector(client, line => @out.WriteLine(line));
                string Dir() => Opt(options, "dir") ?? throw new UsageException("--dir is required.");
                switch (positional[0])
                {
                    case "checkout":
                        await agent.CheckOutAsync(positional.ElementAtOrDefault(1) ?? throw new UsageException("The top document number is required."), Dir(),
                            Opt(options, "roles")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), options.ContainsKey("allow-partial"), ct);
                        return 0;
                    case "add":
                        var m = await agent.AddAsync(Dir(), positional.ElementAtOrDefault(1) ?? throw new UsageException("The file is required."), Opt(options, "name") ?? throw new UsageException("--name is required."),
                            Opt(options, "type") ?? "CAD", Opt(options, "role") ?? throw new UsageException("--role is required."),
                            Opt(options, "references")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [], ct);
                        await @out.WriteLineAsync($"Added {m.Number}.");
                        return 0;
                    case "push":
                        var pushed = await agent.PushChangesAsync(Dir(), ct);
                        await @out.WriteLineAsync($"Uploaded {pushed.Count} file(s).");
                        return 0;
                    case "checkin":
                        IReadOnlyDictionary<string, string[]>? refs = Opt(options, "refs-file") is { } rf ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(await File.ReadAllTextAsync(rf, ct)) : null;
                        await agent.CheckInAsync(Dir(), Opt(options, "comment"), options.ContainsKey("skip-unchanged"), refs, ct);
                        return 0;
                    case "undo":
                        await agent.UndoAsync(Dir(), ct);
                        return 0;
                    case "watch":
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                            await agent.WatchAsync(Dir(), TimeSpan.FromSeconds(double.Parse(Opt(options, "interval") ?? "5", System.Globalization.CultureInfo.InvariantCulture)), cts.Token);
                        }
                        return 0;
                    case "bom" when positional.ElementAtOrDefault(1) == "push":
                        var file = positional.ElementAtOrDefault(2) ?? throw new UsageException("The structure file is required.");
                        var format = Opt(options, "format") ?? CadConnector.FormatFromExtension(file) ?? throw new UsageException("--format is required (step, csv, xml or json).");
                        var markings = Opt(options, "markings-file") is { } mf ? JsonNode.Parse(await File.ReadAllTextAsync(mf, ct))!.AsObject()
                            : Opt(options, "markings-from") is { } doc ? await agent.MarkingsOfDocumentAsync(doc, ct)
                            : throw new UsageException("--markings-from <document> or --markings-file <json> is required: a proposal is marked like the data it comes from.");
                        var proposal = await agent.PushBomAsync(file, format, Opt(options, "title") ?? throw new UsageException("--title is required."), markings, Opt(options, "root"), options.ContainsKey("apply-removals"), ct);
                        await @out.WriteLineAsync($"Created proposal {proposal["number"]}; review it in Keel under CAD structures.");
                        return 0;
                    default:
                        throw new UsageException($"Unknown command '{positional[0]}'.");
                }
            }
            catch (UsageException e) { await err.WriteLineAsync(e.Message); await err.WriteLineAsync("Run 'keel-cad help' for usage."); return 1; }
            catch (AgentException e)
            {
                await err.WriteLineAsync(e.Code is null ? e.Message : $"{e.Message} [{e.Code}]");
                return e.Code == "cad.integrity_mismatch" ? 3 : e.Status == 0 ? 1 : 2;
            }
            catch (HttpRequestException e) { await err.WriteLineAsync("The server could not be reached: " + e.Message); return 4; }
        }

        private sealed class UsageException(string m) : Exception(m);

        private static string? Opt(Dictionary<string, string?> o, string name) => o.TryGetValue(name, out var v) ? v : null;

        private static (List<string> Positional, Dictionary<string, string?> Options) Parse(string[] args)
        {
            var positional = new List<string>();
            var options = new Dictionary<string, string?>(StringComparer.Ordinal);
            string[] flags = ["allow-partial", "skip-unchanged", "apply-removals"];
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(args[i]); continue; }
                var name = args[i][2..];
                if (flags.Contains(name)) options[name] = null;
                else options[name] = i + 1 < args.Length ? args[++i] : throw new UsageException($"--{name} needs a value.");
            }
            if (positional.Count == 0) throw new UsageException("A command is required.");
            return (positional, options);
        }
    }
}
