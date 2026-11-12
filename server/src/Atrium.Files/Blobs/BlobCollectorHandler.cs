using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Files.Storage;
using Atrium.Jobs;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files.Blobs;

/// <summary>
/// The only code that deletes file content (DESIGN.md 4.3), in two passes
/// separated by a grace period:
/// <list type="number">
/// <item>blobs no node or version references are marked <c>PendingDelete</c>;</item>
/// <item>after the grace period, each is checked again and only then its
/// row and its content are deleted, row first.</item>
/// </list>
/// Deleting the row first means a node can never point at deleted content:
/// once the row is gone nothing can reference it (the foreign key refuses),
/// and while it exists the database refuses to delete it if something does.
/// A crash between the two leaves content without a row, which wastes space
/// but loses nothing, and is reused if the same content is uploaded again.
/// </summary>
public sealed class BlobCollectorHandler(
    AtriumDbContext db,
    StorageProviders providers,
    BlobLocks locks,
    TimeProvider time,
    FilesOptions options,
    ILogger<BlobCollectorHandler> logger) : IJobHandler
{
    public const string Type = "blobs.collect";

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromDays(1), JobPriority.Idle, JobResourceClass.Io);

    private const int BatchSize = 200;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // A blob that is referenced again (a restored file, a new upload of
        // the same content) is no longer a candidate.
        await db.Blobs
            .Where(b => b.State == BlobState.PendingDelete
                && (db.Nodes.Any(n => n.BlobId == b.Id) || db.NodeVersions.Any(v => v.BlobId == b.Id)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, BlobState.Stored)
                .SetProperty(b => b.PendingDeleteSince, (DateTimeOffset?)null), ct);

        // Pass 1: mark.
        await db.Blobs
            .Where(b => b.State == BlobState.Stored
                && !db.Nodes.Any(n => n.BlobId == b.Id)
                && !db.NodeVersions.Any(v => v.BlobId == b.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, BlobState.PendingDelete)
                .SetProperty(b => b.PendingDeleteSince, now), ct);

        // Pass 2: delete what has waited out the grace period.
        var cutoff = now - options.BlobGracePeriod;
        var unavailable = new HashSet<Guid>();
        var deleted = 0;
        var after = Guid.Empty;
        while (true)
        {
            // Keyset pagination: every candidate is visited once per run,
            // whatever happens to it.
            var last = after;
            var batch = await db.Blobs.AsNoTracking()
                .Where(b => b.State == BlobState.PendingDelete && b.PendingDeleteSince <= cutoff && b.Id.CompareTo(last) > 0)
                .OrderBy(b => b.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            after = batch[^1].Id;
            foreach (var blob in batch)
            {
                ct.ThrowIfCancellationRequested();
                if (unavailable.Contains(blob.StorageLocationId))
                {
                    continue;
                }

                IStorageProvider provider;
                try
                {
                    provider = await providers.OpenAsync(db, blob.StorageLocationId, ct);
                }
                catch (LocationUnavailableException e)
                {
                    // Never delete rows for a location that cannot be reached:
                    // the content would be left behind without its record.
                    logger.LogWarning("Skipping blob collection on location {LocationId}: {Code}.", e.LocationId, e.Code);
                    unavailable.Add(blob.StorageLocationId);
                    continue;
                }

                if (await DeleteAsync(provider, blob, cutoff, ct))
                {
                    deleted++;
                }
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} unreferenced blobs.", deleted);
        }

        if (unavailable.Count > 0)
        {
            throw new JobFailedException(StorageProviders.Unavailable, $"{unavailable.Count} storage locations could not be reached.");
        }
    }

    private async Task<bool> DeleteAsync(IStorageProvider provider, Blob blob, DateTimeOffset cutoff, CancellationToken ct)
    {
        using (await locks.AcquireAsync(blob.StorageLocationId, blob.Sha256, ct))
        {
            int removed;
            try
            {
                removed = await db.Blobs
                    .Where(b => b.Id == blob.Id
                        && b.State == BlobState.PendingDelete
                        && b.PendingDeleteSince <= cutoff
                        && !db.Nodes.Any(n => n.BlobId == b.Id)
                        && !db.NodeVersions.Any(v => v.BlobId == b.Id))
                    .ExecuteDeleteAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The foreign key refused: something references it after all.
                logger.LogInformation(e, "Blob {BlobId} is referenced again and is kept.", blob.Id);
                removed = 0;
            }

            if (removed == 0)
            {
                // Revived by an upload or referenced again; nothing to do now,
                // the next run marks it correctly.
                return false;
            }

            try
            {
                await provider.DeleteAsync(blob.Key, ct);
            }
            catch (StorageException e)
            {
                logger.LogWarning(e, "Content of deleted blob {BlobId} could not be removed from storage.", blob.Id);
            }

            return true;
        }
    }
}
