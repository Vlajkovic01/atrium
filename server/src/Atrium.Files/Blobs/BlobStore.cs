using System.Security.Cryptography;
using Atrium.Core.Files;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Files.Storage;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files.Blobs;

/// <summary>The content did not match the SHA-256 the client declared. Nothing was stored.</summary>
public sealed class BlobHashMismatchException() : Exception("The content does not match the declared SHA-256.");

/// <summary>
/// Puts content into a managed storage location as a verified,
/// content-addressed blob (DESIGN.md 4.1). A blob row exists only for
/// content whose hash was computed from what the storage holds.
/// </summary>
public sealed class BlobStore(
    AtriumDbContext db,
    StorageProviders providers,
    BlobLocks locks,
    TimeProvider time,
    ILogger<BlobStore> logger)
{
    public const string StagingPrefix = "atrium/staging/";

    /// <summary>A fresh key under the staging area. Never a blob key.</summary>
    public static string NewStagingKey() => StagingPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Stores a stream as a blob: written to staging, hashed, verified, then
    /// moved into place or discarded if the same content is already stored.
    /// </summary>
    /// <exception cref="BlobHashMismatchException">The content differs from <paramref name="expectedSha256"/>.</exception>
    /// <exception cref="LocationUnavailableException">The location cannot be used.</exception>
    public async Task<Blob> StoreAsync(Guid locationId, Stream content, byte[]? expectedSha256, CancellationToken ct)
    {
        var provider = await providers.OpenAsync(db, locationId, ct);
        var stagingKey = NewStagingKey();
        byte[] hash;
        long size;
        try
        {
            using var hashing = new HashingStream(content);
            var written = await provider.WriteAsync(stagingKey, hashing, overwrite: false, ct);
            hash = hashing.GetHash();
            size = hashing.BytesRead;
            if (written.Size != size)
            {
                throw new StorageException($"The storage reports {written.Size} bytes, but {size} were sent.");
            }
        }
        catch
        {
            await DiscardAsync(provider, stagingKey);
            throw;
        }

        return await PromoteAsync(locationId, stagingKey, hash, size, expectedSha256, ct);
    }

    /// <summary>
    /// Turns staged content into a blob. <paramref name="sha256"/> is the hash
    /// computed while the content was written. On local disks and network
    /// shares the staged file is read back and hashed again, which also
    /// proves what reached the disk; on S3 each part was already verified by
    /// the service on arrival. The staged object is gone afterwards, whatever
    /// the outcome.
    /// </summary>
    public async Task<Blob> PromoteAsync(
        Guid locationId, string stagingKey, byte[] sha256, long size, byte[]? expectedSha256, CancellationToken ct)
    {
        if (!stagingKey.StartsWith(StagingPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only staged content can be promoted.", nameof(stagingKey));
        }

        var provider = await providers.OpenAsync(db, locationId, ct);
        try
        {
            var kind = await db.StorageLocations.Where(l => l.Id == locationId).Select(l => l.Kind).SingleAsync(ct);
            if (kind != StorageProviderKind.S3)
            {
                var (readBack, readSize) = await HashObjectAsync(provider, stagingKey, ct);
                if (readSize != size || !CryptographicOperations.FixedTimeEquals(readBack, sha256))
                {
                    throw new StorageException("The staged content read back differs from what was written.");
                }
            }

            if (expectedSha256 is not null && !CryptographicOperations.FixedTimeEquals(expectedSha256, sha256))
            {
                throw new BlobHashMismatchException();
            }

            using (await locks.AcquireAsync(locationId, sha256, ct))
            {
                return await PromoteLockedAsync(provider, locationId, stagingKey, sha256, size, ct);
            }
        }
        finally
        {
            await DiscardAsync(provider, stagingKey);
        }
    }

    private async Task<Blob> PromoteLockedAsync(
        IStorageProvider provider, Guid locationId, string stagingKey, byte[] sha256, long size, CancellationToken ct)
    {
        var key = Blob.KeyFor(sha256);
        var existing = await db.Blobs.AsNoTracking()
            .SingleOrDefaultAsync(b => b.StorageLocationId == locationId && b.Sha256 == sha256, ct);

        if (existing is not null)
        {
            if (existing.Size != size)
            {
                throw new StorageException($"Blob {existing.Id} has size {existing.Size}, but the same hash arrived with {size} bytes.");
            }

            // The row says the content is there. Check, and repair from the
            // verified staged copy if someone removed it from storage.
            if (await provider.StatAsync(key, ct) is not { } stored || stored.Size != size)
            {
                logger.LogWarning("Blob {BlobId} was missing or damaged on storage and is restored from a new upload.", existing.Id);
                await provider.MoveAsync(stagingKey, key, overwrite: true, ct);
            }

            if (existing.State == BlobState.PendingDelete)
            {
                await db.Blobs
                    .Where(b => b.Id == existing.Id && b.State == BlobState.PendingDelete)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.State, BlobState.Stored)
                        .SetProperty(b => b.PendingDeleteSince, (DateTimeOffset?)null), ct);
            }

            return await db.Blobs.AsNoTracking().SingleAsync(b => b.Id == existing.Id, ct);
        }

        try
        {
            await provider.MoveAsync(stagingKey, key, overwrite: false, ct);
        }
        catch (StorageObjectExistsException)
        {
            // Content with this name is on storage but unknown to the
            // database: left by a crash between move and insert, or by a
            // restored backup. Trust it only after hashing it.
            var (actual, actualSize) = await HashObjectAsync(provider, key, ct);
            if (actualSize != size || !CryptographicOperations.FixedTimeEquals(actual, sha256))
            {
                logger.LogWarning("An unknown object at {Key} did not match its name and is replaced.", key);
                await provider.MoveAsync(stagingKey, key, overwrite: true, ct);
            }
        }

        var blob = new Blob
        {
            StorageLocationId = locationId,
            Sha256 = sha256,
            Size = size,
            State = BlobState.Stored,
            CreatedAt = time.GetUtcNow(),
        };
        db.Blobs.Add(blob);
        await db.SaveChangesAsync(ct);
        db.Entry(blob).State = EntityState.Detached;
        return blob;
    }

    private static async Task<(byte[] Hash, long Size)> HashObjectAsync(IStorageProvider provider, string key, CancellationToken ct)
    {
        await using var stream = await provider.OpenReadAsync(key, null, ct);
        using var hashing = new HashingStream(stream);
        var buffer = new byte[81920];
        while (await hashing.ReadAsync(buffer, ct) > 0)
        {
        }

        return (hashing.GetHash(), hashing.BytesRead);
    }

    private async Task DiscardAsync(IStorageProvider provider, string stagingKey)
    {
        try
        {
            // Not cancellable: leftovers in staging are only wasted space, but
            // there is no reason to leave them when the request is abandoned.
            await provider.DeleteAsync(stagingKey, CancellationToken.None);
        }
        catch (StorageException e)
        {
            // The upload cleanup job removes stale staging objects later.
            logger.LogWarning(e, "Could not remove staged object {Key}.", stagingKey);
        }
    }
}
