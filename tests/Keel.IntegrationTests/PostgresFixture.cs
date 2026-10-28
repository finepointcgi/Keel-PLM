using Keel.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Keel.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container per test run, bootstrapped with the same role script as the Compose stack.
/// Each test asks for its own database so tests that tamper with the audit log stay isolated.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithResourceMapping(new FileInfo(Path.Combine(RepoRoot, "deploy/compose/initdb/00-roles.sql")), "/docker-entrypoint-initdb.d/")
        .WithCommand("-c", "max_connections=300")
        // Test databases are throwaway: keep them in memory (faster, and independent of free space on the Docker disk).
        .WithTmpfsMount("/var/lib/postgresql/data")
        .Build();
    private int _counter;

    public static string MigrationsDirectory => Path.Combine(RepoRoot, "db/migrations");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        // The image runs init scripts on a socket-only server before the final start; wait for the keel database to exist.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnectionString("keel", "keel_migrator", "keel_migrator_dev"));
                await conn.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (attempt < 60)
            {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public string ConnectionString(string database, string user, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database, Username = user, Password = password, Pooling = true, MaxPoolSize = 20,
        }.ConnectionString;

    public string SuperuserConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>Creates a fresh database with the production role layout and all migrations applied.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"keel_t{Interlocked.Increment(ref _counter)}";
        await using (var conn = new NpgsqlConnection(SuperuserConnectionString("postgres")))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name} OWNER keel_migrator; GRANT CONNECT ON DATABASE {name} TO keel_app; REVOKE ALL ON DATABASE {name} FROM PUBLIC;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var conn = new NpgsqlConnection(SuperuserConnectionString(name)))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("REVOKE CREATE ON SCHEMA public FROM PUBLIC; ALTER SCHEMA public OWNER TO keel_migrator;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var migratorCs = ConnectionString(name, "keel_migrator", "keel_migrator_dev");
        await new Migrator(migratorCs, MigrationsDirectory, "keel_app", NullLogger.Instance).MigrateAsync();
        return new TestDatabase(name, ConnectionString(name, "keel_app", "keel_app_dev"), migratorCs, SuperuserConnectionString(name));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Keel.slnx) not found.");
    }
}

public sealed record TestDatabase(string Name, string AppConnectionString, string MigratorConnectionString, string SuperuserConnectionString)
{
    public NpgsqlDataSource AppDataSource() => NpgsqlDataSource.Create(AppConnectionString);

    public async Task ExecuteAsSuperuserAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
