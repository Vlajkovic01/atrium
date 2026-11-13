using System.Text.Json;
using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files.External;

public sealed record LibraryScanPayload(Guid LibraryId);

/// <summary>
/// Indexes an external library's folder: new files are added, changed ones
/// updated, vanished ones marked missing. Reads the folder only; nothing in
/// it is ever created, changed or deleted (DESIGN.md 4.4).
/// </summary>
/// <remarks>
/// The scan saves the folders it still has to visit as its checkpoint, so
/// after a restart it continues where it stopped. Every entry it finds is
/// stamped with the scan's start; at the end, what was not stamped is
/// missing. Before marking anything missing it checks the folder is really
/// there: a missing, empty or unreadable folder, or more than half of the
/// files gone at once, marks the library offline and changes nothing, so an
/// unmounted share cannot wipe a library's history.
/// </remarks>
public sealed class LibraryScanHandler(
    AtriumDbContext db,
    MediaTypeResolver mediaTypes,
    IEnumerable<INodeContentObserver> observers,
    ExternalLocks locks,
    TimeProvider time,
    ILogger<LibraryScanHandler> logger) : IJobHandler
{
    public const string Type = "library.scan";

    /// <summary>Above this share of known files vanishing in one scan, the folder is treated as not really there.</summary>
    public const double MissingThreshold = 0.5;

    /// <summary>Fewer missing files than this are always accepted, whatever their share.</summary>
    public const int MissingAlwaysAccepted = 50;

    /// <summary>The checkpoint is saved after this many folders, or every two seconds.</summary>
    public const int CheckpointEvery = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string DedupeKeyFor(Guid libraryId) => $"library.scan:{libraryId}";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.GetPayload<LibraryScanPayload>();
        var library = await db.Libraries.SingleOrDefaultAsync(l => l.Id == payload.LibraryId, ct);
        if (library is not { Mode: LibraryMode.External, ExternalPath: { } path })
        {
            return;
        }

        var folder = new ExternalFolder(path);
        var liveFiles = await db.Nodes.CountAsync(
            n => n.LibraryId == library.Id && n.Kind == NodeKind.File && n.MissingSince == null, ct);
        if (!folder.IsReachable())
        {
            await GoOfflineAsync(library, LibraryErrors.FolderUnavailable, ct);
            return;
        }

        if (liveFiles > 0 && folder.List(string.Empty).Count == 0)
        {
            await GoOfflineAsync(library, LibraryErrors.FolderEmpty, ct);
            return;
        }

        var state = context.Checkpoint is { } saved
            ? JsonSerializer.Deserialize<ScanState>(saved, Json)!
            : new ScanState(time.GetUtcNow(), [string.Empty], [], 0);
        var lastSave = time.GetUtcNow();

        while (state.Pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var relative = state.Pending[^1];
            state.Pending.RemoveAt(state.Pending.Count - 1);

            try
            {
                List<string> subfolders;
                using (await locks.AcquireAsync(library.Id, ct))
                {
                    subfolders = await IndexFolderAsync(library, folder, relative, state.StartedAt, ct);
                }
                state.Pending.AddRange(subfolders.AsEnumerable().Reverse());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // What is inside an unreadable folder is not known to be gone.
                logger.LogWarning("Folder {Folder} of library {Library} could not be read: {Error}", relative, library.Id, e.Message);
                state.Unreadable.Add(relative);
            }

            state = state with { Done = state.Done + 1 };
            if (state.Done % CheckpointEvery == 0 || time.GetUtcNow() - lastSave > TimeSpan.FromSeconds(2) || state.Pending.Count == 0)
            {
                await context.SaveCheckpointAsync(JsonSerializer.Serialize(state, Json), ct);
                await context.ReportProgressAsync(0.95 * state.Done / (state.Done + state.Pending.Count), ct);
                lastSave = time.GetUtcNow();
            }
        }

        await MarkMissingAsync(library, state, liveFiles, ct);
    }

    /// <summary>Adds and updates the entries of one folder. Returns its subfolders to visit.</summary>
    private async Task<List<string>> IndexFolderAsync(
        Library library, ExternalFolder folder, string relative, DateTimeOffset stamp, CancellationToken ct)
    {
        var hash = ExternalFolder.HashPath(relative);
        var parent = await db.Nodes.SingleOrDefaultAsync(n => n.LibraryId == library.Id && n.ExternalPathHash == hash, ct);
        if (parent is null)
        {
            // The parent's own folder listing creates it; reaching here means
            // it vanished meanwhile. Its contents are found on the next scan.
            return [];
        }

        var entries = folder.List(relative);
        var known = await db.Nodes
            .Where(n => n.ParentId == parent.Id)
            .ToDictionaryAsync(n => n.ExternalPath ?? string.Empty, StringComparer.Ordinal, ct);
        var now = time.GetUtcNow();
        var changed = new List<Node>();
        var subfolders = new List<string>();

        parent.LastSeenAt = stamp;
        parent.MissingSince = null;
        foreach (var entry in entries)
        {
            var kind = entry.IsFolder ? NodeKind.Folder : NodeKind.File;
            if (known.TryGetValue(entry.RelativePath, out var node) && node.Kind != kind)
            {
                // A file became a folder or the other way round: a new entry.
                db.Nodes.Remove(node);
                node = null;
            }

            if (node is null)
            {
                node = new Node
                {
                    LibraryId = library.Id,
                    ParentId = parent.Id,
                    Name = entry.Name,
                    NameKey = NodeName.KeyOf(entry.Name),

                    // The folder itself guarantees distinct names, exactly as
                    // written; on a case-sensitive disk "a.jpg" and "A.jpg"
                    // are both real files.
                    ActiveNameKey = entry.Name,
                    Kind = kind,
                    Size = entry.Size,
                    MediaType = entry.IsFolder ? null : mediaTypes.For(entry.Name),
                    ModifiedAt = entry.ModifiedAt,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ExternalPath = entry.RelativePath,
                    ExternalPathHash = ExternalFolder.HashPath(entry.RelativePath),
                    LastSeenAt = stamp,
                };
                db.Nodes.Add(node);
                changed.Add(node);
            }
            else
            {
                node.LastSeenAt = stamp;
                if (node.MissingSince is not null)
                {
                    node.MissingSince = null;
                    changed.Add(node);
                }

                if (!entry.IsFolder && (node.Size != entry.Size || node.ModifiedAt != entry.ModifiedAt))
                {
                    node.Size = entry.Size;
                    node.ModifiedAt = entry.ModifiedAt;
                    node.UpdatedAt = now;

                    // Dated again from the new content.
                    node.CapturedAt = null;
                    changed.Add(node);
                }
            }

            if (entry.IsFolder)
            {
                subfolders.Add(entry.RelativePath);
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var files = changed.Where(n => !n.IsFolder).ToList();
        foreach (var observer in observers)
        {
            await observer.ContentChangedAsync(library, files, ct);
        }

        return subfolders;
    }

    private async Task MarkMissingAsync(Library library, ScanState state, int liveFilesBefore, CancellationToken ct)
    {
        var notSeen = db.Nodes.Where(n => n.LibraryId == library.Id
            && n.Id != library.RootNodeId
            && n.MissingSince == null
            && !n.InTrash
            && (n.LastSeenAt == null || n.LastSeenAt < state.StartedAt));
        var candidates = await notSeen.Select(n => new { n.Id, n.Kind, n.ExternalPath }).ToListAsync(ct);

        // Entries inside a folder that could not be read are not known to be gone.
        var missing = candidates
            .Where(c => !state.Unreadable.Any(u => u.Length == 0 || (c.ExternalPath ?? string.Empty).StartsWith(u + "/", StringComparison.Ordinal)))
            .ToList();
        var missingFiles = missing.Count(m => m.Kind == NodeKind.File);
        if (missingFiles >= MissingAlwaysAccepted && missingFiles > liveFilesBefore * MissingThreshold)
        {
            logger.LogWarning(
                "Scan of library {Library} found {Missing} of {Known} files gone; the library is marked offline and nothing is changed.",
                library.Id,
                missingFiles,
                liveFilesBefore);
            await GoOfflineAsync(library, LibraryErrors.TooManyMissing, ct);
            return;
        }

        var now = time.GetUtcNow();
        foreach (var chunk in missing.Select(m => m.Id).Chunk(500))
        {
            var ids = chunk.ToList();
            await db.Nodes.Where(n => ids.Contains(n.Id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.MissingSince, now), ct);
        }

        // Scan state is not something an admin edits, so it does not bump
        // the library's version and never conflicts with an admin's change.
        await db.Libraries.Where(l => l.Id == library.Id).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.State, LibraryState.Online)
            .SetProperty(l => l.StateReason, (string?)null)
            .SetProperty(l => l.LastScanAt, now), ct);
        logger.LogInformation("Scanned library {Library}: {Missing} entries no longer found.", library.Id, missing.Count);
    }

    private async Task GoOfflineAsync(Library library, string reason, CancellationToken ct)
    {
        await db.Libraries.Where(l => l.Id == library.Id).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.State, LibraryState.Offline)
            .SetProperty(l => l.StateReason, reason), ct);
    }

    private sealed record ScanState(DateTimeOffset StartedAt, List<string> Pending, List<string> Unreadable, int Done);
}



/// <summary>Scans every external library on a schedule, so changes made outside Atrium show up.</summary>
public sealed class LibraryScanAllHandler(AtriumDbContext db, ExternalLibraryService libraries) : IJobHandler
{
    public const string Type = "library.scan-all";

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromHours(6), JobPriority.Background, JobResourceClass.Io);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var ids = await db.Libraries.AsNoTracking()
            .Where(l => l.Mode == LibraryMode.External)
            .Select(l => l.Id)
            .ToListAsync(ct);
        foreach (var id in ids)
        {
            await libraries.EnqueueScanAsync(id, JobPriority.Background, ct);
        }
    }
}
