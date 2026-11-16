using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Identity.Crypto;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>One item of the feed, as it is now.</summary>
/// <param name="Node">Null when the item is gone for the reader: removed for good, or no longer found in its folder.</param>
/// <param name="InTrash">The item or a folder above it is in the trash.</param>
public sealed record ChangedNode(Guid NodeId, Guid LibraryId, Node? Node, bool CanWrite, bool InTrash);

/// <param name="Cursor">Where to continue from next time.</param>
/// <param name="HasMore">More changes are waiting; ask again at once.</param>
public sealed record ChangeBatch(IReadOnlyList<ChangedNode> Items, string Cursor, bool HasMore);

/// <summary>
/// What changed since a client last asked (D-047), so a phone that keeps
/// folders for offline use does not have to read everything again.
/// </summary>
/// <remarks>
/// A client first asks without a cursor and gets one that means "now",
/// reads the folders it cares about, and from then on asks with the cursor
/// it was given last. Each answer lists the items to look at again, each
/// once, as they are at the time of the answer. Applying an answer twice
/// gives the same result, so a lost response is simply asked for again.
/// </remarks>
public sealed class ChangeFeed(AtriumDbContext db, NodeService nodes, SecretProtector protector, TimeProvider time)
{
    public const int MaxLimit = 1000;

    private const string CursorPurpose = "atrium/change-feed-cursor/v1";
    private const int ChunkSize = 500;

    public async Task<FileResult<ChangeBatch>> ReadAsync(FileActor actor, string? cursor, int limit, CancellationToken ct)
    {
        if (cursor is null)
        {
            return FileResult<ChangeBatch>.Success(new ChangeBatch([], Encode(await NewestAsync(ct)), false));
        }

        // The cursor names an entry. It is good only while that very entry
        // exists: after a restored backup or a new database the sequence
        // starts over, and the same number would mean another place.
        if (Decode(cursor) is not { } position
            || await db.Changes.AsNoTracking().SingleOrDefaultAsync(c => c.Sequence == position.Sequence, ct) is not { } seen
            || seen.At.ToUnixTimeMilliseconds() != position.At)
        {
            return FileResult<ChangeBatch>.Fail(FileErrors.CursorExpired);
        }

        // The newest entry is read first and bounds the page, so the cursor
        // handed back never passes an entry this answer did not consider.
        var newest = await NewestAsync(ct);
        var libraries = await nodes.ReadableLibrariesAsync(actor, ct);
        var libraryIds = libraries.Select(l => l.Id).ToList();
        var entries = await db.Changes.AsNoTracking()
            .Where(c => c.Sequence > seen.Sequence && c.Sequence <= newest.Sequence
                && c.LibraryId != null && libraryIds.Contains(c.LibraryId.Value))
            .OrderBy(c => c.Sequence)
            .Take(limit + 1)
            .ToListAsync(ct);
        var hasMore = entries.Count > limit;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        // An item changed several times is reported once, where it changed last.
        var order = new List<Guid>();
        var latest = new Dictionary<Guid, ChangeEntry>();
        foreach (var entry in entries)
        {
            var id = entry.NodeId!.Value;
            if (latest.ContainsKey(id))
            {
                order.Remove(id);
            }

            order.Add(id);
            latest[id] = entry;
        }

        var found = new Dictionary<Guid, Node>();
        foreach (var chunk in order.Chunk(ChunkSize))
        {
            var ids = chunk.ToList();
            foreach (var node in await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync(ct))
            {
                found[node.Id] = node;
            }
        }

        var writable = new Dictionary<Guid, bool>();
        foreach (var library in libraries)
        {
            writable[library.Id] = NodeService.CanWrite(actor, library, await nodes.GrantAsync(actor, library, ct));
        }

        var items = new List<ChangedNode>(order.Count);
        foreach (var id in order)
        {
            var libraryId = latest[id].LibraryId!.Value;

            // A file a scan no longer finds is kept for a while, but is not
            // there for anyone, exactly as when it is asked for by itself.
            var node = found.TryGetValue(id, out var current) && current.MissingSince is null && current.LibraryId == libraryId
                ? current
                : null;
            items.Add(new ChangedNode(
                id, libraryId, node, node is { InTrash: false } && writable[libraryId], node?.InTrash ?? false));
        }

        return FileResult<ChangeBatch>.Success(new ChangeBatch(items, Encode(hasMore ? entries[^1] : newest), hasMore));
    }

    /// <summary>
    /// Removes entries older than the retention, except the newest of them.
    /// That one is where a client continues from if it has seen everything
    /// older than the retention; removing it would send away a client that
    /// missed nothing, or only what the feed still holds.
    /// </summary>
    public static async Task<int> ExpireAsync(AtriumDbContext db, DateTimeOffset now, TimeSpan retention, CancellationToken ct)
    {
        var cutoff = now - retention;
        var kept = await db.Changes.Where(c => c.At < cutoff).MaxAsync(c => (long?)c.Sequence, ct);
        return kept is null
            ? 0
            : await db.Changes.Where(c => c.At < cutoff && c.Sequence < kept).ExecuteDeleteAsync(ct);
    }

    /// <summary>The newest entry. An empty feed gets a marker first, so that "now" is always a real entry.</summary>
    private async Task<ChangeEntry> NewestAsync(CancellationToken ct)
    {
        if (await db.Changes.AsNoTracking().OrderByDescending(c => c.Sequence).FirstOrDefaultAsync(ct) is { } newest)
        {
            return newest;
        }

        var marker = new ChangeEntry { At = time.GetUtcNow() };
        db.Changes.Add(marker);
        await db.SaveChangesAsync(ct);
        db.Entry(marker).State = EntityState.Detached;
        return marker;
    }

    // The cursor is sealed with the server key. Clients cannot read or make
    // one, so the sequence, which counts changes by everyone on the server,
    // tells nobody how busy other people are.
    private string Encode(ChangeEntry entry)
    {
        Span<byte> plain = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(plain, entry.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(plain[8..], entry.At.ToUnixTimeMilliseconds());
        return Base64Url.EncodeToString(protector.Protect(plain, CursorPurpose));
    }

    private (long Sequence, long At)? Decode(string cursor)
    {
        if (cursor.Length is 0 or > 256 || !Base64Url.IsValid(cursor))
        {
            return null;
        }

        try
        {
            var plain = protector.Unprotect(Base64Url.DecodeFromChars(cursor), CursorPurpose);
            return plain.Length == 16
                ? (BinaryPrimitives.ReadInt64BigEndian(plain), BinaryPrimitives.ReadInt64BigEndian(plain.AsSpan(8)))
                : null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
