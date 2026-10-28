using System.Net.Http.Headers;
using System.Security.Cryptography;
using Keel.Audit;
using Keel.Documents.Vault;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Keel.IntegrationTests;

/// <summary>
/// Hosts the real API against a fresh test database. Tokens are signed with a local ECDSA key that stands in for
/// the IdP's JWKS, so authentication runs the production JwtBearer validation path.
/// </summary>
public sealed class ApiFactory(TestDatabase db, string checkpointKeyPath, VaultOptions vault) : WebApplicationFactory<Program>
{
    public const string Issuer = "https://idp.test/realms/keel";
    private static readonly ECDsaSecurityKey SigningKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "test-key" };

    public TestDatabase Database => db;
    public VaultOptions Vault => vault;

    public static async Task<ApiFactory> CreateAsync(PostgresFixture pg)
    {
        var database = await pg.CreateDatabaseAsync();
        var dir = Directory.CreateTempSubdirectory("keel-test-");
        var path = Path.Combine(dir.FullName, "checkpoint.pem");
        using var key = CheckpointKeys.Generate();
        await File.WriteAllTextAsync(path, key.ExportPkcs8PrivateKeyPem());
        return new ApiFactory(database, path, await VaultContainer.OptionsAsync());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets(); // serve Keel.Web and framework assets from the build output, as in Development
        builder.UseSetting("Keel:Database:AppConnectionString", db.AppConnectionString);
        builder.UseSetting("Keel:Auth:Authority", Issuer);
        builder.UseSetting("Keel:Auth:Audience", "keel-api");
        builder.UseSetting("Keel:Auth:RequireHttpsMetadata", "false");
        builder.UseSetting("Keel:Audit:CheckpointPrivateKeyPath", checkpointKeyPath);
        builder.UseSetting("Keel:Audit:CheckpointInterval", "01:00:00");
        builder.UseSetting("Keel:Security:RequireFips", "false");
        builder.UseSetting("Keel:Vault:ServiceUrl", vault.ServiceUrl);
        builder.UseSetting("Keel:Vault:AccessKey", vault.AccessKey);
        builder.UseSetting("Keel:Vault:SecretKey", vault.SecretKey);
        builder.UseSetting("Keel:Vault:Bucket", vault.Bucket);
        builder.UseSetting("Keel:Vault:PartSizeBytes", vault.PartSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(SigningKey);
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            }));
    }

    public static string Token(
        string sub = "u-alice",
        bool? usPerson = true,
        string[]? roles = null,
        string[]? programs = null,
        string[]? cui = null,
        string[]? auths = null,
        string audience = "keel-api",
        DateTime? expires = null,
        DateTimeOffset? authTime = null,
        bool includeAuthTime = true,
        string? displayName = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["name"] = displayName ?? ("User " + sub.Replace("u-", "", StringComparison.Ordinal)),
            ["preferred_username"] = sub.Replace("u-", "", StringComparison.Ordinal),
            ["org"] = "ENG",
            ["citizenship"] = "USA",
            ["roles"] = roles ?? ["plm-viewer"],
            ["programs"] = programs ?? ["ORCA"],
            ["cui_categories"] = cui ?? ["EXPT"],
            ["export_authorizations"] = auths ?? [],
        };
        if (usPerson is { } us) claims["us_person"] = us;
        // auth_time: when the user last authenticated at the IdP; fresh by default so signing works unless a test says otherwise.
        if (includeAuthTime) claims["auth_time"] = (authTime ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = exp < now ? exp.AddMinutes(-5) : now,
            NotBefore = exp < now ? exp.AddMinutes(-5) : now,
            Expires = exp,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    public HttpClient ClientFor(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
