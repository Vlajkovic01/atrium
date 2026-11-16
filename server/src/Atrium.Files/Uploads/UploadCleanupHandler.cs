using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Files.Blobs;
using Atrium.Files.Storage;
using Atrium.Jobs;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files.Uploads;

/// <summary>
/// Removes what abandoned uploads leave behind: parts of uploads nobody
/// continued within their lifetime, records of finished uploads, and staged
/// objects older than a day that nothing is using. None of this is user
/// content yet: a file exists only once its upload finished.
/// </summary>
public sealed class UploadCleanupHandler(
    AtriumDbContext db,
    UploadService uploads,
    StorageProviders providers,
    TimeProvider time,
    ILogger<UploadCleanupHandler> logger) : IJobHandler
{
    public const string Type = "uploads.cleanup";

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromHours(1), JobPriority.Background, JobResourceClass.Io);

    /// <summary>Records of finished uploads are kept this long, so a client can still learn the outcome.</summary>
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(7);

    /// <summary>A staged object lives seconds while it is verified; one this old was left by a crash.</summary>
    public static readonly TimeSpan StagingAge = TimeSpan.FromDays(1);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var failures = 0;

        var expired = await db.Uploads.AsNoTracking()
            .Where(u => u.State == UploadState.Active && u.ExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var upload in expired)
        {
            try
            {
                await uploads.DiscardAsync(upload, UploadState.Cancelled, UploadErrors.Expired, ct);
            }
            catch (Exception e) when (e is LocationUnavailableException or StorageException)
            {
                failures++;
            }
        }

        var finishedBefore = now - FinishedRetention;
        await db.Uploads
            .Where(u => u.State != UploadState.Active && u.UpdatedAt < finishedBefore)
            .ExecuteDeleteAsync(ct);

        var stagedBefore = now - StagingAge;
        var locations = await db.StorageLocations.AsNoTracking().Select(l => l.Id).ToListAsync(ct);
        foreach (var locationId in locations)
        {
            IStorageProvider provider;
            try
            {
                provider = await providers.OpenAsync(db, locationId, ct);
            }
            catch (LocationUnavailableException)
            {
                failures++;
                continue;
            }

            await foreach (var staged in provider.ListAsync(BlobStore.StagingPrefix, ct))
            {
                if (staged.LastModified < stagedBefore)
                {
                    logger.LogInformation("Removing stale staged object {Key}.", staged.Key);
                    await provider.DeleteAsync(staged.Key, ct);
                }
            }
        }

        // Uploads into external libraries stage inside the library's own
        // folder, where only Atrium writes, and only while writes are on.
        var externals = await db.Libraries.AsNoTracking()
            .Where(l => l.Mode == LibraryMode.External && !l.ReadOnly)
            .ToListAsync(ct);
        foreach (var library in externals)
        {
            if (!Directory.Exists(External.ExternalWriter.StagingRoot(library)))
            {
                continue;
            }

            var staging = External.ExternalWriter.StagingProvider(library);
            await foreach (var staged in staging.ListAsync(BlobStore.StagingPrefix, ct))
            {
                if (staged.LastModified < stagedBefore)
                {
                    logger.LogInformation("Removing stale staged upload {Key} of library {Library}.", staged.Key, library.Id);
                    await staging.DeleteAsync(staged.Key, ct);
                }
            }
        }

        if (failures > 0)
        {
            throw new JobFailedException(StorageProviders.Unavailable, $"{failures} items could not be cleaned up; they are retried.");
        }
    }
}
