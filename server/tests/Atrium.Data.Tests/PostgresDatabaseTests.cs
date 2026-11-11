using Atrium.Data.Postgres;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Atrium.Data.Tests;

/// <summary>
/// Runs the database contract against a real PostgreSQL in a container.
/// Needs Docker, so it runs in CI; exclude it locally with
/// <c>dotnet test --filter "Category!=Containers"</c>.
/// </summary>
[Trait("Category", "Containers")]
public sealed class PostgresDatabaseTests : DatabaseContractTests
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    protected override Task StartDatabaseAsync() => _container.StartAsync();

    protected override Task StopDatabaseAsync() => _container.DisposeAsync().AsTask();

    protected override AtriumDbContext CreateContext() =>
        new((DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
            .UseAtriumPostgres(_container.GetConnectionString())
            .Options);
}
