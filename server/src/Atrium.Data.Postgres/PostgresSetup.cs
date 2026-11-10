using Atrium.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Atrium.Data.Postgres;

public static class PostgresSetup
{
    public static DbContextOptionsBuilder UseAtriumPostgres(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(typeof(PostgresSetup).Assembly.GetName().Name));
}

/// <summary>
/// Placeholder until the portable metadata backup (D-008) exists. It refuses,
/// so a PostgreSQL database that holds data is never migrated without a
/// backup. The first migration runs on an empty database and does not come
/// here; this must be replaced before a second migration ships.
/// </summary>
public sealed class PostgresPreMigrationBackup : IPreMigrationBackup
{
    public Task<string> CreateAsync(string fromMigration, CancellationToken ct) =>
        throw new NotSupportedException(
            "Automatic metadata backup for PostgreSQL is not available in this build, so the database " +
            "will not be migrated. Back up the database yourself and use a build that includes the portable backup.");
}

/// <summary>Used only by the EF Core tools when generating migrations. Never connects.</summary>
internal sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<AtriumDbContext>
{
    public AtriumDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AtriumDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=atrium_design_time",
                o => o.MigrationsAssembly(typeof(PostgresSetup).Assembly.GetName().Name))
            .Options);
}
