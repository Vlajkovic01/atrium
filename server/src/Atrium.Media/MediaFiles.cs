using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.External;
using Atrium.Files.Storage;
using Atrium.Storage.Local;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Media;

/// <summary>
/// A file on local disk that tools can open by path. A temporary copy is
/// deleted when disposed; a file that is the content itself is never
/// touched.
/// </summary>
public sealed class LocalMediaFile : IAsyncDisposable
{
    private readonly bool _temporary;

    public LocalMediaFile(string path, ContentSource source, bool temporary)
    {
        Path = path;
        Source = source;
        _temporary = temporary;
    }

    public string Path { get; }

    public ContentSource Source { get; }

    public ValueTask DisposeAsync()
    {
        if (_temporary)
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Gives media tools a path to read: the blob itself on a local location,
/// the file itself in an external folder, or a temporary copy in the cache
/// for content on S3. Nothing is ever written to the content.
/// </summary>
public sealed class MediaFiles(AtriumDbContext db, NodeContent content, StorageProviders providers, MediaOptions options)
{
    public async Task<LocalMediaFile> OpenAsync(Node node, Library library, CancellationToken ct)
    {
        var source = await content.SourceAsync(node, library, ct);
        if (library.Mode == LibraryMode.External)
        {
            var path = new ExternalFolder(library.ExternalPath!).Resolve(node.ExternalPath!)
                ?? throw new ContentUnavailableException($"Node {node.Id} is not inside its folder.");
            return new LocalMediaFile(path, source, temporary: false);
        }

        var blob = await db.Blobs.AsNoTracking().SingleAsync(b => b.Id == node.BlobId, ct);
        var provider = await providers.OpenAsync(db, blob.StorageLocationId, ct);
        if (provider is LocalStorageProvider local)
        {
            return new LocalMediaFile(local.LocalPathOf(blob.Key), source, temporary: false);
        }

        // On S3 the tools get a copy; it lives only while they run.
        Directory.CreateDirectory(options.TempDir);
        var copy = System.IO.Path.Combine(options.TempDir, $"{Guid.NewGuid():N}{System.IO.Path.GetExtension(node.Name)}");
        await using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        await using (var stream = await source.OpenAsync(null, ct))
        {
            await stream.CopyToAsync(target, ct);
        }

        return new LocalMediaFile(copy, source, temporary: true);
    }
}
