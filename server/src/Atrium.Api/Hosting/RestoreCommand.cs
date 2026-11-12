using System.Globalization;
using Atrium.Data;
using Atrium.Data.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Hosting;

/// <summary>
/// <c>Atrium.Api restore &lt;backup.zip&gt;</c>: restores a metadata backup
/// (DESIGN.md 8.2). Runs instead of the server, under the same instance
/// lock, so it cannot run while the server does.
/// </summary>
/// <remarks>
/// Nothing is ever deleted. An existing SQLite database is moved aside
/// under a new name first; a PostgreSQL database must be empty, because
/// the command will not guess which of two sets of metadata to keep.
/// The server's next start applies any newer migrations as usual.
/// </remarks>
public static class RestoreCommand
{
    public const string Argument = "restore";

    public static bool TryGetPath(string[] args, out string path)
    {
        path = string.Empty;
        if (args.Length >= 2 && args[0] == Argument)
        {
            path = Path.GetFullPath(args[1]);
            return true;
        }

        return false;
    }

    public static async Task<int> RunAsync(IServiceProvider services, string path, BootstrapOptions bootstrap, TextWriter output)
    {
        if (!File.Exists(path))
        {
            await output.WriteLineAsync($"No backup found at {path}.");
            return 2;
        }

        try
        {
            await using (var check = File.OpenRead(path))
            {
                var manifest = await PortableBackup.VerifyAsync(check, CancellationToken.None);
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"Backup from {manifest.CreatedAt:u} ({manifest.Reason}, {manifest.Provider}, schema {manifest.Migration}): {manifest.Tables.Sum(t => t.Rows)} rows. Verified."));
            }

            if (bootstrap.DatabaseProvider == DatabaseProvider.Sqlite && MoveAside(bootstrap) is { } moved)
            {
                await output.WriteLineAsync($"The existing database was moved to {moved}. Nothing was deleted.");
            }

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
            var dialect = scope.ServiceProvider.GetRequiredService<IBackupDialect>();
            await using var input = File.OpenRead(path);
            var rows = await PortableBackup.RestoreAsync(db, dialect, input, CancellationToken.None);
            await output.WriteLineAsync($"Restored {rows} rows. Start the server as usual.");
            return 0;
        }
        catch (Exception e) when (e is BackupInvalidException or InvalidDataException)
        {
            await output.WriteLineAsync("Restore refused: " + e.Message);
            return 1;
        }
    }

    /// <summary>Renames the SQLite database and its journal files so the restore starts from an empty database.</summary>
    private static string? MoveAside(BootstrapOptions bootstrap)
    {
        var file = new SqliteConnectionStringBuilder(bootstrap.ConnectionString).DataSource;
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return null;
        }

        SqliteConnection.ClearAllPools();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = $"{file}.before-restore-{stamp}";
        File.Move(file, target);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(file + suffix))
            {
                File.Move(file + suffix, target + suffix);
            }
        }

        return target;
    }
}
