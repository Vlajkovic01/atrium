using Atrium.Core.Files;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>
/// The trash (DESIGN.md 4.2). Removing an item from the trash removes its
/// rows only; content is deleted later by the blob collector, after its
/// grace period, if nothing else uses it.
/// </summary>
public sealed class TrashService(AtriumDbContext db, NodeService nodes, External.ExternalWriter external, TimeProvider time, FilesOptions options)
{
    /// <summary>Items the user moved to the trash, newest first, with what removing each would free.</summary>
    public async Task<List<TrashItem>> ListAsync(FileActor actor, CancellationToken ct)
    {
        var libraries = await ReadableLibrariesAsync(actor, ct);
        var trashed = await db.Nodes.AsNoTracking()
            .Where(n => libraries.Contains(n.LibraryId) && n.TrashedAt != null)
            .OrderByDescending(n => n.TrashedAt)
            .ToListAsync(ct);

        var result = new List<TrashItem>(trashed.Count);
        foreach (var node in trashed)
        {
            var (bytes, count) = await MeasureAsync(node, ct);
            var location = await NodeTree.AncestorsAsync(db, node, ct);
            location.Reverse();
            result.Add(new TrashItem(node, bytes, count, node.TrashedAt!.Value + options.TrashRetention, location));
        }

        return result;
    }

    /// <summary>
    /// Puts an item back where it was, or into <paramref name="targetParentId"/>.
    /// If the original folder is itself in the trash, a target is required.
    /// </summary>
    public async Task<FileResult<Node>> RestoreAsync(
        FileActor actor, Guid id, Guid? targetParentId, ConflictPolicy policy, CancellationToken ct)
    {
        var trashed = await TrashedNodeAsync(actor, id, ct);
        if (!trashed.Ok)
        {
            return trashed;
        }

        var node = await db.Nodes.SingleAsync(n => n.Id == id, ct);
        var parentId = targetParentId ?? node.ParentId!.Value;
        var parent = await nodes.WritableFolderAsync(actor, parentId, ct);
        if (!parent.Ok)
        {
            // The original folder is in the trash too: the client asks where to put it.
            return FileResult<Node>.Fail(parent.Error!, parent.Params);
        }

        if (parent.Value!.Library.Id != node.LibraryId)
        {
            return FileResult<Node>.Fail(FileErrors.CrossLibrary);
        }

        if (node.IsFolder && (await NodeTree.AncestorsAsync(db, parent.Value.Node, ct)).Any(a => a.Id == node.Id))
        {
            return FileResult<Node>.Fail(FileErrors.MoveIntoItself);
        }

        if (parent.Value.Library.Mode == LibraryMode.External)
        {
            db.ChangeTracker.Clear();
            return await external.RestoreAsync(parent.Value.Library, id, parent.Value.Node, policy, ct);
        }

        var name = node.Name;
        var key = node.NameKey;
        if (await nodes.SiblingAsync(parentId, key, except: node.Id, ct) is { } existing)
        {
            if (policy != ConflictPolicy.Rename)
            {
                return NodeService.Conflict<Node>(existing);
            }

            (name, key) = await nodes.FreeNameAsync(parentId, node.Name, ct);
        }

        node.ParentId = parentId;
        node.Name = name;
        node.NameKey = key;
        node.ActiveNameKey = key;
        node.TrashedAt = null;
        node.InTrash = false;
        node.UpdatedAt = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await nodes.SiblingAsync(parentId, key, except: id, ct) is { } taken
                ? NodeService.Conflict<Node>(taken)
                : FileResult<Node>.Fail(FileErrors.VersionConflict);
        }

        if (node.IsFolder)
        {
            await NodeTree.MarkInTrashBelowAsync(db, node.Id, inTrash: false, ct);
        }

        await transaction.CommitAsync(ct);
        return FileResult<Node>.Success(node);
    }

    /// <summary>Removes one item from the trash for good, with everything below it.</summary>
    public async Task<FileResult<bool>> PurgeAsync(FileActor actor, Guid id, CancellationToken ct)
    {
        var trashed = await TrashedNodeAsync(actor, id, ct);
        if (!trashed.Ok)
        {
            return FileResult<bool>.Fail(trashed.Error!);
        }

        var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == trashed.Value!.LibraryId, ct);
        if (library.Mode == LibraryMode.External)
        {
            await external.PurgeAsync(library, [id], ct);
            return FileResult<bool>.Success(true);
        }

        // The database removes the subtree and its versions (cascade).
        await db.Nodes.Where(n => n.Id == id && n.TrashedAt != null).ExecuteDeleteAsync(ct);
        return FileResult<bool>.Success(true);
    }

    /// <summary>Empties the user's trash. Returns how many items were removed.</summary>
    public async Task<FileResult<int>> EmptyAsync(FileActor actor, CancellationToken ct)
    {
        if (!actor.CanWriteAnything)
        {
            return FileResult<int>.Fail(FileErrors.ReadOnly);
        }

        var removed = 0;
        foreach (var library in await WritableLibrariesAsync(actor, ct))
        {
            if (library.Mode == LibraryMode.External)
            {
                var ids = await db.Nodes.Where(n => n.LibraryId == library.Id && n.TrashedAt != null).Select(n => n.Id).ToListAsync(ct);
                removed += await external.PurgeAsync(library, ids, ct);
            }
            else
            {
                removed += await db.Nodes.Where(n => n.LibraryId == library.Id && n.TrashedAt != null).ExecuteDeleteAsync(ct);
            }
        }

        return FileResult<int>.Success(removed);
    }

    /// <summary>
    /// Removes items whose retention ended in managed libraries: rows only,
    /// content follows through the blob collector. External libraries are
    /// handled by <see cref="ExpireExternalAsync"/>.
    /// </summary>
    public static Task<int> ExpireAsync(AtriumDbContext db, DateTimeOffset now, TimeSpan retention, CancellationToken ct)
    {
        var cutoff = now - retention;
        return db.Nodes
            .Where(n => n.TrashedAt != null && n.TrashedAt < cutoff
                && db.Libraries.Any(l => l.Id == n.LibraryId && l.Mode == LibraryMode.Managed))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes from disk what was in an external library's trash for its
    /// whole retention, but only while the library still allows changes: an
    /// admin who switched writes off stops every change, this one included.
    /// </summary>
    public static async Task<int> ExpireExternalAsync(
        AtriumDbContext db, External.ExternalWriter external, DateTimeOffset now, TimeSpan retention, CancellationToken ct)
    {
        var cutoff = now - retention;
        var libraries = await db.Libraries.AsNoTracking()
            .Where(l => l.Mode == LibraryMode.External && !l.ReadOnly)
            .ToListAsync(ct);
        var removed = 0;
        foreach (var library in libraries)
        {
            var ids = await db.Nodes
                .Where(n => n.LibraryId == library.Id && n.TrashedAt != null && n.TrashedAt < cutoff)
                .Select(n => n.Id)
                .ToListAsync(ct);
            if (ids.Count > 0)
            {
                removed += await external.PurgeAsync(library, ids, ct);
            }
        }

        return removed;
    }

    private async Task<FileResult<Node>> TrashedNodeAsync(FileActor actor, Guid id, CancellationToken ct)
    {
        if (await nodes.FindAsync(actor, id, ct) is not { } found)
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        if (found.Node.TrashedAt is null)
        {
            return FileResult<Node>.Fail(FileErrors.NotInTrash);
        }

        return found.CanWrite ? FileResult<Node>.Success(found.Node) : FileResult<Node>.Fail(FileErrors.ReadOnly);
    }

    private async Task<(long Bytes, int Count)> MeasureAsync(Node node, CancellationToken ct)
    {
        if (!node.IsFolder)
        {
            return (node.Size, 1);
        }

        var below = await NodeTree.DescendantsAsync(db, [node.Id], int.MaxValue, ct) ?? [];
        return (below.Where(n => !n.IsFolder).Sum(n => n.Size), below.Count + 1);
    }

    /// <summary>
    /// The trash shows the user's personal library, and the external
    /// libraries the user may change: what someone deleted there is in its
    /// trash for everyone who can change it.
    /// </summary>
    private async Task<List<Guid>> ReadableLibrariesAsync(FileActor actor, CancellationToken ct)
    {
        var personal = await db.Libraries.AsNoTracking().Where(l => l.OwnerUserId == actor.UserId).ToListAsync(ct);
        var result = personal.Where(l => NodeService.CanRead(actor, l)).Select(l => l.Id).ToList();
        result.AddRange((await WritableLibrariesAsync(actor, ct)).Where(l => l.Mode == LibraryMode.External).Select(l => l.Id));
        return result;
    }

    private async Task<List<Library>> WritableLibrariesAsync(FileActor actor, CancellationToken ct)
    {
        var personal = await db.Libraries.AsNoTracking().Where(l => l.OwnerUserId == actor.UserId).ToListAsync(ct);
        var granted = await db.LibraryGrants.AsNoTracking()
            .Where(g => g.UserId == actor.UserId)
            .Join(db.Libraries.AsNoTracking(), g => g.LibraryId, l => l.Id, (g, l) => new { Grant = g, Library = l })
            .ToListAsync(ct);
        return personal.Where(l => NodeService.CanWrite(actor, l))
            .Concat(granted.Where(x => NodeService.CanWrite(actor, x.Library, x.Grant)).Select(x => x.Library))
            .ToList();
    }
}
