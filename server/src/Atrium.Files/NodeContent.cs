using System.Buffers.Binary;
using System.Security.Cryptography;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Files.External;
using Atrium.Files.Storage;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>
/// A file's content, wherever it is: a blob on a storage location, or a
/// file in an external library's folder.
/// </summary>
/// <param name="Key">
/// Identifies the content, not the file: the blob's SHA-256 in a managed
/// library, or a hash of the path, size and modification time in an
/// external one. Used as the ETag and to name cached thumbnails, so a
/// changed file never shows an old thumbnail.
/// </param>
public sealed record ContentSource(
    long Size,
    string Key,
    DateTimeOffset ModifiedAt,
    Func<ByteRange?, CancellationToken, Task<Stream>> OpenAsync);

/// <summary>The content cannot be read now: an external file vanished, or its folder is not there.</summary>
public sealed class ContentUnavailableException(string message) : Exception(message);

public sealed class NodeContent(AtriumDbContext db, StorageProviders providers)
{
    /// <exception cref="LocationUnavailableException">The storage location is not reachable.</exception>
    /// <exception cref="ContentUnavailableException">The external file is gone.</exception>
    public async Task<ContentSource> SourceAsync(Node node, Library library, CancellationToken ct)
    {
        if (node.IsFolder)
        {
            throw new InvalidOperationException("A folder has no content.");
        }

        if (library.Mode == LibraryMode.External)
        {
            return External(node, library);
        }

        var blobId = node.BlobId ?? throw new ContentUnavailableException($"Node {node.Id} has no content.");
        var blob = await db.Blobs.AsNoTracking().SingleAsync(b => b.Id == blobId, ct);
        return await BlobSourceAsync(blob, node.ModifiedAt, ct);
    }

    public async Task<ContentSource> BlobSourceAsync(Blob blob, DateTimeOffset modifiedAt, CancellationToken ct)
    {
        var provider = await providers.OpenAsync(db, blob.StorageLocationId, ct);
        return new ContentSource(
            blob.Size,
            Convert.ToHexStringLower(blob.Sha256),
            modifiedAt,
            (range, token) => provider.OpenReadAsync(blob.Key, range, token));
    }

    /// <summary>The content key of an external file as the index last saw it, without touching the disk.</summary>
    public static string ExternalKey(Guid libraryId, string relativePath, long size, DateTimeOffset modifiedAt)
    {
        var path = ExternalFolder.HashPath(relativePath);
        Span<byte> buffer = stackalloc byte[16 + 32 + 8 + 8];
        libraryId.TryWriteBytes(buffer[..16]);
        path.CopyTo(buffer[16..48]);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[48..56], size);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[56..64], modifiedAt.ToUnixTimeMilliseconds());
        return "x" + Convert.ToHexStringLower(SHA256.HashData(buffer))[..63];
    }

    private static ContentSource External(Node node, Library library)
    {
        var folder = new ExternalFolder(library.ExternalPath ?? throw new InvalidOperationException("An external library without a folder."));
        var relative = node.ExternalPath ?? throw new ContentUnavailableException($"Node {node.Id} has no path.");

        // The disk, not the index, says what the file is now: it may have
        // changed since the last scan.
        var entry = folder.Stat(relative) ?? throw new ContentUnavailableException($"Node {node.Id} is no longer in its folder.");
        return new ContentSource(
            entry.Size,
            ExternalKey(library.Id, relative, entry.Size, entry.ModifiedAt),
            entry.ModifiedAt,
            (range, _) =>
            {
                Stream stream;
                try
                {
                    stream = folder.OpenRead(relative);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new ContentUnavailableException(e.Message);
                }

                if (range is { } r)
                {
                    stream.Seek(r.Offset, SeekOrigin.Begin);
                }

                return Task.FromResult(stream);
            });
    }
}
