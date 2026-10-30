using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Keel.Audit;
using Keel.Documents.Vault;
using Keel.Seed;
using Keel.TestSupport;
using Microsoft.Playwright;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Keel.E2E;

/// <summary>Marks a browser test. Skipped unless <c>KEEL_E2E=1</c>, so the ordinary <c>dotnet test</c> of every other project stays fast.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!E2EFixture.Enabled) Skip = "End-to-end tests are off: run scripts/e2e.sh or set KEEL_E2E=1 (needs Docker and a one-time Chromium download).";
    }
}

/// <summary>
/// The whole real stack with throwaway resources, independent of the dev stack: PostgreSQL, SeaweedFS (vault) and Keycloak (dev realm imported
/// with the redirect URI of this run) in Testcontainers on random host ports with tmpfs storage; migrations applied by <c>Keel.Api --migrate</c>;
/// the demo data seeded through the real services (<see cref="DemoSeeder"/>); the real <c>Keel.Api</c> host as a child process on a free port;
/// and one headless Chromium driven by Playwright. Everything is removed when the run ends.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("KEEL_E2E") == "1";

    public const string KeycloakImage = "quay.io/keycloak/keycloak:26.4";
    public const string VaultImage = "chrislusf/seaweedfs:4.47";
    /// <summary>Signatures need a login this fresh. The host runs with the minimum the product allows so the step-up round trip is testable.</summary>
    public const int StepUpSeconds = 30;

    private readonly List<IAsyncDisposable> _containers = [];
    private Process? _api;
    private Process? _lockApi;
    private Process? _scaleApi;
    private string _adminCs = "", _appCsMain = "", _migratorCsMain = "";
    private int _scalePort;
    private Func<int, Dictionary<string, string>>? _envFor;
    private string _apiBin = "";
    private IPlaywright? _playwright;
    private StreamWriter? _log;
    private string _workDir = "";

    public string RepoRoot { get; } = FindRepoRoot();
    public string ResultsDir => Path.Combine(RepoRoot, "TestResults", "e2e");
    public string BaseUrl { get; private set; } = "";
    /// <summary>
    /// A second host on the same database, vault and Keycloak with a short idle lock (12 s idle, 4 s warning, 24 s until a locked session ends),
    /// started on first use so that the other tests, which idle for longer than that while they wait, are never locked.
    /// </summary>
    public string LockHostUrl { get; private set; } = "";
    public const double LockIdleMinutes = 0.2;
    public const int LockWarningSeconds = 4;
    public const double LockTerminateMinutes = 0.4;
    /// <summary>
    /// A third host on its OWN throwaway database (created in this run's PostgreSQL container, never the main E2E database and never a dev or demo database) that holds a
    /// 50,000-line synthetic structure, started on first use (gap wave D, ADR-0034). The ordinary tests never see that data.
    /// </summary>
    public string ScaleHostUrl { get; private set; } = "";
    public SyntheticBom? ScaleBom { get; private set; }
    /// <summary>
    /// The relay of the e-mail notification channel (ADR-0055): an in-process SMTP server on loopback that the hosts send through, so the verification flow can be followed from the
    /// mail to the page. Nothing leaves the machine.
    /// </summary>
    public Keel.TestSupport.TestSmtpServer Smtp => _smtp ??= new(Keel.TestSupport.TestSmtpMode.Plain);
    private Keel.TestSupport.TestSmtpServer? _smtp;
    public string IdpUrl { get; private set; } = "";
    public DemoManifest Manifest { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    public NpgsqlDataSource AppDb { get; private set; } = null!;
    /// <summary>The throwaway vault of this run (viewer tests put their own synthetic files into it through the real services).</summary>
    public VaultOptions Vault { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!Enabled) return;
        Directory.CreateDirectory(ResultsDir);
        _workDir = Directory.CreateTempSubdirectory("keel-e2e-").FullName;
        _log = new StreamWriter(Path.Combine(ResultsDir, "fixture.log"), append: false) { AutoFlush = true };
        try
        {
            var appPort = FreePort();
            var lockPort = FreePort();
            _scalePort = FreePort();
            BaseUrl = $"http://localhost:{appPort}";
            LockHostUrl = $"http://localhost:{lockPort}";
            ScaleHostUrl = $"http://localhost:{_scalePort}";

            // Start the three containers side by side; they do not depend on each other.
            var postgres = StartPostgresAsync();
            var vault = StartVaultAsync();
            var keycloak = StartKeycloakAsync(appPort, lockPort);
            await Task.WhenAll(postgres, vault, keycloak);
            var (appCs, migratorCs) = await postgres;
            _appCsMain = appCs;
            _migratorCsMain = migratorCs;
            var vaultOptions = await vault;
            Vault = vaultOptions;
            IdpUrl = await keycloak;
            Log($"Postgres, vault and Keycloak are up (idp {IdpUrl}).");

            var apiBin = ApiOutputDirectory();
            _apiBin = apiBin;
            var keyPath = Path.Combine(_workDir, "checkpoint.pem");
            using (var key = CheckpointKeys.Generate()) await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            _envFor = port => ApiEnvironment(port, appCs, migratorCs, vaultOptions, keyPath);
            var env = _envFor(appPort);

            await RunToCompletionAsync(apiBin, ["Keel.Api.dll", "--migrate"], env, "migrate");
            Log("Migrations applied.");

            // Demo data goes in through the real services before the host starts, so the first page already has a realistic dataset.
            var settings = new SeedSettings(appCs, vaultOptions, null);
            await using (var host = SeedHost.Create(settings))
            {
                var manifest = await new DemoSeeder(host, RealmUsers.Load(), new LogWriter(Log)).RunAsync();
                var verdict = await host.VerifyAuditAsync([]);
                manifest.AuditChainValid = verdict.Valid;
                if (!verdict.Valid) throw new InvalidOperationException("The audit chain is invalid after seeding: " + string.Join("; ", verdict.Failures));
                Manifest = manifest;
            }
            Log($"Seeded: {Manifest.Parts} parts, {Manifest.Documents} documents, {Manifest.Changes} changes.");

            AppDb = NpgsqlDataSource.Create(appCs);
            StartApi(apiBin, env);
            await WaitForApiAsync();
            Log("The API host is ready.");

            // Chromium is downloaded once by Playwright's own installer (the developer machine may be online; the product never is).
            var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exit != 0) throw new InvalidOperationException($"'playwright install chromium' failed with exit code {exit}.");
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                // The 3D viewer needs WebGL; a CI container has no GPU, so Chromium renders it in software (SwiftShader).
                Args = ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist", "--enable-webgl"],
            });
            Log("Chromium launched.");
        }
        catch (Exception e)
        {
            Log("FIXTURE FAILED: " + e);
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Enabled) return; // nothing was started (the relay is created on first use)
        try { if (Browser is not null) await Browser.CloseAsync(); } catch (PlaywrightException) { }
        _playwright?.Dispose();
        if (_api is { HasExited: false })
        {
            try { _api.Kill(entireProcessTree: true); await _api.WaitForExitAsync(); } catch (InvalidOperationException) { }
        }
        _api?.Dispose();
        if (_lockApi is { HasExited: false })
        {
            try { _lockApi.Kill(entireProcessTree: true); await _lockApi.WaitForExitAsync(); } catch (InvalidOperationException) { }
        }
        _lockApi?.Dispose();
        if (_scaleApi is { HasExited: false })
        {
            try { _scaleApi.Kill(entireProcessTree: true); await _scaleApi.WaitForExitAsync(); } catch (InvalidOperationException) { }
        }
        _scaleApi?.Dispose();
        if (AppDb is not null) await AppDb.DisposeAsync();
        foreach (var c in _containers) { try { await c.DisposeAsync(); } catch (Exception e) { Log("container cleanup: " + e.Message); } }
        _containers.Clear();
        if (_smtp is not null) await _smtp.DisposeAsync();
        _log?.Dispose();
        _log = null;
        try { if (_workDir.Length > 0) Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    public void Log(string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {message}";
        _log?.WriteLine(line);
        Console.Error.WriteLine("[e2e] " + message);
    }

    // ---------------------------------------------------------------- containers

    private async Task<(string App, string Migrator)> StartPostgresAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithResourceMapping(new FileInfo(Path.Combine(RepoRoot, "deploy/compose/initdb/00-roles.sql")), "/docker-entrypoint-initdb.d/")
            .WithCommand("-c", "max_connections=300")
            // Throwaway data in memory: fast, and independent of free space on the Docker disk.
            .WithTmpfsMount("/var/lib/postgresql/data")
            .Build();
        _containers.Add(container);
        await container.StartAsync();
        string Cs(string user, string password) => new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            { Database = "keel", Username = user, Password = password, Pooling = true, MaxPoolSize = 20 }.ConnectionString;
        // The image runs the init script on a socket-only server before the final start; wait until the keel database answers on TCP.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(Cs("keel_migrator", "keel_migrator_dev"));
                await conn.OpenAsync();
                break;
            }
            catch (NpgsqlException) when (attempt < 60) { await Task.Delay(500); }
        }
        _adminCs = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
        return (Cs("keel_app", "keel_app_dev"), Cs("keel_migrator", "keel_migrator_dev"));
    }

    private async Task<VaultOptions> StartVaultAsync()
    {
        const int s3Port = 8333;
        var container = new ContainerBuilder(VaultImage)
            .WithResourceMapping(new FileInfo(Path.Combine(RepoRoot, "deploy/compose/seaweedfs/s3.json")), "/etc/seaweedfs/")
            .WithTmpfsMount("/data")
            .WithCommand("mini", "-dir=/data", "-s3.config=/etc/seaweedfs/s3.json", "-bucket=keel-vault", "-admin.ui=false", "-master.volumeSizeLimitMB=64")
            .WithPortBinding(s3Port, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(s3Port).ForPath("/keel-vault").ForStatusCode(HttpStatusCode.Forbidden)))
            .Build();
        _containers.Add(container);
        await container.StartAsync();
        var options = new VaultOptions
        {
            ServiceUrl = $"http://localhost:{container.GetMappedPublicPort(s3Port)}", AccessKey = "keel_vault_dev", SecretKey = "keel_vault_dev_secret",
            Bucket = "keel-vault", PartSizeBytes = 5 * 1024 * 1024,
        };
        using var vault = new ObjectVault(options);
        for (var attempt = 0; ; attempt++)
        {
            try { await vault.PingAsync(); return options; }
            catch (Exception) when (attempt < 60) { await Task.Delay(500); }
        }
    }

    private async Task<string> StartKeycloakAsync(int appPort, int lockPort)
    {
        // The dev realm with this run's redirect URIs (the app port is random); everything else is the shipped realm, users included.
        var realm = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepoRoot, "deploy/compose/keycloak/keel-realm.json")))!;
        var client = realm["clients"]!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == "keel-web")!;
        var origin = $"http://localhost:{appPort}";
        var lockOrigin = $"http://localhost:{lockPort}";
        var scaleOrigin = $"http://localhost:{_scalePort}";
        client["redirectUris"] = new JsonArray($"{origin}/signin-oidc", $"{lockOrigin}/signin-oidc", $"{scaleOrigin}/signin-oidc");
        client["webOrigins"] = new JsonArray(origin, lockOrigin, scaleOrigin);
        client["attributes"]!["post.logout.redirect.uris"] = $"{origin}/signout-callback-oidc##{lockOrigin}/signout-callback-oidc##{scaleOrigin}/signout-callback-oidc";
        var patched = Path.Combine(_workDir, "keel-realm.json");
        await File.WriteAllTextAsync(patched, realm.ToJsonString());

        var container = new ContainerBuilder(KeycloakImage)
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "keel_kc_admin_dev")
            .WithEnvironment("KC_HEALTH_ENABLED", "true")
            .WithResourceMapping(new FileInfo(patched), "/opt/keycloak/data/import/")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/realms/keel/.well-known/openid-configuration")))
            .Build();
        _containers.Add(container);
        await container.StartAsync();
        return $"http://localhost:{container.GetMappedPublicPort(8080)}/realms/keel";
    }

    // ---------------------------------------------------------------- the real host as a child process

    private Dictionary<string, string> ApiEnvironment(int port, string appCs, string migratorCs, VaultOptions vault, string checkpointKey) => new()
    {
        // Development makes the host serve its static web assets from the build output, like `dotnet run`. Every setting that
        // would otherwise come from appsettings.Development.json (the dev stack!) is overridden here, so a run can never touch it.
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
        ["ASPNETCORE_URLS"] = $"http://localhost:{port}",
        ["Keel__Database__AppConnectionString"] = appCs,
        ["Keel__Database__MigratorConnectionString"] = migratorCs,
        ["Keel__Auth__Authority"] = IdpUrl,
        ["Keel__Auth__RequireHttpsMetadata"] = "false",
        ["Keel__Auth__WebClientSecret"] = "keel-web-dev-secret",
        ["Keel__Audit__CheckpointPrivateKeyPath"] = checkpointKey,
        ["Keel__Audit__CheckpointInterval"] = "00:00:20",
        ["Keel__Security__RequireFips"] = "false",
        ["Keel__Vault__ServiceUrl"] = vault.ServiceUrl,
        ["Keel__Vault__AccessKey"] = vault.AccessKey,
        ["Keel__Vault__SecretKey"] = vault.SecretKey,
        ["Keel__Vault__Bucket"] = vault.Bucket,
        ["Keel__Workflow__StepUpMaxAgeSeconds"] = StepUpSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Logging__LogLevel__Default"] = "Warning",
        // The e-mail channel sends through the in-process relay (plain on loopback, acknowledged), so e-mail verification can be exercised end to end.
        ["Keel__Notifications__Email__Enabled"] = "true",
        ["Keel__Notifications__Email__Host"] = "127.0.0.1",
        ["Keel__Notifications__Email__Port"] = Smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Keel__Notifications__Email__Security"] = "None",
        ["Keel__Notifications__Email__AcknowledgeInsecureTransport"] = "true",
        ["Keel__Notifications__Email__FromAddress"] = "keel@corp.example",
        ["Keel__Notifications__Email__BaseUrl"] = $"http://localhost:{port}",
        ["Keel__Notifications__Email__AllowedRecipientDomains__0"] = "corp.example",
        ["Keel__Notifications__Email__Verification__MinIntervalSeconds"] = "0",
        ["Keel__Notifications__Email__Verification__MaxPerUserPerDay"] = "50",
        ["Keel__Notifications__Email__Verification__MaxPerAddressPerDay"] = "50",
    };

    private async Task RunToCompletionAsync(string workDir, string[] args, Dictionary<string, string> env, string name)
    {
        var psi = NewStart(workDir, args, env);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {name}.");
        var output = await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        await File.WriteAllTextAsync(Path.Combine(ResultsDir, $"{name}.log"), output[0] + output[1]);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{name} failed (exit {process.ExitCode}): {output[1]}{output[0]}");
    }

    private void StartApi(string workDir, Dictionary<string, string> env)
    {
        _api = Process.Start(NewStart(workDir, ["Keel.Api.dll"], env)) ?? throw new InvalidOperationException("Could not start the API host.");
        var apiLog = new StreamWriter(Path.Combine(ResultsDir, "api.log")) { AutoFlush = true };
        _api.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (apiLog) apiLog.WriteLine(e.Data); };
        _api.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (apiLog) apiLog.WriteLine("ERR " + e.Data); };
        _api.BeginOutputReadLine();
        _api.BeginErrorReadLine();
    }

    /// <summary>
    /// Stops the short-idle-lock host. Lock state is shared through the database, so while that host runs it also ends the idle sessions of tests on the main host
    /// (anything idle for more than about 24 s: step-up waits, long audits). Tests that sign in on any other host therefore stop it first; the next lock test restarts it.
    /// </summary>
    public async Task StopLockHostAsync()
    {
        if (_lockApi is null) return;
        if (!_lockApi.HasExited)
        {
            try { _lockApi.Kill(entireProcessTree: true); await _lockApi.WaitForExitAsync(); } catch (InvalidOperationException) { }
        }
        _lockApi.Dispose();
        _lockApi = null;
    }

    /// <summary>Starts the short-idle-lock host (once) and waits until it is ready.</summary>
    public async Task EnsureLockHostAsync()
    {
        if (_lockApi is not null) return;
        var port = int.Parse(LockHostUrl[(LockHostUrl.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var env = _envFor!(port);
        env["Keel__Session__IdleLockMinutes"] = LockIdleMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["Keel__Session__WarningSeconds"] = LockWarningSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["Keel__Session__LockedTerminateMinutes"] = LockTerminateMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["Keel__Session__ActivityThrottleSeconds"] = "1";
        // The checkpoint timer of the main host is enough; keep the second host quiet.
        _lockApi = Process.Start(NewStart(_apiBin, ["Keel.Api.dll"], env)) ?? throw new InvalidOperationException("Could not start the lock host.");
        var log = new StreamWriter(Path.Combine(ResultsDir, "api-lock.log")) { AutoFlush = true };
        _lockApi.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        _lockApi.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine("ERR " + e.Data); };
        _lockApi.BeginOutputReadLine();
        _lockApi.BeginErrorReadLine();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_lockApi.HasExited) throw new InvalidOperationException($"The lock host exited with code {_lockApi.ExitCode}; see {Path.Combine(ResultsDir, "api-lock.log")}.");
            try { if ((await http.GetAsync($"{LockHostUrl}/health/ready")).IsSuccessStatusCode) { Log("The lock host is ready."); return; } }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new TimeoutException("The lock host did not become ready within 90 seconds.");
    }

    /// <summary>
    /// Creates the scale database (a second database in this run's container, owned like the main one), migrates it with the real migrator, fills it with a ten-level,
    /// 50,000-line synthetic structure through the application role, and starts a host on it (once). Synthetic data only.
    /// </summary>
    public async Task EnsureScaleHostAsync()
    {
        if (_scaleApi is not null) return;
        const string database = "keel_scale";
        await using (var admin = new NpgsqlConnection(_adminCs))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {database} OWNER keel_migrator; GRANT CONNECT ON DATABASE {database} TO keel_app; REVOKE ALL ON DATABASE {database} FROM PUBLIC;", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_adminCs) { Database = database }.ConnectionString))
        {
            await owner.OpenAsync();
            await using var cmd = new NpgsqlCommand("REVOKE CREATE ON SCHEMA public FROM PUBLIC; ALTER SCHEMA public OWNER TO keel_migrator;", owner);
            await cmd.ExecuteNonQueryAsync();
        }
        string For(string cs) => new NpgsqlConnectionStringBuilder(cs) { Database = database }.ConnectionString;
        var appCs = For(_appCsMain);
        var migratorCs = For(_migratorCsMain);
        var env = _envFor!(_scalePort);
        env["Keel__Database__AppConnectionString"] = appCs;
        env["Keel__Database__MigratorConnectionString"] = migratorCs;
        await RunToCompletionAsync(_apiBin, ["Keel.Api.dll", "--migrate"], env, "migrate-scale");
        await using (var ds = NpgsqlDataSource.Create(appCs))
        {
            ScaleBom = await SyntheticBomGenerator.InsertAsync(ds, new SyntheticBomOptions { Seed = 23, Levels = 10, TargetLines = 50_000, ReleasedFraction = 0.3 });
        }
        Log($"The scale database holds {ScaleBom!.Parts.Count} parts and {ScaleBom.Lines.Count} usage lines (root {ScaleBom.RootNumber}).");
        _scaleApi = Process.Start(NewStart(_apiBin, ["Keel.Api.dll"], env)) ?? throw new InvalidOperationException("Could not start the scale host.");
        var log = new StreamWriter(Path.Combine(ResultsDir, "api-scale.log")) { AutoFlush = true };
        _scaleApi.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        _scaleApi.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine("ERR " + e.Data); };
        _scaleApi.BeginOutputReadLine();
        _scaleApi.BeginErrorReadLine();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_scaleApi.HasExited) throw new InvalidOperationException($"The scale host exited with code {_scaleApi.ExitCode}; see {Path.Combine(ResultsDir, "api-scale.log")}.");
            try { if ((await http.GetAsync($"{ScaleHostUrl}/health/ready")).IsSuccessStatusCode) { Log("The scale host is ready."); return; } }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new TimeoutException("The scale host did not become ready within 90 seconds.");
    }

    private static ProcessStartInfo NewStart(string workDir, string[] args, Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    private async Task WaitForApiAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_api!.HasExited) throw new InvalidOperationException($"The API host exited with code {_api.ExitCode}; see {Path.Combine(ResultsDir, "api.log")}.");
            try
            {
                using var response = await http.GetAsync($"{BaseUrl}/health/ready");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new TimeoutException("The API host did not become ready within 90 seconds.");
    }

    private string ApiOutputDirectory()
    {
        // The test and the host are built with the same configuration, so the host's output is next to ours in the same layout.
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var dir = Path.Combine(RepoRoot, "src", "Keel.Api", "bin", config, "net10.0");
        if (!File.Exists(Path.Combine(dir, "Keel.Api.dll"))) throw new FileNotFoundException($"The API host is not built at {dir}. Build the solution first.");
        return dir;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Keel.slnx) not found.");
    }

    /// <summary>Adapts the seeder's text log to the fixture log.</summary>
    private sealed class LogWriter(Action<string> log) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => log("seed: " + value);
        public override void Write(char value) { }
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EFixture>
{
    public const string Name = "e2e";
}
