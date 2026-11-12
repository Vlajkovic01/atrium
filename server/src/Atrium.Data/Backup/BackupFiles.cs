using System.Globalization;
using System.Reflection;
using Atrium.Data.Migrations;

namespace Atrium.Data.Backup;

/// <summary>One backup file in the backup directory.</summary>
public sealed record BackupFile(string Name, string Path, long Size, DateTimeOffset CreatedAt, string Reason);

/// <summary>
/// Backup files on local disk: written atomically, verified before they
/// count, and pruned by age within each reason.
/// </summary>
public static class BackupFiles
{
    public const string Prefix = "atrium-backup-";
    public const string Extension = ".zip";

    public static string ServerVersion { get; } =
        typeof(BackupFiles).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Writes a backup to <paramref name="directory"/>. The file appears
    /// under its final name only after it was written, flushed to disk and
    /// verified, so a backup that exists can be trusted.
    /// </summary>
    public static async Task<(BackupFile File, BackupManifest Manifest)> CreateAsync(
        AtriumDbContext db, IBackupDialect dialect, string directory, string reason, DateTimeOffset now, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var name = $"{Prefix}{now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}-{reason}{Extension}";
        var path = System.IO.Path.Combine(directory, name);
        if (File.Exists(path))
        {
            throw new IOException($"Backup file '{path}' already exists; refusing to overwrite it.");
        }

        var temp = path + ".partial";
        BackupManifest manifest;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                manifest = await PortableBackup.WriteAsync(db, dialect, file, reason, ServerVersion, now, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
                file.Position = 0;
                await PortableBackup.VerifyAsync(file, ct);
            }

            File.Move(temp, path);
        }
        finally
        {
            File.Delete(temp);
        }

        return (Describe(path)!, manifest);
    }

    public static List<BackupFile> List(string directory) =>
        !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, Prefix + "*" + Extension)
                .Select(Describe)
                .OfType<BackupFile>()
                .OrderByDescending(f => f.CreatedAt)
                .ToList();

    /// <summary>Keeps the newest <paramref name="keep"/> backups of one reason and deletes the older ones.</summary>
    public static IReadOnlyList<BackupFile> Prune(string directory, string reason, int keep)
    {
        var old = List(directory).Where(f => f.Reason == reason).Skip(keep).ToList();
        foreach (var file in old)
        {
            File.Delete(file.Path);
        }

        return old;
    }

    public static BackupFile? Describe(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return TryParseName(name, out var created, out var reason)
            ? new BackupFile(name, path, new FileInfo(path).Length, created, reason)
            : null;
    }

    /// <summary>Reads the time and reason from a backup's file name.</summary>
    public static bool TryParseName(string name, out DateTimeOffset created, out string reason)
    {
        created = default;
        reason = string.Empty;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        var core = name[Prefix.Length..^Extension.Length];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || !DateTimeOffset.TryParseExact(core[..dash], "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out created))
        {
            return false;
        }

        reason = core[(dash + 1)..];
        return true;
    }
}

/// <summary>
/// The backup taken before every migration (DESIGN.md 8.1), on both
/// databases. If it cannot be written and verified, the migration does not
/// run.
/// </summary>
public sealed class PortablePreMigrationBackup(AtriumDbContext db, IBackupDialect dialect, string directory, TimeProvider time)
    : IPreMigrationBackup
{
    public const string ReasonPrefix = "pre-migration";

    public async Task<string> CreateAsync(string fromMigration, CancellationToken ct)
    {
        var (file, _) = await BackupFiles.CreateAsync(db, dialect, directory, ReasonPrefix, time.GetUtcNow(), ct);
        return file.Path;
    }
}
