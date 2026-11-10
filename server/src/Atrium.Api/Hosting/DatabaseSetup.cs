using Atrium.Data;
using Atrium.Data.Migrations;
using Atrium.Data.Postgres;
using Atrium.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atrium.Api.Hosting;

public static class DatabaseSetup
{
    public static IServiceCollection AddAtriumDatabase(this IServiceCollection services, BootstrapOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<AtriumDbContext>(builder =>
        {
            switch (options.DatabaseProvider)
            {
                case DatabaseProvider.Sqlite:
                    builder.UseAtriumSqlite(options.ConnectionString);
                    break;
                case DatabaseProvider.Postgres:
                    builder.UseAtriumPostgres(options.ConnectionString);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown database provider {options.DatabaseProvider}.");
            }
        });

        services.AddScoped<IMigrationOperations, EfMigrationOperations>();
        services.AddScoped<IPreMigrationBackup>(sp => options.DatabaseProvider switch
        {
            DatabaseProvider.Sqlite => new SqlitePreMigrationBackup(
                sp.GetRequiredService<AtriumDbContext>(),
                options.BackupDir,
                sp.GetRequiredService<TimeProvider>()),
            _ => new PostgresPreMigrationBackup(),
        });
        services.AddScoped<DatabaseMigrator>();

        return services;
    }

    /// <summary>
    /// Runs before the server accepts requests. A failure here stops startup:
    /// serving requests against a schema the code does not match risks
    /// corrupting metadata.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<DatabaseMigrator>();
        await migrator.MigrateAsync(app.Lifetime.ApplicationStopping);
    }
}
