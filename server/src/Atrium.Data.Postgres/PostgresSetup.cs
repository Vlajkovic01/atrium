using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Atrium.Data.Postgres;

public static class PostgresSetup
{
    public static DbContextOptionsBuilder UseAtriumPostgres(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(typeof(PostgresSetup).Assembly.GetName().Name));
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
