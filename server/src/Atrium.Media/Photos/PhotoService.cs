using System.Globalization;
using Atrium.Core.Files;
using Atrium.Core.Media;
using Atrium.Data;
using Atrium.Files;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Media.Photos;

/// <summary>A month of the timeline and how many photos and videos it holds.</summary>
public sealed record TimelineBucket(int Year, int Month, int Count);

/// <summary>A photo or video with the facts a grid needs to lay it out.</summary>
public sealed record PhotoItem(Node Node, MediaInfo? Info);

public sealed record PhotoPage(IReadOnlyList<PhotoItem> Items, string? NextCursor);

/// <param name="Query">Words in the name, ignoring case and diacritics.</param>
/// <param name="From">From this local date and time, inclusive (D-034).</param>
/// <param name="To">Until this local date and time, exclusive.</param>
public sealed record SearchRequest(
    string? Query, SearchType? Type, DateTimeOffset? From, DateTimeOffset? To, Guid? LibraryId, string? Cursor, int Limit);

public enum SearchType
{
    Photo,
    Video,

    /// <summary>Any file that is neither.</summary>
    Document,
    Folder,
}

/// <summary>
/// The photo timeline, albums and search (DESIGN.md 2.3, UX.md Photos). The
/// timeline is a view, not a library: every photo and video the person can
/// read, across their personal library and the libraries shared with them,
/// newest first, without the trash and without files that vanished.
/// </summary>
public sealed class PhotoService(AtriumDbContext db, NodeService nodes, TimeProvider time)
{
    public const int MaxLimit = 500;

    /// <summary>Months with their counts, newest first, for a scrubber that works with any number of photos.</summary>
    public async Task<List<TimelineBucket>> BucketsAsync(FileActor actor, Guid? libraryId, CancellationToken ct)
    {
        var libraries = await LibraryIdsAsync(actor, libraryId, ct);

        // One indexed column; grouped here because the month of a stored
        // millisecond count is spelled differently on every database.
        var dates = await Visible(libraries).Select(n => n.CapturedAt!.Value).ToListAsync(ct);
        return dates
            .GroupBy(d => (d.Year, d.Month))
            .OrderByDescending(g => g.Key)
            .Select(g => new TimelineBucket(g.Key.Year, g.Key.Month, g.Count()))
            .ToList();
    }

    /// <summary>A page of the timeline, newest first, optionally within [from, to).</summary>
    public async Task<PhotoPage> TimelineAsync(
        FileActor actor, Guid? libraryId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, CancellationToken ct)
    {
        var libraries = await LibraryIdsAsync(actor, libraryId, ct);
        var query = Visible(libraries);
        if (from is { } f)
        {
            query = query.Where(n => n.CapturedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(n => n.CapturedAt < t);
        }

        return await PageAsync(query, cursor, limit, ct);
    }

    /// <summary>
    /// Files and folders by name, type and date, across every library the
    /// person can read. Dates are when a photo was taken, or when another
    /// file was last changed.
    /// </summary>
    public async Task<PhotoPage> SearchAsync(FileActor actor, SearchRequest request, CancellationToken ct)
    {
        var libraries = await LibraryIdsAsync(actor, request.LibraryId, ct);
        var query = db.Nodes.AsNoTracking()
            .Where(n => libraries.Contains(n.LibraryId) && !n.InTrash && n.MissingSince == null && n.ParentId != null);

        foreach (var word in Words(request.Query))
        {
            var pattern = $"%{EscapeLike(word)}%";
            query = query.Where(n => EF.Functions.Like(n.SearchKey, pattern, "\\"));
        }

        query = request.Type switch
        {
            SearchType.Photo => query.Where(n => n.MediaKind == MediaKind.Photo),
            SearchType.Video => query.Where(n => n.MediaKind == MediaKind.Video),
            SearchType.Document => query.Where(n => n.Kind == NodeKind.File && n.MediaKind == null),
            SearchType.Folder => query.Where(n => n.Kind == NodeKind.Folder),
            _ => query,
        };

        // Only photos and videos have a capture time; other files are dated
        // by their modification time, both on the same local clock.
        if (request.From is { } from)
        {
            query = query.Where(n => (n.CapturedAt ?? n.ModifiedAt) >= from);
        }

        if (request.To is { } to)
        {
            query = query.Where(n => (n.CapturedAt ?? n.ModifiedAt) < to);
        }

        var take = Math.Clamp(request.Limit, 1, MaxLimit);
        var offset = request.Cursor is { } c && int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var page = await query
            .OrderByDescending(n => n.CapturedAt ?? n.ModifiedAt)
            .ThenByDescending(n => n.Id)
            .Skip(offset)
            .Take(take + 1)
            .ToListAsync(ct);
        var items = await WithInfoAsync(page.Take(take).ToList(), ct);
        return new PhotoPage(items, page.Count > take ? (offset + take).ToString(CultureInfo.InvariantCulture) : null);
    }

    public Task<List<Album>> AlbumsAsync(FileActor actor, CancellationToken ct) =>
        db.Albums.AsNoTracking().Where(a => a.OwnerUserId == actor.UserId).OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);

    public Task<Album?> AlbumAsync(FileActor actor, Guid id, CancellationToken ct) =>
        db.Albums.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.OwnerUserId == actor.UserId, ct);

    /// <summary>The items of an album the person can still see, newest first.</summary>
    public async Task<PhotoPage> AlbumItemsAsync(FileActor actor, Guid albumId, string? cursor, int limit, CancellationToken ct)
    {
        var libraries = await LibraryIdsAsync(actor, null, ct);
        var query = Visible(libraries).Where(n => db.AlbumItems.Any(i => i.AlbumId == albumId && i.NodeId == n.Id));
        return await PageAsync(query, cursor, limit, ct);
    }

    /// <summary>How many items an album shows, and the newest and oldest dates in it.</summary>
    public async Task<(int Count, DateTimeOffset? Newest, DateTimeOffset? Oldest, Guid? NewestId)> AlbumSummaryAsync(
        FileActor actor, Guid albumId, CancellationToken ct)
    {
        var libraries = await LibraryIdsAsync(actor, null, ct);
        var items = Visible(libraries).Where(n => db.AlbumItems.Any(i => i.AlbumId == albumId && i.NodeId == n.Id));
        var count = await items.CountAsync(ct);
        if (count == 0)
        {
            return (0, null, null, null);
        }

        var newest = await items.OrderByDescending(n => n.CapturedAt).ThenByDescending(n => n.Id).Select(n => new { n.Id, n.CapturedAt }).FirstAsync(ct);
        var oldest = await items.OrderBy(n => n.CapturedAt).Select(n => n.CapturedAt).FirstAsync(ct);
        return (count, newest.CapturedAt, oldest, newest.Id);
    }

    public async Task<FileResult<Album>> CreateAlbumAsync(FileActor actor, string? name, IReadOnlyList<Guid> nodeIds, CancellationToken ct)
    {
        if (!actor.CanWriteAnything)
        {
            return FileResult<Album>.Fail(FileErrors.ReadOnly);
        }

        if (NormalizeName(name) is not { } normalized)
        {
            return FileResult<Album>.Fail(AlbumErrors.InvalidName);
        }

        var now = time.GetUtcNow();
        var album = new Album { OwnerUserId = actor.UserId, Name = normalized, CreatedAt = now, UpdatedAt = now };
        db.Albums.Add(album);
        await db.SaveChangesAsync(ct);
        if (nodeIds.Count > 0)
        {
            var added = await AddItemsAsync(actor, album.Id, nodeIds, ct);
            if (!added.Ok)
            {
                await db.Albums.Where(a => a.Id == album.Id).ExecuteDeleteAsync(ct);
                return FileResult<Album>.Fail(added.Error!);
            }
        }

        return FileResult<Album>.Success((await AlbumAsync(actor, album.Id, ct))!);
    }

    public async Task<FileResult<Album>> UpdateAlbumAsync(
        FileActor actor, Guid id, int expectedVersion, string? name, Guid? coverNodeId, CancellationToken ct)
    {
        if (!actor.CanWriteAnything)
        {
            return FileResult<Album>.Fail(FileErrors.ReadOnly);
        }

        var album = await db.Albums.SingleOrDefaultAsync(a => a.Id == id && a.OwnerUserId == actor.UserId, ct);
        if (album is null)
        {
            return FileResult<Album>.Fail(FileErrors.NotFound);
        }

        if (album.Version != expectedVersion)
        {
            return FileResult<Album>.Fail(FileErrors.VersionConflict);
        }

        if (name is not null)
        {
            if (NormalizeName(name) is not { } normalized)
            {
                return FileResult<Album>.Fail(AlbumErrors.InvalidName);
            }

            album.Name = normalized;
        }

        if (coverNodeId is { } cover)
        {
            if (!await db.AlbumItems.AnyAsync(i => i.AlbumId == id && i.NodeId == cover, ct))
            {
                return FileResult<Album>.Fail(AlbumErrors.NotInAlbum);
            }

            album.CoverNodeId = cover;
        }

        album.UpdatedAt = time.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FileResult<Album>.Fail(FileErrors.VersionConflict);
        }

        return FileResult<Album>.Success(album);
    }

    /// <summary>Deletes the album. Its photos are references and stay where they are.</summary>
    public async Task<bool> DeleteAlbumAsync(FileActor actor, Guid id, CancellationToken ct) =>
        actor.CanWriteAnything
        && await db.Albums.Where(a => a.Id == id && a.OwnerUserId == actor.UserId).ExecuteDeleteAsync(ct) == 1;

    /// <summary>Adds photos and videos the person can read. Adding one twice changes nothing.</summary>
    public async Task<FileResult<int>> AddItemsAsync(FileActor actor, Guid albumId, IReadOnlyList<Guid> nodeIds, CancellationToken ct)
    {
        if (!actor.CanWriteAnything)
        {
            return FileResult<int>.Fail(FileErrors.ReadOnly);
        }

        if (!await db.Albums.AnyAsync(a => a.Id == albumId && a.OwnerUserId == actor.UserId, ct))
        {
            return FileResult<int>.Fail(FileErrors.NotFound);
        }

        var libraries = await LibraryIdsAsync(actor, null, ct);
        var wanted = nodeIds.Distinct().ToList();
        var readable = await Visible(libraries).Where(n => wanted.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct);
        if (readable.Count != wanted.Count)
        {
            // Someone else's file, or not a photo or video: the same answer.
            return FileResult<int>.Fail(FileErrors.NotFound);
        }

        var present = await db.AlbumItems.Where(i => i.AlbumId == albumId && wanted.Contains(i.NodeId)).Select(i => i.NodeId).ToListAsync(ct);
        var now = time.GetUtcNow();
        var added = wanted.Except(present).Select(id => new AlbumItem { AlbumId = albumId, NodeId = id, AddedAt = now }).ToList();
        db.AlbumItems.AddRange(added);
        await db.SaveChangesAsync(ct);
        await TouchAsync(albumId, now, ct);
        return FileResult<int>.Success(added.Count);
    }

    public async Task<FileResult<int>> RemoveItemsAsync(FileActor actor, Guid albumId, IReadOnlyList<Guid> nodeIds, CancellationToken ct)
    {
        if (!actor.CanWriteAnything)
        {
            return FileResult<int>.Fail(FileErrors.ReadOnly);
        }

        if (!await db.Albums.AnyAsync(a => a.Id == albumId && a.OwnerUserId == actor.UserId, ct))
        {
            return FileResult<int>.Fail(FileErrors.NotFound);
        }

        var ids = nodeIds.Distinct().ToList();
        var removed = await db.AlbumItems.Where(i => i.AlbumId == albumId && ids.Contains(i.NodeId)).ExecuteDeleteAsync(ct);
        await db.Albums.Where(a => a.Id == albumId && a.CoverNodeId != null && ids.Contains(a.CoverNodeId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CoverNodeId, (Guid?)null), ct);
        await TouchAsync(albumId, time.GetUtcNow(), ct);
        return FileResult<int>.Success(removed);
    }

    public static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > NodeName.MaxLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    private Task TouchAsync(Guid albumId, DateTimeOffset now, CancellationToken ct) =>
        db.Albums.Where(a => a.Id == albumId).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, now), ct);

    private IQueryable<Node> Visible(List<Guid> libraries) =>
        db.Nodes.AsNoTracking().Where(n =>
            libraries.Contains(n.LibraryId) && n.MediaKind != null && n.CapturedAt != null && !n.InTrash && n.MissingSince == null);

    private async Task<List<Guid>> LibraryIdsAsync(FileActor actor, Guid? only, CancellationToken ct)
    {
        var ids = (await nodes.ReadableLibrariesAsync(actor, ct)).Select(l => l.Id).ToList();
        return only is { } id ? ids.Where(x => x == id).ToList() : ids;
    }

    /// <summary>Keyset paging on (capture time, id), newest first: stable while photos are added.</summary>
    private async Task<PhotoPage> PageAsync(IQueryable<Node> query, string? cursor, int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        if (DecodeCursor(cursor) is { } after)
        {
            var at = after.CapturedAt;
            var id = after.Id;
            query = query.Where(n => n.CapturedAt < at || (n.CapturedAt == at && n.Id.CompareTo(id) < 0));
        }

        var page = await query.OrderByDescending(n => n.CapturedAt).ThenByDescending(n => n.Id).Take(take + 1).ToListAsync(ct);
        var items = await WithInfoAsync(page.Take(take).ToList(), ct);
        var last = items.Count > 0 ? items[^1].Node : null;
        return new PhotoPage(items, page.Count > take && last is not null ? EncodeCursor(last.CapturedAt!.Value, last.Id) : null);
    }

    private async Task<List<PhotoItem>> WithInfoAsync(List<Node> page, CancellationToken ct)
    {
        var blobIds = page.Where(n => n.BlobId is not null).Select(n => n.BlobId!.Value).Distinct().ToList();
        var nodeIds = page.Where(n => n.BlobId is null && n.MediaKind is not null).Select(n => n.Id).ToList();
        var infos = await db.MediaInfos.AsNoTracking()
            .Where(m => (m.BlobId != null && blobIds.Contains(m.BlobId.Value)) || (m.NodeId != null && nodeIds.Contains(m.NodeId.Value)))
            .ToListAsync(ct);
        var byBlob = infos.Where(m => m.BlobId is not null).ToDictionary(m => m.BlobId!.Value);
        var byNode = infos.Where(m => m.NodeId is not null).ToDictionary(m => m.NodeId!.Value);
        return page.Select(n => new PhotoItem(
            n,
            n.BlobId is { } b ? byBlob.GetValueOrDefault(b) : byNode.GetValueOrDefault(n.Id))).ToList();
    }

    private static string EncodeCursor(DateTimeOffset at, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{at.ToUnixTimeMilliseconds()}_{id:N}");

    private static (DateTimeOffset CapturedAt, Guid Id)? DecodeCursor(string? cursor)
    {
        var parts = cursor?.Split('_');
        return parts is { Length: 2 }
            && long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms)
            && Guid.TryParseExact(parts[1], "N", out var id)
            ? (DateTimeOffset.FromUnixTimeMilliseconds(ms), id)
            : null;
    }

    private static IEnumerable<string> Words(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : NodeName.SearchKeyOf(query.Trim()).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

public static class AlbumErrors
{
    public const string InvalidName = "album.invalid_name";
    public const string NotInAlbum = "album.not_in_album";
}
