using Atrium.Data;
using Atrium.Data.Backup;
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

        services.AddSingleton<IBackupDialect>(options.DatabaseProvider switch
        {
            DatabaseProvider.Sqlite => new SqliteBackupDialect(),
            _ => new PostgresBackupDialect(),
        });
        services.AddSingleton(new BackupOptions(options.BackupDir));
        services.AddScoped<IMigrationOperations, EfMigrationOperations>();
        services.AddScoped<IPreMigrationBackup>(sp => new PortablePreMigrationBackup(
            sp.GetRequiredService<AtriumDbContext>(),
            sp.GetRequiredService<IBackupDialect>(),
            options.BackupDir,
            sp.GetRequiredService<TimeProvider>()));
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
