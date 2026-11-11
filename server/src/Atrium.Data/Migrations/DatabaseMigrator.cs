using Microsoft.Extensions.Logging;

namespace Atrium.Data.Migrations;

/// <summary>The server is older than the database it was pointed at.</summary>
public sealed class DatabaseNewerThanServerException(IReadOnlyList<string> unknownMigrations)
    : Exception(
        "The database was created by a newer version of Atrium than this one " +
        $"(unknown migrations: {string.Join(", ", unknownMigrations)}). " +
        "Downgrading is not supported. Start the newer version, or restore a metadata backup taken before the upgrade.")
{
    public IReadOnlyList<string> UnknownMigrations { get; } = unknownMigrations;
}

/// <summary>What the migrator needs from the database. Separate so the ordering rules can be tested.</summary>
public interface IMigrationOperations
{
    /// <summary>Migrations this build of the server contains, oldest first.</summary>
    IReadOnlyList<string> Known { get; }

    Task<IReadOnlyList<string>> GetAppliedAsync(CancellationToken ct);

    Task ApplyPendingAsync(CancellationToken ct);
}

/// <summary>Takes a metadata backup before the schema changes (DESIGN.md 8.1).</summary>
public interface IPreMigrationBackup
{
    /// <summary>
    /// Creates and verifies a backup. Must throw if the backup cannot be
    /// trusted; the migration is then not attempted.
    /// </summary>
    Task<string> CreateAsync(string fromMigration, CancellationToken ct);
}

/// <summary>
/// Brings the database schema up to this build on startup. Order matters
/// and is the whole point of this class: refuse downgrades, back up, and
/// only then migrate.
/// </summary>
public sealed class DatabaseMigrator(
    IMigrationOperations operations,
    IPreMigrationBackup backup,
    ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken ct)
    {
        var known = operations.Known;
        var applied = await operations.GetAppliedAsync(ct);

        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new DatabaseNewerThanServerException(unknown);
        }

        var pending = known.Except(applied, StringComparer.Ordinal).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        // A database with no applied migrations holds no data to protect.
        if (applied.Count > 0)
        {
            var location = await backup.CreateAsync(applied[^1], ct);
            logger.LogInformation("Metadata backup written to {Location} before migrating.", location);
        }

        logger.LogInformation("Applying {Count} database migration(s): {Migrations}.", pending.Count, string.Join(", ", pending));
        await operations.ApplyPendingAsync(ct);
    }
}
