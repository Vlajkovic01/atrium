using Atrium.Core.Files;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>
/// What a user is charged for: the logical size of their files, including
/// the trash and earlier versions. Deduplication never changes it
/// (DESIGN.md 3.3).
/// </summary>
public sealed class UsageService(AtriumDbContext db)
{
    public async Task<UsageSummary> ForUserAsync(Guid userId, CancellationToken ct)
    {
        var quota = await db.Users.Where(u => u.Id == userId).Select(u => u.QuotaBytes).SingleOrDefaultAsync(ct);
        var libraryId = await db.Libraries.Where(l => l.OwnerUserId == userId).Select(l => (Guid?)l.Id).SingleOrDefaultAsync(ct);
        if (libraryId is null)
        {
            return new UsageSummary(0, 0, 0, 0, quota);
        }

        return await ForLibraryAsync(db, libraryId.Value, ct) with { QuotaBytes = quota };
    }

    /// <summary>
    /// What each of these people uses, as <see cref="UsageSummary.UsedBytes"/>
    /// would say, in three queries whatever their number: for the list of
    /// people an admin sees.
    /// </summary>
    public static async Task<Dictionary<Guid, long>> UsedByOwnersAsync(AtriumDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var owned = await db.Libraries.AsNoTracking()
            .Where(l => l.OwnerUserId != null && userIds.Contains(l.OwnerUserId.Value))
            .Select(l => new { l.Id, Owner = l.OwnerUserId!.Value })
            .ToListAsync(ct);
        var libraryIds = owned.Select(o => o.Id).ToList();
        var files = await db.Nodes.AsNoTracking()
            .Where(n => libraryIds.Contains(n.LibraryId) && n.Kind == NodeKind.File)
            .GroupBy(n => n.LibraryId)
            .Select(g => new { Library = g.Key, Bytes = g.Sum(n => n.Size) })
            .ToDictionaryAsync(x => x.Library, x => x.Bytes, ct);
        var versions = await db.NodeVersions.AsNoTracking()
            .Join(db.Nodes, v => v.NodeId, n => n.Id, (v, n) => new { n.LibraryId, v.Size })
            .Where(x => libraryIds.Contains(x.LibraryId))
            .GroupBy(x => x.LibraryId)
            .Select(g => new { Library = g.Key, Bytes = g.Sum(x => x.Size) })
            .ToDictionaryAsync(x => x.Library, x => x.Bytes, ct);

        return owned.ToDictionary(
            o => o.Owner,
            o => files.GetValueOrDefault(o.Id) + versions.GetValueOrDefault(o.Id));
    }

    public static async Task<UsageSummary> ForLibraryAsync(AtriumDbContext db, Guid libraryId, CancellationToken ct)
    {
        var all = await db.Nodes
            .Where(n => n.LibraryId == libraryId && n.Kind == NodeKind.File)
            .SumAsync(n => (long?)n.Size, ct) ?? 0;

        var trashedRoots = await db.Nodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId && n.TrashedAt != null)
            .ToListAsync(ct);
        var trash = trashedRoots.Where(n => !n.IsFolder).Sum(n => n.Size);
        var trashedFolders = trashedRoots.Where(n => n.IsFolder).Select(n => n.Id).ToList();
        if (trashedFolders.Count > 0)
        {
            var below = await NodeTree.DescendantsAsync(db, trashedFolders, int.MaxValue, ct) ?? [];

            // A file trashed on its own inside a trashed folder is counted once.
            trash += below.Where(n => !n.IsFolder && n.TrashedAt is null).Sum(n => n.Size);
        }

        var versions = await db.NodeVersions
            .Where(v => db.Nodes.Any(n => n.Id == v.NodeId && n.LibraryId == libraryId))
            .SumAsync(v => (long?)v.Size, ct) ?? 0;

        return new UsageSummary(all + versions, all - trash, trash, versions, null);
    }
}
