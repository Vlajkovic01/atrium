using Atrium.Core.Files;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>
/// Files and folders: browsing, creating, renaming, moving, copying and
/// moving to the trash. Every method checks the actor's access to the
/// library first; someone else's node answers "not found", the same as one
/// that does not exist.
/// </summary>
public sealed class NodeService(AtriumDbContext db, TimeProvider time, FilesOptions options)
{
    /// <summary>A folder copy larger than this is refused rather than run inside one request.</summary>
    public const int MaxCopyItems = 10_000;

    /// <summary>The node if the actor may see it, with what the actor may do.</summary>
    public async Task<AccessibleNode?> FindAsync(FileActor actor, Guid id, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, ct);

        // A file a scan no longer finds is kept for a while, but is not there for anyone.
        if (node is null || node.MissingSince is not null)
        {
            return null;
        }

        var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == node.LibraryId, ct);
        var grant = await GrantAsync(actor, library, ct);
        if (!CanRead(actor, library, grant))
        {
            return null;
        }

        var inTrash = node.TrashedAt is not null
            || (await NodeTree.AncestorsAsync(db, node, ct)).Any(a => a.TrashedAt is not null);
        return new AccessibleNode(node, library, CanWrite(actor, library, grant), inTrash);
    }

    /// <summary>The actor's grant on a library that is not personal; null for personal libraries.</summary>
    public Task<LibraryGrant?> GrantAsync(FileActor actor, Library library, CancellationToken ct) =>
        library.Kind == LibraryKind.Personal
            ? Task.FromResult<LibraryGrant?>(null)
            : db.LibraryGrants.AsNoTracking().SingleOrDefaultAsync(g => g.LibraryId == library.Id && g.UserId == actor.UserId, ct);

    /// <summary>
    /// Personal libraries: the owner only, and not from a device approved
    /// without access to them. Admins have no way in (D-004). Every other
    /// library: whoever has a grant, admins included only through one.
    /// </summary>
    public static bool CanRead(FileActor actor, Library library, LibraryGrant? grant = null) => library.Kind switch
    {
        LibraryKind.Personal => library.OwnerUserId == actor.UserId && !actor.HidePersonal,
        _ => grant is not null && grant.UserId == actor.UserId && grant.LibraryId == library.Id,
    };

    /// <summary>
    /// Writing also needs a session that may write, a library that is not
    /// read-only and, outside a personal library, a write grant. External
    /// libraries are only read for now.
    /// </summary>
    public static bool CanWrite(FileActor actor, Library library, LibraryGrant? grant = null) =>
        CanRead(actor, library, grant)
        && actor.CanWriteAnything
        && !library.ReadOnly
        && library.Mode == LibraryMode.Managed
        && (library.Kind == LibraryKind.Personal || grant?.Access == LibraryAccess.Write);

    /// <summary>The ancestors from the library root down to the node, the node excluded.</summary>
    public async Task<FileResult<List<Node>>> PathAsync(FileActor actor, Guid id, CancellationToken ct)
    {
        if (await FindAsync(actor, id, ct) is not { } found)
        {
            return FileResult<List<Node>>.Fail(FileErrors.NotFound);
        }

        var ancestors = await NodeTree.AncestorsAsync(db, found.Node, ct);
        ancestors.Reverse();
        return FileResult<List<Node>>.Success(ancestors);
    }

    /// <summary>A page of a folder's children, folders first, then by the chosen order.</summary>
    public async Task<FileResult<(List<Node> Items, int Total)>> ChildrenAsync(
        FileActor actor, Guid folderId, ChildSort sort, bool descending, NodeKind? kind, int offset, int limit, CancellationToken ct)
    {
        if (await FindAsync(actor, folderId, ct) is not { } found)
        {
            return FileResult<(List<Node>, int)>.Fail(FileErrors.NotFound);
        }

        if (!found.Node.IsFolder)
        {
            return FileResult<(List<Node>, int)>.Fail(FileErrors.NotAFolder);
        }

        // Inside a trashed folder everything is in the trash too, so the
        // trashed flag of the children themselves only matters at the top.
        var query = db.Nodes.AsNoTracking().Where(n => n.ParentId == folderId && n.MissingSince == null);
        if (!found.InTrash)
        {
            query = query.Where(n => n.TrashedAt == null);
        }

        if (kind is { } k)
        {
            query = query.Where(n => n.Kind == k);
        }

        var total = await query.CountAsync(ct);

        // Kind is stored by name: "Folder" sorts before "File" only in
        // descending text order, so folders first is spelled out.
        var ordered = query.OrderBy(n => n.Kind == NodeKind.Folder ? 0 : 1);
        ordered = (sort, descending) switch
        {
            (ChildSort.Modified, false) => ordered.ThenBy(n => n.ModifiedAt),
            (ChildSort.Modified, true) => ordered.ThenByDescending(n => n.ModifiedAt),
            (ChildSort.Size, false) => ordered.ThenBy(n => n.Size),
            (ChildSort.Size, true) => ordered.ThenByDescending(n => n.Size),
            (ChildSort.Type, false) => ordered.ThenBy(n => n.MediaType),
            (ChildSort.Type, true) => ordered.ThenByDescending(n => n.MediaType),
            (_, false) => ordered.ThenBy(n => n.NameKey),
            (_, true) => ordered.ThenByDescending(n => n.NameKey),
        };

        var items = await ordered.ThenBy(n => n.NameKey).ThenBy(n => n.Id).Skip(offset).Take(limit).ToListAsync(ct);
        return FileResult<(List<Node>, int)>.Success((items, total));
    }

    public async Task<FileResult<Node>> CreateFolderAsync(FileActor actor, Guid parentId, string? name, CancellationToken ct)
    {
        var parent = await WritableFolderAsync(actor, parentId, ct);
        if (!parent.Ok)
        {
            return FileResult<Node>.Fail(parent.Error!, parent.Params);
        }

        if (!NodeName.TryNormalize(name, out var normalized, out var key))
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }

        if (await SiblingAsync(parentId, key, except: null, ct) is { } existing)
        {
            return Conflict<Node>(existing);
        }

        var now = time.GetUtcNow();
        var folder = new Node
        {
            LibraryId = parent.Value!.Library.Id,
            ParentId = parentId,
            Name = normalized,
            NameKey = key,
            ActiveNameKey = key,
            Kind = NodeKind.Folder,
            ModifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = actor.UserId,
        };
        return await SaveNewAsync(folder, parentId, key, ct);
    }

    /// <summary>Rename, move, or both. <paramref name="expectedVersion"/> is the version the client last saw.</summary>
    public async Task<FileResult<Node>> UpdateAsync(
        FileActor actor, Guid id, int expectedVersion, string? newName, Guid? newParentId, CancellationToken ct)
    {
        if (await FindAsync(actor, id, ct) is not { } found)
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        if (!found.CanWrite)
        {
            return FileResult<Node>.Fail(FileErrors.ReadOnly);
        }

        if (found.Node.ParentId is null)
        {
            return FileResult<Node>.Fail(FileErrors.IsRoot);
        }

        if (found.InTrash)
        {
            return FileResult<Node>.Fail(FileErrors.InTrash);
        }

        var node = await db.Nodes.SingleAsync(n => n.Id == id, ct);
        if (node.Version != expectedVersion)
        {
            return FileResult<Node>.Fail(FileErrors.VersionConflict);
        }

        var name = node.Name;
        var key = node.NameKey;
        if (newName is not null && !NodeName.TryNormalize(newName, out name, out key))
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }

        var parentId = newParentId ?? node.ParentId ?? throw new InvalidOperationException("Checked above: not a root.");
        if (parentId != node.ParentId)
        {
            var target = await WritableFolderAsync(actor, parentId, ct);
            if (!target.Ok)
            {
                return FileResult<Node>.Fail(target.Error!, target.Params);
            }

            if (target.Value!.Library.Id != node.LibraryId)
            {
                return FileResult<Node>.Fail(FileErrors.CrossLibrary);
            }

            // A folder cannot go inside itself or anything below it.
            if (parentId == node.Id
                || (await NodeTree.AncestorsAsync(db, target.Value.Node, ct)).Any(a => a.Id == node.Id))
            {
                return FileResult<Node>.Fail(FileErrors.MoveIntoItself);
            }
        }

        if (await SiblingAsync(parentId, key, except: node.Id, ct) is { } existing)
        {
            return Conflict<Node>(existing);
        }

        node.Name = name;
        node.NameKey = key;
        node.ActiveNameKey = key;
        node.ParentId = parentId;
        node.UpdatedAt = time.GetUtcNow();
        return await SaveChangedAsync(node, parentId, key, ct);
    }

    /// <summary>
    /// Copies a file or a folder with everything in it. In a managed library
    /// no bytes are copied: the copies point at the same blobs. Counts toward
    /// the quota like any other file.
    /// </summary>
    public async Task<FileResult<Node>> CopyAsync(FileActor actor, Guid id, Guid targetParentId, string? newName, CancellationToken ct)
    {
        if (await FindAsync(actor, id, ct) is not { } found)
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        if (found.Node.ParentId is null)
        {
            return FileResult<Node>.Fail(FileErrors.IsRoot);
        }

        if (found.InTrash)
        {
            return FileResult<Node>.Fail(FileErrors.InTrash);
        }

        var target = await WritableFolderAsync(actor, targetParentId, ct);
        if (!target.Ok)
        {
            return FileResult<Node>.Fail(target.Error!, target.Params);
        }

        if (target.Value!.Library.Id != found.Library.Id)
        {
            return FileResult<Node>.Fail(FileErrors.CrossLibrary);
        }

        var source = found.Node;
        if (source.IsFolder
            && (targetParentId == source.Id
                || (await NodeTree.AncestorsAsync(db, target.Value.Node, ct)).Any(a => a.Id == source.Id)))
        {
            return FileResult<Node>.Fail(FileErrors.MoveIntoItself);
        }

        var descendants = source.IsFolder
            ? await NodeTree.DescendantsAsync(db, [source.Id], MaxCopyItems, ct)
            : [];
        if (descendants is null)
        {
            return FileResult<Node>.Fail(FileErrors.TooManyItems, new Dictionary<string, object?> { ["max"] = MaxCopyItems });
        }

        // Children already in the trash are not copied.
        var live = LiveSubtree(source.Id, descendants);
        var addedBytes = source.Size + live.Sum(n => n.Size);
        if (await QuotaRefusalAsync(found.Library, addedBytes, ct) is { } refusal)
        {
            return FileResult<Node>.Fail(FileErrors.QuotaExceeded, refusal);
        }

        var name = source.Name;
        var key = source.NameKey;
        if (newName is not null && !NodeName.TryNormalize(newName, out name, out key))
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }

        if (await SiblingAsync(targetParentId, key, except: null, ct) is { } existing)
        {
            if (newName is not null)
            {
                return Conflict<Node>(existing);
            }

            // Copying without a new name into the same folder: "Report (2).pdf".
            (name, key) = await FreeNameAsync(targetParentId, source.Name, ct);
        }

        var now = time.GetUtcNow();
        var map = new Dictionary<Guid, Guid>();
        Node Clone(Node original, Guid parentId, string cloneName, string cloneKey)
        {
            var clone = new Node
            {
                LibraryId = original.LibraryId,
                ParentId = parentId,
                Name = cloneName,
                NameKey = cloneKey,
                ActiveNameKey = cloneKey,
                Kind = original.Kind,
                BlobId = original.BlobId,
                Size = original.Size,
                MediaType = original.MediaType,
                ModifiedAt = original.ModifiedAt,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedByUserId = actor.UserId,
            };
            map[original.Id] = clone.Id;
            return clone;
        }

        var root = Clone(source, targetParentId, name, key);
        db.Nodes.Add(root);
        foreach (var node in live)
        {
            db.Nodes.Add(Clone(node, map[node.ParentId!.Value], node.Name, node.NameKey));
        }

        return await SaveNewAsync(root, targetParentId, key, ct);
    }

    /// <summary>
    /// Moves a node to the trash. Only the node itself is marked; what is
    /// below it is in the trash because it is. Nothing is deleted.
    /// </summary>
    public async Task<FileResult<Node>> TrashAsync(FileActor actor, Guid id, int? expectedVersion, CancellationToken ct)
    {
        if (await FindAsync(actor, id, ct) is not { } found)
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        if (!found.CanWrite)
        {
            return FileResult<Node>.Fail(FileErrors.ReadOnly);
        }

        if (found.Node.ParentId is null)
        {
            return FileResult<Node>.Fail(FileErrors.IsRoot);
        }

        if (found.InTrash)
        {
            return FileResult<Node>.Fail(FileErrors.InTrash);
        }

        var node = await db.Nodes.SingleAsync(n => n.Id == id, ct);
        if (expectedVersion is { } expected && node.Version != expected)
        {
            return FileResult<Node>.Fail(FileErrors.VersionConflict);
        }

        var now = time.GetUtcNow();
        node.TrashedAt = now;
        node.InTrash = true;
        node.ActiveNameKey = null;
        node.UpdatedAt = now;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FileResult<Node>.Fail(FileErrors.VersionConflict);
        }

        if (node.IsFolder)
        {
            await NodeTree.MarkInTrashBelowAsync(db, node.Id, inTrash: true, ct);
        }

        await transaction.CommitAsync(ct);
        return FileResult<Node>.Success(node);
    }

    /// <summary>
    /// Adds a file to a folder, or gives an existing file new content,
    /// according to <paramref name="policy"/>. Used when an upload finishes.
    /// </summary>
    public async Task<FileResult<Node>> AddFileAsync(
        FileActor actor,
        Guid parentId,
        string name,
        Blob blob,
        string? mediaType,
        DateTimeOffset? modifiedAt,
        ConflictPolicy policy,
        CancellationToken ct)
    {
        var parent = await WritableFolderAsync(actor, parentId, ct);
        if (!parent.Ok)
        {
            return FileResult<Node>.Fail(parent.Error!, parent.Params);
        }

        var library = parent.Value!.Library;
        if (blob.StorageLocationId != library.StorageLocationId)
        {
            throw new InvalidOperationException("The content is stored on a different location than the library.");
        }

        if (!NodeName.TryNormalize(name, out var normalized, out var key))
        {
            return FileResult<Node>.Fail(FileErrors.InvalidName);
        }

        var now = time.GetUtcNow();
        if (await SiblingAsync(parentId, key, except: null, ct) is { } existing)
        {
            switch (policy)
            {
                case ConflictPolicy.Replace when !existing.IsFolder:
                    return await ReplaceContentAsync(actor, library, existing.Id, blob, mediaType, modifiedAt ?? now, ct);
                case ConflictPolicy.Rename:
                    (normalized, key) = await FreeNameAsync(parentId, normalized, ct);
                    break;
                default:
                    return Conflict<Node>(existing);
            }
        }

        if (await QuotaRefusalAsync(library, blob.Size, ct) is { } refusal)
        {
            return FileResult<Node>.Fail(FileErrors.QuotaExceeded, refusal);
        }

        var file = new Node
        {
            LibraryId = library.Id,
            ParentId = parentId,
            Name = normalized,
            NameKey = key,
            ActiveNameKey = key,
            Kind = NodeKind.File,
            BlobId = blob.Id,
            Size = blob.Size,
            MediaType = mediaType,
            ModifiedAt = modifiedAt ?? now,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = actor.UserId,
        };
        return await SaveNewAsync(file, parentId, key, ct);
    }

    /// <summary>
    /// The quota check: null when <paramref name="addedBytes"/> fit, or the
    /// numbers to show when they do not. Counts what the user is charged
    /// for, including the trash and earlier versions (DESIGN.md 3.3).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>?> QuotaRefusalAsync(Library library, long addedBytes, CancellationToken ct)
    {
        if (library.OwnerUserId is not { } ownerId)
        {
            return null;
        }

        var quota = await db.Users.Where(u => u.Id == ownerId).Select(u => u.QuotaBytes).SingleAsync(ct);
        if (quota is not { } limit)
        {
            return null;
        }

        var used = (await UsageService.ForLibraryAsync(db, library.Id, ct)).UsedBytes;
        return used + addedBytes <= limit
            ? null
            : new Dictionary<string, object?> { ["quotaBytes"] = limit, ["usedBytes"] = used, ["neededBytes"] = addedBytes };
    }

    private async Task<FileResult<Node>> ReplaceContentAsync(
        FileActor actor, Library library, Guid nodeId, Blob blob, string? mediaType, DateTimeOffset modifiedAt, CancellationToken ct)
    {
        var node = await db.Nodes.SingleAsync(n => n.Id == nodeId, ct);
        var now = time.GetUtcNow();
        if (node.BlobId == blob.Id)
        {
            // Identical content: no new version, only the reported time changes.
            node.ModifiedAt = modifiedAt;
            node.UpdatedAt = now;
        }
        else
        {
            // The new content is charged; the old one stays charged as a version.
            if (await QuotaRefusalAsync(library, blob.Size, ct) is { } refusal)
            {
                return FileResult<Node>.Fail(FileErrors.QuotaExceeded, refusal);
            }

            if (node.BlobId is { } previous)
            {
                db.NodeVersions.Add(new NodeVersion
                {
                    NodeId = node.Id,
                    BlobId = previous,
                    Size = node.Size,
                    ModifiedAt = node.ModifiedAt,
                    CreatedAt = now,
                    CreatedByUserId = actor.UserId,
                });
            }

            node.BlobId = blob.Id;
            node.Size = blob.Size;
            node.MediaType = mediaType ?? node.MediaType;
            node.ModifiedAt = modifiedAt;
            node.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FileResult<Node>.Fail(FileErrors.VersionConflict);
        }

        await VersionService.TrimAsync(db, node.Id, options.MaxVersions, ct);
        return FileResult<Node>.Success(node);
    }

    /// <summary>A folder the actor can write into, not in the trash.</summary>
    internal async Task<FileResult<AccessibleNode>> WritableFolderAsync(FileActor actor, Guid id, CancellationToken ct)
    {
        if (await FindAsync(actor, id, ct) is not { } found)
        {
            return FileResult<AccessibleNode>.Fail(FileErrors.NotFound);
        }

        if (!found.Node.IsFolder)
        {
            return FileResult<AccessibleNode>.Fail(FileErrors.NotAFolder);
        }

        if (!found.CanWrite)
        {
            return FileResult<AccessibleNode>.Fail(FileErrors.ReadOnly);
        }

        return found.InTrash
            ? FileResult<AccessibleNode>.Fail(FileErrors.InTrash)
            : FileResult<AccessibleNode>.Success(found);
    }

    internal Task<Node?> SiblingAsync(Guid parentId, string key, Guid? except, CancellationToken ct) =>
        db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.ParentId == parentId && n.ActiveNameKey == key && n.Id != except, ct);

    /// <summary>The first of "Name (2)", "Name (3)" … that is free in the folder.</summary>
    internal async Task<(string Name, string Key)> FreeNameAsync(Guid parentId, string name, CancellationToken ct)
    {
        for (var number = 2; ; number++)
        {
            var candidate = NodeName.Numbered(name, number);
            var key = NodeName.KeyOf(candidate);
            if (await SiblingAsync(parentId, key, except: null, ct) is null)
            {
                return (candidate, key);
            }
        }
    }

    internal static FileResult<T> Conflict<T>(Node existing) =>
        FileResult<T>.Fail(FileErrors.NameConflict, new Dictionary<string, object?>
        {
            ["existingId"] = existing.Id,
            ["existingKind"] = existing.Kind.ToString(),
        });

    /// <summary>The live part of a subtree, parents before children; anything in or under a trashed node is left out.</summary>
    public static List<Node> LiveSubtree(Guid rootId, List<Node> descendants) => NodeTree.LiveSubtree(rootId, descendants);

    private async Task<FileResult<Node>> SaveNewAsync(Node node, Guid parentId, string key, CancellationToken ct)
    {
        if (db.Entry(node).State == EntityState.Detached)
        {
            db.Nodes.Add(node);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A parallel request took the name between the check and the insert.
            db.ChangeTracker.Clear();
            return await SiblingAsync(parentId, key, except: null, ct) is { } existing
                ? Conflict<Node>(existing)
                : throw new InvalidOperationException("Saving the node failed.");
        }

        db.ChangeTracker.Clear();
        return FileResult<Node>.Success(await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == node.Id, ct));
    }

    private async Task<FileResult<Node>> SaveChangedAsync(Node node, Guid parentId, string key, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FileResult<Node>.Fail(FileErrors.VersionConflict);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await SiblingAsync(parentId, key, except: node.Id, ct) is { } existing
                ? Conflict<Node>(existing)
                : throw new InvalidOperationException("Saving the node failed.");
        }

        return FileResult<Node>.Success(node);
    }
}
