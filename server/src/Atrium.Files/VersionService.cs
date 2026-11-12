using Atrium.Core.Files;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>Earlier contents of a file (D-019).</summary>
public sealed class VersionService(AtriumDbContext db, NodeService nodes, TimeProvider time)
{
    public async Task<FileResult<List<NodeVersion>>> ListAsync(FileActor actor, Guid nodeId, CancellationToken ct)
    {
        if (await nodes.FindAsync(actor, nodeId, ct) is not { } found)
        {
            return FileResult<List<NodeVersion>>.Fail(FileErrors.NotFound);
        }

        if (found.Node.IsFolder)
        {
            return FileResult<List<NodeVersion>>.Fail(FileErrors.NotAFile);
        }

        var versions = await db.NodeVersions.AsNoTracking()
            .Where(v => v.NodeId == nodeId)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .ToListAsync(ct);
        return FileResult<List<NodeVersion>>.Success(versions);
    }

    /// <summary>One version, if the actor may read the file.</summary>
    public async Task<FileResult<NodeVersion>> FindAsync(FileActor actor, Guid nodeId, Guid versionId, CancellationToken ct)
    {
        if (await nodes.FindAsync(actor, nodeId, ct) is null)
        {
            return FileResult<NodeVersion>.Fail(FileErrors.NotFound);
        }

        return await db.NodeVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.NodeId == nodeId, ct) is { } version
            ? FileResult<NodeVersion>.Success(version)
            : FileResult<NodeVersion>.Fail(FileErrors.NotFound);
    }

    /// <summary>
    /// Makes an earlier version current. The current content becomes a
    /// version in turn, so restoring never loses anything.
    /// </summary>
    public async Task<FileResult<Node>> RestoreAsync(FileActor actor, Guid nodeId, Guid versionId, CancellationToken ct)
    {
        if (await WritableFileAsync(actor, nodeId, ct) is { } refusal)
        {
            return FileResult<Node>.Fail(refusal);
        }

        var node = await db.Nodes.SingleAsync(n => n.Id == nodeId, ct);
        var version = await db.NodeVersions.SingleOrDefaultAsync(v => v.Id == versionId && v.NodeId == nodeId, ct);
        if (version is null)
        {
            return FileResult<Node>.Fail(FileErrors.NotFound);
        }

        var now = time.GetUtcNow();
        if (node.BlobId is { } current)
        {
            db.NodeVersions.Add(new NodeVersion
            {
                NodeId = node.Id,
                BlobId = current,
                Size = node.Size,
                ModifiedAt = node.ModifiedAt,
                CreatedAt = now,
                CreatedByUserId = actor.UserId,
            });
        }

        db.NodeVersions.Remove(version);
        node.BlobId = version.BlobId;
        node.Size = version.Size;
        node.ModifiedAt = version.ModifiedAt;
        node.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FileResult<Node>.Fail(FileErrors.VersionConflict);
        }

        return FileResult<Node>.Success(node);
    }

    /// <summary>Removes one version. Its content is deleted by the blob collector if nothing else uses it.</summary>
    public async Task<FileResult<bool>> DeleteAsync(FileActor actor, Guid nodeId, Guid versionId, CancellationToken ct)
    {
        if (await WritableFileAsync(actor, nodeId, ct) is { } refusal)
        {
            return FileResult<bool>.Fail(refusal);
        }

        var removed = await db.NodeVersions.Where(v => v.Id == versionId && v.NodeId == nodeId).ExecuteDeleteAsync(ct);
        return removed == 1 ? FileResult<bool>.Success(true) : FileResult<bool>.Fail(FileErrors.NotFound);
    }

    /// <summary>Keeps at most <paramref name="max"/> versions of the file, the newest ones.</summary>
    public static async Task TrimAsync(AtriumDbContext db, Guid nodeId, int max, CancellationToken ct)
    {
        var surplus = await db.NodeVersions
            .Where(v => v.NodeId == nodeId)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Skip(Math.Max(0, max))
            .Select(v => v.Id)
            .ToListAsync(ct);
        if (surplus.Count > 0)
        {
            await db.NodeVersions.Where(v => surplus.Contains(v.Id)).ExecuteDeleteAsync(ct);
        }
    }

    private async Task<string?> WritableFileAsync(FileActor actor, Guid nodeId, CancellationToken ct)
    {
        if (await nodes.FindAsync(actor, nodeId, ct) is not { } found)
        {
            return FileErrors.NotFound;
        }

        if (found.Node.IsFolder)
        {
            return FileErrors.NotAFile;
        }

        if (!found.CanWrite)
        {
            return FileErrors.ReadOnly;
        }

        return found.InTrash ? FileErrors.InTrash : null;
    }
}
