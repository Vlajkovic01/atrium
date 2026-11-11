using Atrium.Data.Postgres;
using Atrium.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Data.Tests;

/// <summary>
/// Fails when the model was changed without generating a migration for a
/// provider. Needs no database connection, so both providers are checked
/// everywhere, including machines without Docker.
/// </summary>
public sealed class ModelSnapshotTests
{
    [Fact]
    public void Sqlite_migrations_match_the_model()
    {
        using var db = new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
                .UseAtriumSqlite("Data Source=unused.db")
                .Options);

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void Postgres_migrations_match_the_model()
    {
        using var db = new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
                .UseAtriumPostgres("Host=unused")
                .Options);

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void Both_providers_have_the_same_number_of_migrations()
    {
        using var sqlite = new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
                .UseAtriumSqlite("Data Source=unused.db")
                .Options);
        using var postgres = new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
                .UseAtriumPostgres("Host=unused")
                .Options);

        Assert.Equal(sqlite.Database.GetMigrations().Count(), postgres.Database.GetMigrations().Count());
    }
}
