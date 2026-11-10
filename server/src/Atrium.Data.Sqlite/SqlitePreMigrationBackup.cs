using System.Globalization;
using Atrium.Data.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Data.Sqlite;

/// <summary>
/// Snapshot of the SQLite database taken before a migration. VACUUM INTO
/// writes a consistent copy even while the database is in use. The copy is
/// then opened and checked, because a backup that cannot be read is worse
/// than none: it would let the migration proceed on false confidence.
/// </summary>
public sealed class SqlitePreMigrationBackup(AtriumDbContext db, string backupDirectory, TimeProvider time)
    : IPreMigrationBackup
{
    public async Task<string> CreateAsync(string fromMigration, CancellationToken ct)
    {
        Directory.CreateDirectory(backupDirectory);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(backupDirectory, $"pre-migration-{stamp}-{fromMigration}.db");
        if (File.Exists(path))
        {
            throw new IOException($"Backup file '{path}' already exists; refusing to overwrite it.");
        }

        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [path], ct);
        await VerifyAsync(path, ct);
        return path;
    }

    private static async Task VerifyAsync(string path, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = await command.ExecuteScalarAsync(ct) as string;
        if (!string.Equals(result, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup '{path}' failed its integrity check: {result}");
        }
    }
}
