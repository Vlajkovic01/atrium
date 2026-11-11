using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Atrium.Storage.Internal;

namespace Atrium.Storage.Local;

/// <summary>
/// Stores objects as files under one root directory. Also used for mounted
/// network shares, which look the same to the operating system.
/// </summary>
/// <remarks>
/// Every write goes to a temporary file inside the root (so on the same
/// filesystem), is flushed to disk, and is then renamed into place. A crash
/// at any point leaves either the old content or the new, never a mix.
/// </remarks>
public sealed class LocalStorageProvider : IStorageProvider
{
    private const int BufferSize = 128 * 1024;

    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly string _tempRoot;

    public LocalStorageProvider(string rootDirectory)
    {
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The storage root must be an absolute path.", nameof(rootDirectory));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _rootWithSeparator = _root + Path.DirectorySeparatorChar;
        _tempRoot = Path.Combine(_root, StorageKey.ReservedPrefix);
    }

    public StorageCapabilities Capabilities { get; } =
        new(AtomicMove: true, ReportsFreeSpace: true, MinimumPartSize: 1, MaximumPartSize: long.MaxValue);

    public Task<Stream> OpenReadAsync(string key, ByteRange? range, CancellationToken ct)
    {
        var path = Resolve(key);
        FileStream file;
        try
        {
            // Other readers, and a delete or move while reading, are allowed.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.Asynchronous);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new StorageObjectNotFoundException(key);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            // The key names a folder of other objects, not an object.
            throw new StorageObjectNotFoundException(key);
        }

        if (range is not { } r)
        {
            return Task.FromResult<Stream>(file);
        }

        if (r.Offset < 0 || r.Length < 0 || r.Offset > file.Length)
        {
            file.Dispose();
            throw new ArgumentOutOfRangeException(nameof(range), "The requested range is outside the object.");
        }

        file.Seek(r.Offset, SeekOrigin.Begin);
        var available = file.Length - r.Offset;
        return Task.FromResult<Stream>(new BoundedReadStream(file, Math.Min(r.Length ?? available, available)));
    }

    public async Task<StorageObject> WriteAsync(string key, Stream content, bool overwrite, CancellationToken ct)
    {
        var destination = Resolve(key);
        RefuseFolderKey(destination);
        if (!overwrite && File.Exists(destination))
        {
            throw new StorageObjectExistsException(key);
        }

        var temp = NewTempFile();
        try
        {
            await using (var file = CreateForWrite(temp))
            {
                await content.CopyToAsync(file, BufferSize, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }

            MoveIntoPlace(temp, destination, key, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }

        return Describe(key, destination);
    }

    public Task<MultipartUpload> BeginMultipartAsync(string key, CancellationToken ct)
    {
        RefuseFolderKey(Resolve(key));
        var uploadId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(PartDirectory(uploadId));
        return Task.FromResult(new MultipartUpload(key, uploadId));
    }

    public async Task<UploadedPart> UploadPartAsync(
        MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        var directory = PartDirectory(upload.UploadId);
        if (!Directory.Exists(directory))
        {
            throw new StorageException("The multipart upload does not exist or was aborted.");
        }

        // Written under another name first, so a part interrupted halfway is
        // never mistaken for a complete one.
        var final = Path.Combine(directory, PartFileName(partNumber));
        var temp = final + ".partial";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        await using (var file = CreateForWrite(temp, FileMode.Create))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
            }

            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
        }

        if (written != length)
        {
            File.Delete(temp);
            throw new StorageException(
                $"Part {partNumber} was announced as {length} bytes but {written} arrived.");
        }

        File.Move(temp, final, overwrite: true);
        return new UploadedPart(partNumber, Convert.ToHexStringLower(hash.GetHashAndReset()), written);
    }

    public async Task<StorageObject> CompleteMultipartAsync(
        MultipartUpload upload, IReadOnlyList<UploadedPart> parts, bool overwrite, CancellationToken ct)
    {
        var destination = Resolve(upload.Key);
        RefuseFolderKey(destination);
        if (!overwrite && File.Exists(destination))
        {
            throw new StorageObjectExistsException(upload.Key);
        }

        var directory = PartDirectory(upload.UploadId);
        var temp = NewTempFile();
        try
        {
            await using (var file = CreateForWrite(temp))
            {
                foreach (var part in parts.OrderBy(p => p.PartNumber))
                {
                    var partPath = Path.Combine(directory, PartFileName(part.PartNumber));
                    if (!File.Exists(partPath) || new FileInfo(partPath).Length != part.Size)
                    {
                        throw new StorageException($"Part {part.PartNumber} is missing or has the wrong size.");
                    }

                    await using var source = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                    await source.CopyToAsync(file, BufferSize, ct);
                }

                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }

            MoveIntoPlace(temp, destination, upload.Key, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }

        // Only after the object is safely in place are the parts removed.
        Directory.Delete(directory, recursive: true);
        return Describe(upload.Key, destination);
    }

    public Task AbortMultipartAsync(MultipartUpload upload, CancellationToken ct)
    {
        var directory = PartDirectory(upload.UploadId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StorageObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        StorageKey.ValidatePrefix(prefix);
        RequireRoot();

        // Start from the deepest directory the prefix names; the rest of the
        // prefix is matched against each key.
        var lastSlash = prefix.LastIndexOf('/');
        var startKey = lastSlash < 0 ? string.Empty : prefix[..lastSlash];
        var start = startKey.Length == 0 ? _root : Path.Combine(_root, startKey.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(start))
        {
            yield break;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None };
        foreach (var file in new DirectoryInfo(start).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            var key = Path.GetRelativePath(_root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(StorageKey.ReservedPrefix + "/", StringComparison.OrdinalIgnoreCase)
                || !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new StorageObject(key, file.Length, file.LastWriteTimeUtc);
        }

        await Task.CompletedTask;
    }

    public Task<StorageObject?> StatAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        return Task.FromResult(File.Exists(path) ? Describe(key, path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing there: deleting is idempotent.
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            // The key names a folder of other objects. There is no object
            // with this key to delete, and the folder is left alone.
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct)
    {
        var source = Resolve(sourceKey);
        var destination = Resolve(destinationKey);
        if (!File.Exists(source))
        {
            throw new StorageObjectNotFoundException(sourceKey);
        }

        RefuseFolderKey(destination);
        MoveIntoPlace(source, destination, destinationKey, overwrite);
        return Task.CompletedTask;
    }

    public async Task<StorageHealth> CheckAsync(CancellationToken ct)
    {
        var probeKey = StorageProbe.NewKey();
        try
        {
            if (!await StorageProbe.RoundTripAsync(this, probeKey, ct))
            {
                return StorageHealth.Failed(StorageProbe.MismatchCode, null);
            }

            return StorageHealth.Healthy(new DriveInfo(_root).AvailableFreeSpace);
        }
        catch (StorageUnavailableException e)
        {
            return StorageHealth.Failed(StorageHealthCodes.RootMissing, e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            return StorageHealth.Failed(StorageHealthCodes.AccessDenied, e.Message);
        }
        catch (Exception e) when (e is IOException or StorageException or ArgumentException)
        {
            return StorageHealth.Failed(StorageHealthCodes.Unavailable, e.Message);
        }
        finally
        {
            try
            {
                File.Delete(Path.Combine(_root, probeKey));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The probe could not be removed; the result already says what failed.
            }
        }
    }

    /// <summary>
    /// Turns a key into a path and proves the path is inside the root. The
    /// key is validated first; the containment check is a second, independent
    /// barrier in case a platform interprets some character unexpectedly.
    /// </summary>
    private string Resolve(string key)
    {
        StorageKey.Validate(key);
        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidStorageKeyException(key, "outside the storage root");
        }

        RequireRoot();
        return path;
    }

    /// <summary>
    /// The root must already exist; this class never creates it. If a
    /// network share is not mounted, its mount point is an empty directory
    /// or missing: creating folders there would silently store files on the
    /// wrong disk and report the location as healthy.
    /// </summary>
    private void RequireRoot()
    {
        if (!Directory.Exists(_root))
        {
            throw new StorageUnavailableException(
                "The storage root directory does not exist. If it is a network share or an external disk, it may not be mounted.");
        }
    }

    /// <summary>
    /// On a filesystem a name is either a file or a folder. A key that is
    /// already the folder of other objects cannot also be an object.
    /// </summary>
    private static void RefuseFolderKey(string path)
    {
        if (Directory.Exists(path))
        {
            throw new StorageException("The key is already used as the folder of other objects.");
        }
    }

    private void MoveIntoPlace(string source, string destination, string destinationKey, bool overwrite)
    {
        var directory = Path.GetDirectoryName(destination)!;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (IOException e)
        {
            // Part of the key's path is an existing object, not a folder.
            throw new StorageException("Part of the key's path is already used by an object.", e);
        }

        try
        {
            File.Move(source, destination, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(destination))
        {
            // Someone created the destination between the check and the move.
            throw new StorageObjectExistsException(destinationKey);
        }

        DirectorySync.Flush(directory);
    }

    private string NewTempFile()
    {
        Directory.CreateDirectory(_tempRoot);
        return Path.Combine(_tempRoot, $"write-{Guid.NewGuid():N}");
    }

    private string PartDirectory(string uploadId)
    {
        // The id comes back from persisted state. Accept only what this
        // class itself generates, so it can never name another directory.
        if (!Guid.TryParseExact(uploadId, "N", out _))
        {
            throw new StorageException("The multipart upload id is not valid.");
        }

        return Path.Combine(_tempRoot, $"multipart-{uploadId}");
    }

    private static string PartFileName(int partNumber) => partNumber.ToString("D6", CultureInfo.InvariantCulture);

    private static FileStream CreateForWrite(string path, FileMode mode = FileMode.CreateNew) =>
        new(path, mode, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);

    private static StorageObject Describe(string key, string path)
    {
        var info = new FileInfo(path);
        return new StorageObject(key, info.Length, info.LastWriteTimeUtc);
    }
}
