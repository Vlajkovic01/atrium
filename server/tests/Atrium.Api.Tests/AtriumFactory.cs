using Atrium.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Atrium.Api.Tests;

/// <summary>
/// Hosts the real server in memory against a real SQLite database in its own
/// temporary config directory, so tests never share state.
/// </summary>
public sealed class AtriumFactory : WebApplicationFactory<Program>
{
    public string ConfigDir { get; } =
        Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting(BootstrapOptions.ConfigDirKey, ConfigDir);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        // Pooled connections keep the database file open on Windows.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(ConfigDir, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files must not fail a test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
