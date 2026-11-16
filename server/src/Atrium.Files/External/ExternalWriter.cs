using System.Collections.Concurrent;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Storage.Local;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files.External;

/// <summary>One lock per external library: a scan and a change never interleave in one folder.</summary>
public sealed class ExternalLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid libraryId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(libraryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

/// <summary>
/// Changes the files of an external library whose admin enabled writes
/// (DESIGN.md 4.4). This class and nothing else writes into such a folder.
/// </summary>
/// <remarks>
/// Each change is made on disk first, then in the index, under the
/// library's lock. If the index cannot be updated, the disk change is
/// undone. A crash between the two leaves the file safe where the disk
/// change put it; the next scan finds it there. Deleting never deletes: the
/// file moves into <c>.atrium-trash/&lt;id&gt;/</c> inside the library, on the
/// same disk, and is removed only when its trash retention ends, and only
/// while writes are still enabled.
/// </remarks>
public sealed class ExternalWriter(AtriumDbContext db, ExternalLocks locks, TimeProvider time)
{
    public const string UploadsFolder = ".uploads";

    /// <summary>Where uploads into this library are assembled: inside its trash folder, on the same disk, so finishing is a rename.</summary>
    public static string StagingRoot(Library library) =>
        Path.Combine(library.ExternalPath!, ExternalFolder.TrashFolderName, UploadsFolder);

    /// <summary>
    /// Proves the folder can be written by creating Atrium's own trash and
    /// staging folders in it. Fails for a read-only mount.
    /// </summary>
    public static bool TryPrepare(Library library)
    {
        try
        {
            Directory.CreateDirectory(StagingRoot(library));
            var probe = Path.Combine(StagingRoot(library), $".probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static LocalStorageProvider StagingProvider(Library library)
    {
        Directory.CreateDirectory(StagingRoot(library));
        return new LocalStorageProvider(StagingRoot(library));
    }

    public async Task<FileResult<Node>> CreateFolderAsync(Library library, Node parent, string name, Guid userId, CancellationToken ct)
    {
        using var held = await locks.AcquireAsync(library.Id, ct);
        if (!NodeName.TryNormalize(name, out var normalized, out var key))
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }

        var relative = Join(parent.ExternalPath!, normalized);
        if (await ConflictAsync(library, parent.Id, key, relative, except: null, ct) is { } conflict)
        {
            return conflict;
        }

        var full = Folder(library).Resolve(relative) ?? throw new InvalidOperationException("A checked name left the folder.");
        Directory.CreateDirectory(full);
        var now = time.GetUtcNow();
        await ClearMissingAtAsync(library.Id, relative, ct);
        var node = NewNode(library, parent.Id, normalized, relative, NodeKind.Folder, 0, null, now, userId, now);
        db.Nodes.Add(node);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            TryDeleteEmptyFolder(full);
            throw;
        }

        return FileResult<Node>.Success(node);
    }

    /// <summary>Renames and moves within the library. A folder takes everything in it along.</summary>
    public async Task<FileResult<Node>> MoveAsync(Library library, Guid nodeId, Node newParent, string newName, CancellationToken ct)
    {
        using var held = await locks.AcquireAsync(library.Id, ct);
        var node = await db.Nodes.SingleAsync(n => n.Id == nodeId, ct);

        // A name found on disk is kept as it is when the item only moves,
        // even if Atrium would not let anyone choose it.
        string normalized;
        string key;
        if (string.Equals(newName, node.Name, StringComparison.Ordinal))
        {
            (normalized, key) = (node.Name, node.NameKey);
        }
        else if (!NodeName.TryNormalize(newName, out normalized, out key))
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }
        var oldRelative = node.ExternalPath!;
        var newRelative = Join(newParent.ExternalPath!, normalized);
        if (string.Equals(oldRelative, newRelative, StringComparison.Ordinal))
        {
            return FileResult<Node>.Success(node);
        }

        // Only a change of case on a case-insensitive disk targets the same entry.
        var sameEntry = string.Equals(oldRelative, newRelative, StringComparison.OrdinalIgnoreCase);
        if (!sameEntry && await ConflictAsync(library, newParent.Id, key, newRelative, except: node.Id, ct) is { } conflict)
        {
            return conflict;
        }

        var folder = Folder(library);
        var from = folder.Resolve(oldRelative);
        var to = folder.Resolve(newRelative);
        if (from is null || to is null || !Exists(from))
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        MoveOnDisk(from, to, node.IsFolder);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            node.ParentId = newParent.Id;
            node.Name = normalized;
            node.NameKey = key;
            node.ActiveNameKey = normalized;
            node.UpdatedAt = time.GetUtcNow();
            await RepathAsync(node, newRelative, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            MoveOnDisk(to, from, node.IsFolder);
            throw;
        }

        return FileResult<Node>.Success(node);
    }

    /// <summary>Moves a file or folder into the library's own trash folder. Nothing is deleted.</summary>
    public async Task<FileResult<Node>> TrashAsync(Library library, Guid nodeId, CancellationToken ct)
    {
        using var held = await locks.AcquireAsync(library.Id, ct);
        var node = await db.Nodes.SingleAsync(n => n.Id == nodeId, ct);
        var result = await TrashLockedAsync(library, node, ct);
        return result;
    }

    /// <summary>Puts a trashed item back at its original place, or under <paramref name="parent"/>.</summary>
    public async Task<FileResult<Node>> RestoreAsync(
        Library library, Guid nodeId, Node parent, ConflictPolicy policy, CancellationToken ct)
    {
        using var held = await locks.AcquireAsync(library.Id, ct);
        var node = await db.Nodes.SingleAsync(n => n.Id == nodeId, ct);
        var name = node.Name;
        var key = node.NameKey;
        var target = Join(parent.ExternalPath!, name);
        if (await ConflictAsync(library, parent.Id, key, target, except: node.Id, ct) is { } conflict)
        {
            if (policy != ConflictPolicy.Rename)
            {
                return conflict;
            }

            (name, key, target) = await FreeNameAsync(library, parent, node.Name, ct);
        }

        var folder = Folder(library);
        var from = folder.Resolve(node.ExternalPath!) ?? throw new InvalidOperationException("A trashed item outside its folder.");
        var to = folder.Resolve(target) ?? throw new InvalidOperationException("A checked name left the folder.");
        if (!Exists(from))
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        MoveOnDisk(from, to, node.IsFolder);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            node.ParentId = parent.Id;
            node.Name = name;
            node.NameKey = key;
            node.ActiveNameKey = name;
            node.TrashedAt = null;
            node.InTrash = false;
            node.UpdatedAt = time.GetUtcNow();
            await RepathAsync(node, target, ct);
            await db.SaveChangesAsync(ct);
            if (node.IsFolder)
            {
                await NodeTreeInTrash(node.Id, inTrash: false, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            MoveOnDisk(to, from, node.IsFolder);
            throw;
        }

        TryDeleteEmptyFolder(Path.GetDirectoryName(from)!);
        return FileResult<Node>.Success(node);
    }

    /// <summary>
    /// Deletes trashed items for good: first from disk, then from the index.
    /// A crash in between leaves rows pointing at nothing, which the next
    /// attempt removes.
    /// </summary>
    public async Task<int> PurgeAsync(Library library, IReadOnlyList<Guid> trashedIds, CancellationToken ct)
    {
        using var held = await locks.AcquireAsync(library.Id, ct);
        var folder = Folder(library);
        var removed = 0;
        foreach (var id in trashedIds)
        {
            var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id && n.TrashedAt != null, ct);
            if (node?.ExternalPath is not { } path || !IsInTrashFolder(path))
            {
                continue;
            }

            // .atrium-trash/<id>/<name>: the whole <id> folder goes.
            var holder = folder.Resolve(path[..path.LastIndexOf('/')]);
            if (holder is not null && Directory.Exists(holder))
            {
                Directory.Delete(holder, recursive: true);
            }

            removed += await db.Nodes.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
        }

        return removed;
    }

    /// <summary>
    /// Puts an uploaded file, assembled and verified in the staging folder,
    /// into its folder. "Replace" moves the existing file to the trash first.
    /// </summary>
    public async Task<FileResult<Node>> PlaceUploadAsync(
        Library library,
        Node parent,
        string name,
        string stagedPath,
        long size,
        string? mediaType,
        DateTimeOffset? modifiedAt,
        ConflictPolicy policy,
        Guid userId,
        CancellationToken ct)
    {
        using var held = await locks.AcquireAsync(library.Id, ct);
        if (!NodeName.TryNormalize(name, out var normalized, out var key))
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }

        var relative = Join(parent.ExternalPath!, normalized);
        if (await ConflictAsync(library, parent.Id, key, relative, except: null, ct) is { } conflict)
        {
            var existing = await db.Nodes.SingleOrDefaultAsync(
                n => n.ParentId == parent.Id && n.NameKey == key && !n.InTrash && n.MissingSince == null, ct);
            switch (policy)
            {
                case ConflictPolicy.Replace when existing is { IsFolder: false }:
                    var trashed = await TrashLockedAsync(library, existing, ct);
                    if (!trashed.Ok)
                    {
                        return trashed;
                    }

                    break;
                case ConflictPolicy.Rename:
                    (normalized, key, relative) = await FreeNameAsync(library, parent, normalized, ct);
                    break;
                default:
                    return conflict;
            }
        }

        var target = Folder(library).Resolve(relative) ?? throw new InvalidOperationException("A checked name left the folder.");
        File.Move(stagedPath, target, overwrite: false);
        var now = time.GetUtcNow();
        await ClearMissingAtAsync(library.Id, relative, ct);
        if (modifiedAt is { } when)
        {
            File.SetLastWriteTimeUtc(target, when.UtcDateTime);
        }

        var written = new FileInfo(target);
        var node = NewNode(
            library, parent.Id, normalized, relative, NodeKind.File, size, mediaType,
            new DateTimeOffset(written.LastWriteTimeUtc, TimeSpan.Zero), userId, now);
        db.Nodes.Add(node);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // The file stays where it is; the next scan indexes it.
            db.ChangeTracker.Clear();
            throw;
        }

        return FileResult<Node>.Success(node);
    }

    public static bool IsInTrashFolder(string relativePath) =>
        relativePath.StartsWith(ExternalFolder.TrashFolderName + "/", StringComparison.Ordinal);

    private async Task<FileResult<Node>> TrashLockedAsync(Library library, Node node, CancellationToken ct)
    {
        var folder = Folder(library);
        var from = folder.Resolve(node.ExternalPath!);
        if (from is null || !Exists(from))
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        // Every trashed item gets its own folder, so names never collide there.
        var target = $"{ExternalFolder.TrashFolderName}/{node.Id:N}/{Path.GetFileName(from)}";
        var holder = Path.Combine(library.ExternalPath!, ExternalFolder.TrashFolderName, node.Id.ToString("N"));
        Directory.CreateDirectory(holder);
        var to = Path.Combine(holder, Path.GetFileName(from));
        MoveOnDisk(from, to, node.IsFolder);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = time.GetUtcNow();
            node.TrashedAt = now;
            node.InTrash = true;
            node.ActiveNameKey = null;
            node.UpdatedAt = now;
            await RepathAsync(node, target, ct);
            await db.SaveChangesAsync(ct);
            if (node.IsFolder)
            {
                await NodeTreeInTrash(node.Id, inTrash: true, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            MoveOnDisk(to, from, node.IsFolder);
            TryDeleteEmptyFolder(holder);
            throw;
        }

        return FileResult<Node>.Success(node);
    }

    private Task NodeTreeInTrash(Guid id, bool inTrash, CancellationToken ct) => NodeTree.MarkInTrashBelowAsync(db, id, inTrash, ct);

    /// <summary>
    /// Index entries of files that vanished from where something new now
    /// goes. Nothing is on disk there (that was checked), so they are only
    /// history, and would block the new entry's path and name.
    /// </summary>
    private Task<int> ClearMissingAtAsync(Guid libraryId, string relative, CancellationToken ct)
    {
        var below = relative + "/";
        return db.Nodes
            .Where(n => n.LibraryId == libraryId && n.MissingSince != null
                && (n.ExternalPath == relative || n.ExternalPath!.StartsWith(below)))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Gives the node and, for a folder, everything below it their new paths.</summary>
    private async Task RepathAsync(Node node, string newRelative, CancellationToken ct)
    {
        await ClearMissingAtAsync(node.LibraryId, newRelative, ct);
        var oldPrefix = node.ExternalPath + "/";
        node.ExternalPath = newRelative;
        node.ExternalPathHash = ExternalFolder.HashPath(newRelative);
        if (!node.IsFolder)
        {
            return;
        }

        var below = await NodeTree.DescendantsAsync(db, [node.Id], int.MaxValue, ct) ?? [];
        foreach (var child in below)
        {
            if (child.ExternalPath is { } path && path.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                var moved = newRelative + "/" + path[oldPrefix.Length..];
                var hash = ExternalFolder.HashPath(moved);
                var id = child.Id;
                await db.Nodes.Where(n => n.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.ExternalPath, moved).SetProperty(n => n.ExternalPathHash, hash), ct);
            }
        }
    }

    /// <summary>A live sibling with the same name ignoring case, or anything already at the path on disk.</summary>
    private async Task<FileResult<Node>?> ConflictAsync(
        Library library, Guid parentId, string key, string relative, Guid? except, CancellationToken ct)
    {
        var sibling = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(
            n => n.ParentId == parentId && n.NameKey == key && n.Id != except && !n.InTrash && n.MissingSince == null, ct);
        if (sibling is not null)
        {
            return NodeService.Conflict<Node>(sibling);
        }

        var full = Folder(library).Resolve(relative);
        if (full is null)
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }

        return Exists(full)
            ? FileResult<Node>.Fail(FileErrors.NameConflict, new Dictionary<string, object?> { ["existingId"] = null, ["existingKind"] = null })
            : null;
    }

    private async Task<(string Name, string Key, string Relative)> FreeNameAsync(Library library, Node parent, string name, CancellationToken ct)
    {
        for (var number = 2; ; number++)
        {
            var candidate = NodeName.Numbered(name, number);
            var key = NodeName.KeyOf(candidate);
            var relative = Join(parent.ExternalPath!, candidate);
            if (await ConflictAsync(library, parent.Id, key, relative, except: null, ct) is null)
            {
                return (candidate, key, relative);
            }
        }
    }

    private static Node NewNode(
        Library library, Guid parentId, string name, string relative, NodeKind kind, long size, string? mediaType, DateTimeOffset modifiedAt, Guid userId, DateTimeOffset now)
    {
        return new Node
        {
            LibraryId = library.Id,
            ParentId = parentId,
            Name = name,
            NameKey = NodeName.KeyOf(name),
            ActiveNameKey = name,
            Kind = kind,
            Size = size,
            MediaType = mediaType,
            ModifiedAt = modifiedAt,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId,
            ExternalPath = relative,
            ExternalPathHash = ExternalFolder.HashPath(relative),
            LastSeenAt = now,
        };
    }

    private static ExternalFolder Folder(Library library) => new(library.ExternalPath!);

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MoveOnDisk(string from, string to, bool isFolder)
    {
        if (isFolder)
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite: false);
        }
    }

    private static void TryDeleteEmptyFolder(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
