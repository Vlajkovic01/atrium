using Atrium.Data.Postgres;
using Atrium.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Atrium.Jobs.Tests;

public sealed class SqliteJobQueueTests : JobQueueContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    protected override void UseDatabase(DbContextOptionsBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        builder.UseAtriumSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "atrium.db"),
            Pooling = false,
        }.ToString());
    }

    protected override Task StopDatabaseAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files must not fail a test run.
        }

        return Task.CompletedTask;
    }
}

/// <summary>Needs Docker, so it runs in CI (D-021).</summary>
[Trait("Category", "Containers")]
public sealed class PostgresJobQueueTests : JobQueueContractTests
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    protected override void UseDatabase(DbContextOptionsBuilder builder) =>
        builder.UseAtriumPostgres(_container.GetConnectionString());

    protected override Task StartDatabaseAsync() => _container.StartAsync();

    protected override Task StopDatabaseAsync() => _container.DisposeAsync().AsTask();
}
