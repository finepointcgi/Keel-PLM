using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Keel.Api;
using Keel.Api.Endpoints;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Core;
using Keel.Workflow;
using Keel.Web.Components;
using Keel.Web.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Prometheus;

// A fatal startup error (bad configuration, FIPS required but not active) must end the process with a message and exit code 1. The
// runtime's default is to abort, which asks the kernel for a core dump of the whole process: slow, large and pointless on a
// read-only container whose disk may be nearly full.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine($"FATAL: {e.ExceptionObject}");
    Environment.Exit(1);
};

// Container HEALTHCHECK without curl or a shell (ADR-0020): `Keel.Api.dll --healthcheck [url]` exits 0 when /health/live answers 200.
if (args.Contains("--healthcheck"))
{
    var url = args.SkipWhile(a => a != "--healthcheck").Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:8080/health/live";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync(url);
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"health check failed: {ex.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});

// Secrets as files (ADR-0020): every file named Keel__Section__Key in KEEL_SECRETS_DIR becomes the setting Keel:Section:Key. This is the
// shape of a Docker/Compose secrets directory and of a mounted Kubernetes Secret, so no secret needs to appear in an environment
// variable (visible in `docker inspect` and the pod spec) or in an image.
var secretsDirectory = builder.Configuration["KEEL_SECRETS_DIR"];
if (!string.IsNullOrWhiteSpace(secretsDirectory))
{
    if (!Directory.Exists(secretsDirectory))
        throw new InvalidOperationException($"KEEL_SECRETS_DIR '{secretsDirectory}' does not exist.");
    builder.Configuration.AddKeyPerFile(o =>
    {
        o.FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(secretsDirectory);
        o.Optional = false;
        o.IgnoreCondition = name => !name.StartsWith("Keel__", StringComparison.Ordinal);
    });
}

var options = builder.Configuration.GetSection(KeelOptions.Section).Get<KeelOptions>() ?? new KeelOptions();
builder.Services.AddSingleton(options);

if (args.Contains("--migrate"))
{
    using var loggerFactory = LoggerFactory.Create(l => l.AddJsonConsole());
    var migrator = new Migrator(options.Database.MigratorConnectionString, Migrator.DefaultDirectory, options.Database.AppRole,
        loggerFactory.CreateLogger<Migrator>());
    var applied = await migrator.MigrateAsync();
    Console.WriteLine($"Migrations applied: {applied}");
    return;
}

if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(options.DataProtection.KeyDirectory))
    throw new InvalidOperationException("Keel:DataProtection:KeyDirectory is required in Production: sessions and antiforgery tokens need a persistent, shared key ring.");
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Keel");
if (!string.IsNullOrWhiteSpace(options.DataProtection.KeyDirectory))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.DataProtection.KeyDirectory));
if (options.Proxy.Enabled)
{
    var networks = options.Proxy.KnownNetworks
        .Select(n => System.Net.IPNetwork.TryParse(n, out var parsed) ? parsed : throw new InvalidOperationException($"Keel:Proxy:KnownNetworks entry '{n}' is not a CIDR range."))
        .ToList();
    if (networks.Count == 0)
        throw new InvalidOperationException("Keel:Proxy:KnownNetworks must list the proxy networks when Keel:Proxy:Enabled is true.");
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var n in networks) o.KnownIPNetworks.Add(n);
        o.ForwardLimit = 1;
    });
}

var fips = FipsStatus.Detect();
if (options.Security.RequireFips && !fips.Enabled)
    throw new InvalidOperationException($"Keel:Security:RequireFips is set but FIPS mode is not active: {fips.Detail}");
builder.Services.AddSingleton(fips);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.Database.AppConnectionString));
builder.Services.AddSingleton<PostgresAuditLog>();
builder.Services.AddSingleton<IAuditLog, MeteredAuditLog>();
builder.Services.AddSingleton<AuditQueries>();
builder.Services.AddSingleton<AuditLogService>();
builder.Services.AddSingleton<Keel.Parts.PartService>();
builder.Services.AddKeelDocuments(builder.Configuration);
Keel.Bom.BomModule.AddKeelBom(builder.Services); // 1.4 BOM
builder.Services.AddSingleton<IWorkflowObjectGateway, Keel.Parts.PartWorkflowGateway>();
builder.Services.AddSingleton<IChangeObjectGateway, Keel.Parts.PartChangeGateway>(); // 1.6 change management
builder.Services.AddSingleton<ILinkedDocumentProvider, Keel.Documents.LinkedDocumentProvider>();
builder.Services.AddSingleton<IDocumentPinProvider, Keel.Documents.DocumentPinProvider>(); // 2.1 baselines pin documents
Keel.Change.ChangeModule.AddKeelChange(builder.Services);
builder.Services.AddKeelWorkflow(builder.Configuration);
builder.Services.AddSingleton<Keel.Migration.BatchQueries>(); // Phase 3: read-only migration batch viewer (admin page); loading is the keel-import CLI
Keel.Admin.AdminModule.AddKeelAdmin(builder.Services); // 2.2 administration and classification

var publicKeys = options.Audit.CheckpointPublicKeyPaths.Select(CheckpointKeys.LoadPem).ToList();
if (!string.IsNullOrEmpty(options.Audit.CheckpointPrivateKeyPath))
{
    var signingKey = CheckpointKeys.LoadPem(options.Audit.CheckpointPrivateKeyPath);
    var signingKeyId = CheckpointKeys.KeyId(signingKey);
    if (publicKeys.All(k => CheckpointKeys.KeyId(k) != signingKeyId))
    {
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(signingKey.ExportSubjectPublicKeyInfo(), out _);
        publicKeys.Add(pub);
    }
    builder.Services.AddSingleton(sp => new CheckpointSigner(signingKey, sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddHostedService<CheckpointService>();
}
builder.Services.AddSingleton(new TrustedCheckpointKeys(publicKeys));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<VaultUnavailableExceptionHandler>();

// Two ways in (ADR-0014): browsers use the OIDC cookie session; API clients use JWT bearer. The "keel" policy scheme
// routes /api/* and any request carrying a bearer token to JWT, everything else to the cookie (whose challenge
// redirects to the IdP).
const string SmartScheme = "keel";
var uiOptions = new KeelUiOptions
{
    Authority = options.Auth.Authority,
    ClientId = options.Auth.WebClientId,
    ClientSecret = options.Auth.WebClientSecret,
    RequireHttpsMetadata = options.Auth.RequireHttpsMetadata,
};
builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = SmartScheme;
        o.DefaultChallengeScheme = SmartScheme;
    })
    .AddPolicyScheme(SmartScheme, "API bearer or UI cookie", o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? JwtBearerDefaults.AuthenticationScheme
            : KeelUi.CookieScheme)
    .AddKeelUi(uiOptions, secureCookies: !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    .AddJwtBearer(o =>
    {
        o.Authority = options.Auth.Authority;
        o.Audience = options.Auth.Audience;
        o.RequireHttpsMetadata = options.Auth.RequireHttpsMetadata;
        o.MapInboundClaims = false;
        o.TokenValidationParameters.ValidateIssuer = true;
        o.TokenValidationParameters.ValidateAudience = true;
        o.TokenValidationParameters.ValidateLifetime = true;
        o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(60);
        o.TokenValidationParameters.NameClaimType = "sub";
        o.TokenValidationParameters.RoleClaimType = "roles";
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.AnyPlmRole, p => p.RequireAuthenticatedUser().RequireClaim("sub").RequireClaim("roles", Roles.All))
    .AddPolicy(Policies.Auditor, p => p.RequireAuthenticatedUser().RequireClaim("sub").RequireClaim("roles", Roles.Auditor))
    .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireClaim("sub").RequireClaim("roles", Roles.Admin));
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<FipsHealthCheck>("fips", tags: ["ready"]);

var app = builder.Build();

if (options.Proxy.Enabled) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseSecurityHeaders(Uri.TryCreate(options.Auth.Authority, UriKind.Absolute, out var idp) ? idp.GetLeftPart(UriPartial.Authority) : null);
app.UseCorrelationId();
app.UseHttpMetrics();
app.UseAuthentication();
app.UseAuthorization();
app.UseKeelUserDirectory();
app.UseAntiforgery();

// Liveness says only that the process serves requests; readiness gates traffic on DB + schema + FIPS posture.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status200OK },
    ResponseWriter = HealthResponse.WriteAsync,
}).AllowAnonymous();
// ASSUMPTION(A12): /metrics is reachable only from the cluster network (NetworkPolicy / host firewall), see docs/setup.md.
app.MapMetrics().AllowAnonymous();

app.MapStaticAssets().AllowAnonymous();
app.MapKeelApi();
app.MapPartEndpoints();
app.MapDocumentEndpoints();
app.MapBomEndpoints(); // 1.4 BOM
app.MapWorkflowEndpoints();
app.MapChangeEndpoints(); // 1.6 change management
app.MapConfigurationEndpoints(); // 2.1 baselines and effectivity
app.MapAdminEndpoints(); // 2.2 administration and classification
app.MapMbomEndpoints(); // 2.3 EBOM / MBOM and reconciliation
app.MapKeelUi<App>();

app.Run();

public partial class Program;
