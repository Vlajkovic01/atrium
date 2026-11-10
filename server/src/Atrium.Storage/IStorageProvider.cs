namespace Atrium.Storage;

/// <summary>A part of an object: <paramref name="Length"/> bytes from <paramref name="Offset"/>, or to the end when null.</summary>
public readonly record struct ByteRange(long Offset, long? Length);

public sealed record StorageObject(string Key, long Size, DateTimeOffset LastModified);

/// <param name="AtomicMove">A move either fully happens or does not. False on S3, where it is copy then delete.</param>
/// <param name="ReportsFreeSpace">The health check can say how much space is left.</param>
/// <param name="MinimumPartSize">Smallest size of every multipart part except the last.</param>
/// <param name="MaximumPartSize">Largest part the provider accepts in one call.</param>
public sealed record StorageCapabilities(bool AtomicMove, bool ReportsFreeSpace, long MinimumPartSize, long MaximumPartSize);

public sealed record StorageHealth(bool Ok, long? FreeBytes, string? ErrorCode, string? Detail)
{
    public static StorageHealth Healthy(long? freeBytes) => new(true, freeBytes, null, null);

    public static StorageHealth Failed(string errorCode, string? detail) => new(false, null, errorCode, detail);
}

/// <summary>An upload in progress. Both values are persisted so the upload can resume after a restart.</summary>
public sealed record MultipartUpload(string Key, string UploadId);

public sealed record UploadedPart(int PartNumber, string ETag, long Size);

public class StorageException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class StorageObjectNotFoundException(string key)
    : StorageException($"Object '{key}' does not exist.")
{
    public string Key { get; } = key;
}

public sealed class StorageObjectExistsException(string key)
    : StorageException($"Object '{key}' already exists and overwriting was not requested.")
{
    public string Key { get; } = key;
}

/// <summary>
/// One place where bytes are stored. Keys are relative, forward-slash
/// paths validated by <see cref="StorageKey"/>; providers never see
/// user-supplied file names for managed libraries, only blob keys.
/// </summary>
/// <remarks>
/// Rules every implementation follows, checked by one shared test suite:
/// a write is all or nothing; an existing object is never replaced unless
/// the caller asks; delete is idempotent; nothing outside the location's
/// root can be reached through a key.
/// </remarks>
public interface IStorageProvider
{
    StorageCapabilities Capabilities { get; }

    /// <summary>Streamed read, whole or a range. The caller disposes the stream.</summary>
    /// <exception cref="StorageObjectNotFoundException">No such object.</exception>
    Task<Stream> OpenReadAsync(string key, ByteRange? range, CancellationToken ct);

    /// <summary>
    /// Streamed write. Afterwards the key holds the complete new content,
    /// or, on any failure, whatever it held before.
    /// </summary>
    /// <exception cref="StorageObjectExistsException">The key exists and <paramref name="overwrite"/> is false.</exception>
    Task<StorageObject> WriteAsync(string key, Stream content, bool overwrite, CancellationToken ct);

    Task<MultipartUpload> BeginMultipartAsync(string key, CancellationToken ct);

    /// <summary>Stores one part. Uploading the same part number again replaces it, which is how a failed part is retried.</summary>
    Task<UploadedPart> UploadPartAsync(MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken ct);

    /// <summary>Assembles the parts into the object. Nothing is visible under the key before this returns.</summary>
    /// <exception cref="StorageObjectExistsException">The key exists and <paramref name="overwrite"/> is false.</exception>
    Task<StorageObject> CompleteMultipartAsync(
        MultipartUpload upload, IReadOnlyList<UploadedPart> parts, bool overwrite, CancellationToken ct);

    /// <summary>Discards an upload and its parts. Safe to call for an upload that is already gone.</summary>
    Task AbortMultipartAsync(MultipartUpload upload, CancellationToken ct);

    /// <summary>All objects whose key starts with the prefix. Order is not guaranteed.</summary>
    IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct);

    Task<StorageObject?> StatAsync(string key, CancellationToken ct);

    /// <summary>Deletes the object. Does nothing if it does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <exception cref="StorageObjectNotFoundException">The source does not exist.</exception>
    /// <exception cref="StorageObjectExistsException">The destination exists and <paramref name="overwrite"/> is false.</exception>
    Task MoveAsync(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct);

    /// <summary>
    /// Proves the location works by writing, reading back and deleting a
    /// probe object. Never throws: a failure is the result.
    /// </summary>
    Task<StorageHealth> CheckAsync(CancellationToken ct);
}
