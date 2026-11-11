using System.Net;
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
    public const string TrustedProxiesKey = "ATRIUM_TRUSTED_PROXIES";

    private BootstrapOptions(
        string configDir, DatabaseProvider provider, string connectionString, IReadOnlyList<IPNetwork> trustedProxies)
    {
        ConfigDir = configDir;
        DatabaseProvider = provider;
        ConnectionString = connectionString;
        TrustedProxies = trustedProxies;
    }

    /// <summary>
    /// Addresses of reverse proxies whose forwarding headers are believed
    /// (DESIGN.md 6.9). Empty by default: a header saying "this request is
    /// really from address X over HTTPS" is ignored unless it comes from a
    /// proxy listed here, because anyone can send such a header.
    /// </summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; }

    /// <summary>Holds the SQLite database, server keys and local backups.</summary>
    public string ConfigDir { get; }

    public DatabaseProvider DatabaseProvider { get; }

    public string ConnectionString { get; }

    public string BackupDir => Path.Combine(ConfigDir, "backups");

    public static BootstrapOptions From(IConfiguration configuration)
    {
        var configDir = Path.GetFullPath(NonEmpty(configuration[ConfigDirKey]) ?? ".atrium");
        var connection = NonEmpty(configuration[DatabaseConnectionKey]);
        var proxies = ParseProxies(NonEmpty(configuration[TrustedProxiesKey]));

        var providerName = NonEmpty(configuration[DatabaseProviderKey]) ?? "sqlite";
        switch (providerName.ToLowerInvariant())
        {
            case "sqlite":
                connection ??= new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(configDir, "atrium.db"),
                }.ToString();
                return new BootstrapOptions(configDir, DatabaseProvider.Sqlite, connection, proxies);

            case "postgres" or "postgresql":
                if (connection is null)
                {
                    throw new InvalidOperationException(
                        $"{DatabaseProviderKey} is 'postgres' but {DatabaseConnectionKey} is not set. " +
                        "Set it to a PostgreSQL connection string.");
                }

                return new BootstrapOptions(configDir, DatabaseProvider.Postgres, connection, proxies);

            default:
                throw new InvalidOperationException(
                    $"{DatabaseProviderKey} '{providerName}' is not supported. Use 'sqlite' or 'postgres'.");
        }
    }

    // A comma-separated list of addresses or networks: "172.30.0.2, 10.0.0.0/24".
    private static IReadOnlyList<IPNetwork> ParseProxies(string? value)
    {
        if (value is null)
        {
            return [];
        }

        var networks = new List<IPNetwork>();
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                // A typo here must stop the server: silently trusting nothing
                // would break HTTPS detection, silently trusting more would
                // be a security hole.
                throw new InvalidOperationException(
                    $"{TrustedProxiesKey} contains '{entry}', which is not an IP address or a network such as 10.0.0.0/24.");
            }
        }

        return networks;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
