using Atrium.Core.Files;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>
/// The trash (DESIGN.md 4.2). Removing an item from the trash removes its
/// rows only; content is deleted later by the blob collector, after its
/// grace period, if nothing else uses it.
/// </summary>
public sealed class TrashService(AtriumDbContext db, NodeService nodes, TimeProvider time, FilesOptions options)
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

        var libraries = await WritableLibrariesAsync(actor, ct);
        var removed = await db.Nodes
            .Where(n => libraries.Contains(n.LibraryId) && n.TrashedAt != null)
            .ExecuteDeleteAsync(ct);
        return FileResult<int>.Success(removed);
    }

    /// <summary>Removes items whose retention ended, in every library. Run by the retention job.</summary>
    public static Task<int> ExpireAsync(AtriumDbContext db, DateTimeOffset now, TimeSpan retention, CancellationToken ct)
    {
        var cutoff = now - retention;
        return db.Nodes.Where(n => n.TrashedAt != null && n.TrashedAt < cutoff).ExecuteDeleteAsync(ct);
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

    private async Task<List<Guid>> ReadableLibrariesAsync(FileActor actor, CancellationToken ct)
    {
        var libraries = await db.Libraries.AsNoTracking().Where(l => l.OwnerUserId == actor.UserId).ToListAsync(ct);
        return libraries.Where(l => NodeService.CanRead(actor, l)).Select(l => l.Id).ToList();
    }

    private async Task<List<Guid>> WritableLibrariesAsync(FileActor actor, CancellationToken ct)
    {
        var libraries = await db.Libraries.AsNoTracking().Where(l => l.OwnerUserId == actor.UserId).ToListAsync(ct);
        return libraries.Where(l => NodeService.CanWrite(actor, l)).Select(l => l.Id).ToList();
    }
}
