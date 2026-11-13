using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Files.Blobs;
using Atrium.Files.Storage;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files.Uploads;

public static class UploadErrors
{
    public const string OffsetMismatch = "upload.offset_mismatch";
    public const string ChecksumMismatch = "upload.checksum_mismatch";
    public const string ContentMismatch = "upload.content_mismatch";
    public const string ChunkSize = "upload.invalid_chunk_size";
    public const string TooLarge = "upload.too_large";
    public const string NotActive = "upload.not_active";
    public const string Busy = "upload.busy";
    public const string Expired = "upload.expired";
}

public sealed record NewUpload(
    Guid ParentId,
    string Name,
    long Length,
    string? MediaType,
    DateTimeOffset? ModifiedAt,
    byte[]? Sha256,
    ConflictPolicy Policy);

/// <summary>A chunk the client sends, with the checksum it declared for it, if any.</summary>
public sealed record UploadChunk(long Offset, long Length, Stream Content, HashAlgorithmName? ChecksumAlgorithm, byte[]? Checksum);

/// <summary>
/// Resumable uploads (D-024, D-028). Every accepted chunk becomes one part
/// of a multipart upload on the storage location and is durable before the
/// offset moves, so a dropped connection or a server restart loses at most
/// the chunk that was in flight. When the last chunk arrives the parts are
/// assembled, verified as a blob and added to the folder.
/// </summary>
public sealed class UploadService(
    AtriumDbContext db,
    NodeService nodes,
    StorageProviders providers,
    BlobStore blobs,
    UploadRunningHashes hashes,
    TimeProvider time,
    ILogger<UploadService> logger)
{
    /// <summary>The largest upload accepted: what S3 allows for one object.</summary>
    public const long MaxLength = 5L * 1024 * 1024 * 1024 * 1024;

    public const long DefaultChunkSize = 16L * 1024 * 1024;

    /// <summary>Under S3's limit of 10,000 parts, with room for a client that sends smaller chunks.</summary>
    public const int MaxParts = 10_000;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Busy = new();

    public async Task<FileResult<Upload>> CreateAsync(FileActor actor, NewUpload request, CancellationToken ct)
    {
        if (request.Length is < 0 or > MaxLength)
        {
            return FileResult<Upload>.Fail(UploadErrors.TooLarge, new Dictionary<string, object?> { ["maxBytes"] = MaxLength });
        }

        var parent = await nodes.WritableFolderAsync(actor, request.ParentId, ct);
        if (!parent.Ok)
        {
            return FileResult<Upload>.Fail(parent.Error!, parent.Params);
        }

        if (!NodeName.TryNormalize(request.Name, out var name, out var key))
        {
            return FileResult<Upload>.Fail(FileErrors.InvalidName);
        }

        // Fail early on what would fail at the end anyway; both are checked
        // again when the upload finishes.
        var existing = await nodes.SiblingAsync(request.ParentId, key, except: null, ct);
        if (existing is not null && (request.Policy == ConflictPolicy.Fail || (request.Policy == ConflictPolicy.Replace && existing.IsFolder)))
        {
            return NodeService.Conflict<Upload>(existing);
        }

        var library = parent.Value!.Library;
        if (await nodes.QuotaRefusalAsync(library, request.Length, ct) is { } refusal)
        {
            return FileResult<Upload>.Fail(FileErrors.QuotaExceeded, refusal);
        }

        // Uploads stage into the library's storage location; an external
        // library has none and is written to differently.
        if (library.StorageLocationId is not { } locationId)
        {
            return FileResult<Upload>.Fail(FileErrors.ReadOnly);
        }

        IStorageProvider provider;
        try
        {
            provider = await providers.OpenAsync(db, locationId, ct);
        }
        catch (LocationUnavailableException e)
        {
            return FileResult<Upload>.Fail(e.Code);
        }

        var stagingKey = BlobStore.NewStagingKey();
        var multipart = await provider.BeginMultipartAsync(stagingKey, ct);
        var now = time.GetUtcNow();
        var upload = new Upload
        {
            UserId = actor.UserId,
            LibraryId = library.Id,
            ParentId = request.ParentId,
            StorageLocationId = locationId,
            Name = name,
            MediaType = request.MediaType,
            Length = request.Length,
            ChunkSize = ChunkSizeFor(request.Length, provider.Capabilities),
            ConflictPolicy = request.Policy.ToString(),
            ModifiedAt = request.ModifiedAt,
            ExpectedSha256 = request.Sha256,
            State = UploadState.Active,
            StagingKey = stagingKey,
            MultipartUploadId = multipart.UploadId,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        db.Uploads.Add(upload);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await provider.AbortMultipartAsync(multipart, CancellationToken.None);
            throw;
        }

        // An empty file has nothing to send: it is finished at once.
        if (request.Length == 0)
        {
            var finished = await FinishAsync(actor, upload, provider, [], ct);
            return finished.Ok ? FileResult<Upload>.Success(finished.Value!) : FileResult<Upload>.Fail(finished.Error!, finished.Params);
        }

        return FileResult<Upload>.Success(upload);
    }

    /// <summary>The caller's upload, whatever its state.</summary>
    public Task<Upload?> FindAsync(FileActor actor, Guid id, CancellationToken ct) =>
        db.Uploads.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.UserId == actor.UserId, ct);

    /// <summary>The caller's uploads still waiting for data, so a reloaded page can offer to resume them.</summary>
    public Task<List<Upload>> ListActiveAsync(FileActor actor, CancellationToken ct) =>
        db.Uploads.AsNoTracking()
            .Where(u => u.UserId == actor.UserId && u.State == UploadState.Active)
            .OrderBy(u => u.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Stores one chunk. The offset moves only after the chunk is durable on
    /// storage; a chunk that fails halfway is discarded and sent again.
    /// </summary>
    public async Task<FileResult<Upload>> AppendAsync(FileActor actor, Guid id, UploadChunk chunk, CancellationToken ct)
    {
        var gate = Busy.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
        {
            // One writer per upload: two connections sending the same chunk
            // would otherwise race for the same part number.
            return FileResult<Upload>.Fail(UploadErrors.Busy);
        }

        try
        {
            return await AppendLockedAsync(actor, id, chunk, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FileResult<bool>> CancelAsync(FileActor actor, Guid id, CancellationToken ct)
    {
        var upload = await db.Uploads.SingleOrDefaultAsync(u => u.Id == id && u.UserId == actor.UserId, ct);
        if (upload is null)
        {
            return FileResult<bool>.Fail(FileErrors.NotFound);
        }

        if (upload.State != UploadState.Active)
        {
            return FileResult<bool>.Fail(UploadErrors.NotActive);
        }

        await DiscardAsync(upload, UploadState.Cancelled, null, ct);
        return FileResult<bool>.Success(true);
    }

    /// <summary>
    /// Which of these SHA-256 hashes the caller already has in their own
    /// files, versions or trash. Never answers about other users' files
    /// (D-010): that would tell anyone whether a given file is on the server.
    /// </summary>
    public async Task<List<byte[]>> OwnedHashesAsync(FileActor actor, IReadOnlyCollection<byte[]> hashes, CancellationToken ct)
    {
        var libraries = await db.Libraries.AsNoTracking().Where(l => l.OwnerUserId == actor.UserId).ToListAsync(ct);
        var readable = libraries.Where(l => NodeService.CanRead(actor, l)).Select(l => l.Id).ToList();
        if (readable.Count == 0 || hashes.Count == 0)
        {
            return [];
        }

        var found = new List<byte[]>();
        foreach (var chunk in hashes.Chunk(200))
        {
            var list = chunk.ToList();
            var owned = await db.Blobs.AsNoTracking()
                .Where(b => list.Contains(b.Sha256)
                    && (db.Nodes.Any(n => n.BlobId == b.Id && readable.Contains(n.LibraryId))
                        || db.NodeVersions.Any(v => v.BlobId == b.Id && db.Nodes.Any(n => n.Id == v.NodeId && readable.Contains(n.LibraryId)))))
                .Select(b => b.Sha256)
                .ToListAsync(ct);
            found.AddRange(owned);
        }

        return found;
    }

    /// <summary>The chunk size for an upload: large enough to stay under the part limit, never below the provider's minimum.</summary>
    public static long ChunkSizeFor(long length, StorageCapabilities capabilities)
    {
        var forParts = (length + (MaxParts / 2) - 1) / (MaxParts / 2);
        var size = Math.Max(DefaultChunkSize, Math.Max(forParts, capabilities.MinimumPartSize));
        return Math.Min(size, capabilities.MaximumPartSize);
    }

    private async Task<FileResult<Upload>> AppendLockedAsync(FileActor actor, Guid id, UploadChunk chunk, CancellationToken ct)
    {
        var upload = await db.Uploads.SingleOrDefaultAsync(u => u.Id == id && u.UserId == actor.UserId, ct);
        if (upload is null)
        {
            return FileResult<Upload>.Fail(FileErrors.NotFound);
        }

        if (upload.State != UploadState.Active || upload.ExpiresAt <= time.GetUtcNow())
        {
            return FileResult<Upload>.Fail(UploadErrors.NotActive);
        }

        if (chunk.Offset != upload.Offset)
        {
            return FileResult<Upload>.Fail(UploadErrors.OffsetMismatch, new Dictionary<string, object?> { ["offset"] = upload.Offset });
        }

        IStorageProvider provider;
        try
        {
            provider = await providers.OpenAsync(db, upload.StorageLocationId, ct);
        }
        catch (LocationUnavailableException e)
        {
            return FileResult<Upload>.Fail(e.Code);
        }

        var parts = JsonSerializer.Deserialize<List<UploadedPart>>(upload.Parts, Json) ?? [];
        var isLast = upload.Offset + chunk.Length == upload.Length;
        if (chunk.Length <= 0
            || upload.Offset + chunk.Length > upload.Length
            || chunk.Length > upload.ChunkSize
            || (!isLast && chunk.Length < provider.Capabilities.MinimumPartSize)
            || parts.Count >= MaxParts)
        {
            return FileResult<Upload>.Fail(UploadErrors.ChunkSize, new Dictionary<string, object?>
            {
                ["chunkSize"] = upload.ChunkSize,
                ["minimumChunkSize"] = provider.Capabilities.MinimumPartSize,
            });
        }

        var multipart = new MultipartUpload(upload.StagingKey, upload.MultipartUploadId);
        var running = hashes.Continue(upload.Id, upload.Offset);
        using var checksum = chunk.ChecksumAlgorithm is { } algorithm ? IncrementalHash.CreateHash(algorithm) : null;
        UploadedPart part;
        await using (var tee = new TeeHashStream(chunk.Content, running, checksum))
        {
            try
            {
                part = await provider.UploadPartAsync(multipart, parts.Count + 1, tee, chunk.Length, ct);
            }
            catch
            {
                // What was hashed so far no longer matches the stored parts.
                hashes.Forget(upload.Id);
                throw;
            }
        }

        if (checksum is not null && !CryptographicOperations.FixedTimeEquals(checksum.GetHashAndReset(), chunk.Checksum))
        {
            // The part is stored but not counted: the next attempt at this
            // offset uploads the same part number again, which replaces it.
            hashes.Forget(upload.Id);
            return FileResult<Upload>.Fail(UploadErrors.ChecksumMismatch);
        }

        parts.Add(part);
        upload.Parts = JsonSerializer.Serialize(parts, Json);
        upload.Offset += part.Size;
        upload.UpdatedAt = time.GetUtcNow();
        upload.ExpiresAt = upload.UpdatedAt + Lifetime;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            hashes.Forget(upload.Id);
            return FileResult<Upload>.Fail(UploadErrors.Busy);
        }

        hashes.Advance(upload.Id, upload.Offset, running);
        return isLast ? await FinishAsync(actor, upload, provider, parts, ct) : FileResult<Upload>.Success(upload);
    }

    /// <summary>Assembles the parts, verifies the content as a blob, and adds the file to its folder.</summary>
    private async Task<FileResult<Upload>> FinishAsync(
        FileActor actor, Upload upload, IStorageProvider provider, List<UploadedPart> parts, CancellationToken ct)
    {
        var multipart = new MultipartUpload(upload.StagingKey, upload.MultipartUploadId);
        if (parts.Count == 0)
        {
            await provider.AbortMultipartAsync(multipart, ct);
            await provider.WriteAsync(upload.StagingKey, new MemoryStream([]), overwrite: true, ct);
        }
        else
        {
            await provider.CompleteMultipartAsync(multipart, parts, overwrite: false, ct);
        }

        // The hash computed while the chunks arrived, unless a restart lost
        // it; then the assembled object is read back.
        var sha256 = hashes.Take(upload.Id, upload.Length) ?? await HashStagedAsync(provider, upload.StagingKey, ct);

        Blob blob;
        try
        {
            blob = await blobs.PromoteAsync(upload.StorageLocationId, upload.StagingKey, sha256, upload.Length, upload.ExpectedSha256, ct);
        }
        catch (BlobHashMismatchException)
        {
            await MarkAsync(upload, UploadState.Failed, UploadErrors.ContentMismatch, ct);
            return FileResult<Upload>.Fail(UploadErrors.ContentMismatch);
        }

        var policy = Enum.Parse<ConflictPolicy>(upload.ConflictPolicy);
        var added = await nodes.AddFileAsync(actor, upload.ParentId, upload.Name, blob, upload.MediaType, upload.ModifiedAt, policy, ct);
        if (!added.Ok)
        {
            // The content is stored but belongs to no file; the collector
            // removes it after its grace period.
            await MarkAsync(upload, UploadState.Failed, added.Error, ct);
            return FileResult<Upload>.Fail(added.Error!, added.Params);
        }

        upload.NodeId = added.Value!.Id;
        await MarkAsync(upload, UploadState.Completed, null, ct);
        return FileResult<Upload>.Success(upload);
    }

    /// <summary>Records the end of an upload. Not cancellable: the outcome must not be lost once the work is done.</summary>
    private async Task MarkAsync(Upload upload, UploadState state, string? error, CancellationToken ct)
    {
        _ = ct;
        var now = time.GetUtcNow();
        var nodeId = upload.NodeId;
        await db.Uploads.Where(u => u.Id == upload.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.State, state)
            .SetProperty(u => u.ErrorCode, error)
            .SetProperty(u => u.NodeId, nodeId)
            .SetProperty(u => u.UpdatedAt, now), CancellationToken.None);
        upload.State = state;
        upload.ErrorCode = error;
        upload.UpdatedAt = now;
        hashes.Forget(upload.Id);
        Busy.TryRemove(upload.Id, out _);
    }

    /// <summary>Stops an upload and removes its parts. Used by cancel and by expiry.</summary>
    internal async Task DiscardAsync(Upload upload, UploadState state, string? error, CancellationToken ct)
    {
        try
        {
            var provider = await providers.OpenAsync(db, upload.StorageLocationId, ct);
            await provider.AbortMultipartAsync(new MultipartUpload(upload.StagingKey, upload.MultipartUploadId), ct);
        }
        catch (Exception e) when (e is LocationUnavailableException or StorageException)
        {
            // The row stays active and the cleanup job tries again later.
            logger.LogWarning(e, "Could not discard the parts of upload {UploadId}.", upload.Id);
            throw;
        }

        await MarkAsync(upload, state, error, ct);
    }

    private static async Task<byte[]> HashStagedAsync(IStorageProvider provider, string key, CancellationToken ct)
    {
        await using var stream = await provider.OpenReadAsync(key, null, ct);
        return await SHA256.HashDataAsync(stream, ct);
    }
}
