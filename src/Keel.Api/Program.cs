using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Keel.Api;
using Keel.Api.Endpoints;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});

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

var fips = FipsStatus.Detect();
if (options.Security.RequireFips && !fips.Enabled)
    throw new InvalidOperationException($"Keel:Security:RequireFips is set but FIPS mode is not active: {fips.Detail}");
builder.Services.AddSingleton(fips);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.Database.AppConnectionString));
builder.Services.AddSingleton<PostgresAuditLog>();
builder.Services.AddSingleton<IAuditLog, MeteredAuditLog>();
builder.Services.AddSingleton<AuditQueries>();
builder.Services.AddSingleton<Keel.Parts.PartService>();
builder.Services.AddKeelDocuments(builder.Configuration);

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

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseSecurityHeaders();
app.UseCorrelationId();
app.UseHttpMetrics();
app.UseAuthentication();
app.UseAuthorization();

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

app.MapKeelApi();
app.MapPartEndpoints();
app.MapDocumentEndpoints();

app.Run();

public partial class Program;
