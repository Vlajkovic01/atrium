using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Data.Backup;
using Atrium.Files.Storage;
using Atrium.Jobs;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files.Backups;

public sealed record MetadataBackupPayload(string Reason);

/// <summary>
/// Takes a metadata backup and puts a copy into every storage location
/// (DESIGN.md 8.2), so a location that holds blobs also holds what is
/// needed to name them. Runs daily and on demand.
/// </summary>
public sealed class MetadataBackupHandler(
    AtriumDbContext db,
    IBackupDialect dialect,
    BackupOptions options,
    StorageProviders providers,
    TimeProvider time,
    ILogger<MetadataBackupHandler> logger) : IJobHandler
{
    public const string Type = "metadata.backup";
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
    public const string StoragePrefix = "atrium/backups/";

    /// <summary>How many backups of each reason are kept, locally and in each location.</summary>
    public const int Keep = 14;

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromDays(1), JobPriority.Background, JobResourceClass.Io);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var reason = context.Job.Payload is null ? Scheduled : context.GetPayload<MetadataBackupPayload>().Reason;
        if (reason is not (Scheduled or Manual))
        {
            throw new JobFailedException(JobErrorCodes.Unexpected, $"Unknown backup reason '{reason}'.", permanent: true);
        }

        var (file, manifest) = await BackupFiles.CreateAsync(db, dialect, options.Directory, reason, time.GetUtcNow(), ct);
        logger.LogInformation("Metadata backup {Name} written: {Tables} tables, {Rows} rows.",
            file.Name, manifest.Tables.Count, manifest.Tables.Sum(t => t.Rows));
        await context.ReportProgressAsync(0.5, ct);

        var failed = 0;
        var locations = await db.StorageLocations.AsNoTracking().Select(l => l.Id).ToListAsync(ct);
        foreach (var locationId in locations)
        {
            try
            {
                var provider = await providers.OpenAsync(db, locationId, ct);
                await using (var stream = File.OpenRead(file.Path))
                {
                    await provider.WriteAsync(StoragePrefix + file.Name, stream, overwrite: false, ct);
                }

                await PruneRemoteAsync(provider, reason, ct);
            }
            catch (Exception e) when (e is LocationUnavailableException or StorageException)
            {
                logger.LogWarning(e, "The backup could not be copied to storage location {LocationId}.", locationId);
                failed++;
            }
        }

        BackupFiles.Prune(options.Directory, reason, Keep);
        if (failed > 0)
        {
            // The local backup exists; the job is retried for the copies.
            throw new JobFailedException(StorageProviders.Unavailable, $"The backup could not be copied to {failed} storage locations.");
        }
    }

    private static async Task PruneRemoteAsync(IStorageProvider provider, string reason, CancellationToken ct)
    {
        var copies = new List<(string Key, DateTimeOffset Created)>();
        await foreach (var item in provider.ListAsync(StoragePrefix, ct))
        {
            if (BackupFiles.TryParseName(item.Key[StoragePrefix.Length..], out var created, out var itemReason) && itemReason == reason)
            {
                copies.Add((item.Key, created));
            }
        }

        foreach (var (key, _) in copies.OrderByDescending(c => c.Created).Skip(Keep))
        {
            await provider.DeleteAsync(key, ct);
        }
    }
}
