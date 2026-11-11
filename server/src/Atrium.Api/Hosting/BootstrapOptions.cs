using Microsoft.Data.Sqlite;

namespace Atrium.Api.Hosting;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>
/// The few values the server needs before it can read its own settings
/// from the database. They come from environment variables; everything
/// else is configured in the setup wizard (DESIGN.md 1.3).
/// </summary>
/// <remarks>
/// Deliberately a class, not a record: a record's generated ToString would
/// print the connection string, which can contain a password.
/// </remarks>
public sealed class BootstrapOptions
{
    public const string ConfigDirKey = "ATRIUM_CONFIG_DIR";
    public const string DatabaseProviderKey = "ATRIUM_DB_PROVIDER";
    public const string DatabaseConnectionKey = "ATRIUM_DB_CONNECTION";

    private BootstrapOptions(string configDir, DatabaseProvider provider, string connectionString)
    {
        ConfigDir = configDir;
        DatabaseProvider = provider;
        ConnectionString = connectionString;
    }

    /// <summary>Holds the SQLite database, server keys and local backups.</summary>
    public string ConfigDir { get; }

    public DatabaseProvider DatabaseProvider { get; }

    public string ConnectionString { get; }

    public string BackupDir => Path.Combine(ConfigDir, "backups");

    public static BootstrapOptions From(IConfiguration configuration)
    {
        var configDir = Path.GetFullPath(NonEmpty(configuration[ConfigDirKey]) ?? ".atrium");
        var connection = NonEmpty(configuration[DatabaseConnectionKey]);

        var providerName = NonEmpty(configuration[DatabaseProviderKey]) ?? "sqlite";
        switch (providerName.ToLowerInvariant())
        {
            case "sqlite":
                connection ??= new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(configDir, "atrium.db"),
                }.ToString();
                return new BootstrapOptions(configDir, DatabaseProvider.Sqlite, connection);

            case "postgres" or "postgresql":
                if (connection is null)
                {
                    throw new InvalidOperationException(
                        $"{DatabaseProviderKey} is 'postgres' but {DatabaseConnectionKey} is not set. " +
                        "Set it to a PostgreSQL connection string.");
                }

                return new BootstrapOptions(configDir, DatabaseProvider.Postgres, connection);

            default:
                throw new InvalidOperationException(
                    $"{DatabaseProviderKey} '{providerName}' is not supported. Use 'sqlite' or 'postgres'.");
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
