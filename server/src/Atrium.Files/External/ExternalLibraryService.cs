using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Files.Storage;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files.External;

/// <summary>Error codes for adding and changing external libraries. Clients translate them.</summary>
public static class LibraryErrors
{
    public const string InvalidName = "library.invalid_name";
    public const string PathNotAbsolute = "library.path_not_absolute";
    public const string PathMissing = "library.path_missing";
    public const string PathUnreadable = "library.path_unreadable";

    /// <summary>The folder is, contains or lies inside Atrium's own data: the config or cache directory, or a storage location.</summary>
    public const string PathProtected = "library.path_protected";

    /// <summary>The folder is, contains or lies inside another external library.</summary>
    public const string PathOverlaps = "library.path_overlaps";
    public const string NotExternal = "library.not_external";
    public const string ConfirmationMismatch = "library.confirmation_mismatch";
    public const string UnknownUser = "library.unknown_user";

    /// <summary>Writes cannot be enabled: the folder cannot be written, for example because it is mounted read-only.</summary>
    public const string FolderReadOnly = "library.folder_read_only";

    /// <summary>Why a library is offline.</summary>
    public const string FolderUnavailable = "library.folder_unavailable";
    public const string FolderEmpty = "library.folder_empty";
    public const string TooManyMissing = "library.too_many_missing";
}

/// <summary>Paths an external library must stay clear of: Atrium's own config and cache directories.</summary>
public sealed record ExternalLibraryOptions(IReadOnlyList<string> ProtectedPaths);

public sealed record PathCheck(bool Ok, string? ErrorCode, int? EntryCount);

/// <param name="UserIds">Who may see the library. The admin who adds it sees it only if listed.</param>
/// <param name="ShowInTimeline">Its photos and videos also appear on the timeline; every file shows in Files either way.</param>
public sealed record NewExternalLibrary(string Name, string Path, IReadOnlyList<Guid> UserIds, bool ShowInTimeline = true);

/// <summary>
/// Adds and manages external libraries: existing folders Atrium indexes
/// without copying (DESIGN.md 4.4). Adding one never writes into the
/// folder; deleting one removes only Atrium's index, never a file.
/// </summary>
public sealed class ExternalLibraryService(
    AtriumDbContext db,
    JobQueue queue,
    StorageProviders providers,
    ExternalLibraryOptions options,
    TimeProvider time)
{
    public const int MaxNameLength = 128;

    /// <summary>Checks a folder before it becomes a library. Reads only; never creates anything.</summary>
    public async Task<PathCheck> CheckPathAsync(string? path, Guid? exceptLibrary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            return new PathCheck(false, LibraryErrors.PathNotAbsolute, null);
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        if (!Directory.Exists(full))
        {
            return new PathCheck(false, LibraryErrors.PathMissing, null);
        }

        foreach (var protectedPath in await ProtectedPathsAsync(ct))
        {
            if (Overlaps(full, protectedPath))
            {
                return new PathCheck(false, LibraryErrors.PathProtected, null);
            }
        }

        var others = await db.Libraries.AsNoTracking()
            .Where(l => l.Mode == LibraryMode.External && l.ExternalPath != null && l.Id != exceptLibrary)
            .Select(l => l.ExternalPath!)
            .ToListAsync(ct);
        if (others.Any(other => Overlaps(full, other)))
        {
            return new PathCheck(false, LibraryErrors.PathOverlaps, null);
        }

        var folder = new ExternalFolder(full);
        if (!folder.IsReachable())
        {
            return new PathCheck(false, LibraryErrors.PathUnreadable, null);
        }

        try
        {
            return new PathCheck(true, null, folder.List(string.Empty).Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PathCheck(false, LibraryErrors.PathUnreadable, null);
        }
    }

    public async Task<FileResult<Library>> CreateAsync(Guid adminId, NewExternalLibrary request, CancellationToken ct)
    {
        if (NormalizeName(request.Name) is not { } name)
        {
            return FileResult<Library>.Fail(LibraryErrors.InvalidName);
        }

        var check = await CheckPathAsync(request.Path, null, ct);
        if (!check.Ok)
        {
            return FileResult<Library>.Fail(check.ErrorCode!);
        }

        var userIds = request.UserIds.Distinct().ToList();
        if (!await AllUsersExistAsync(userIds, ct))
        {
            return FileResult<Library>.Fail(LibraryErrors.UnknownUser);
        }

        var now = time.GetUtcNow();
        var libraryId = Guid.CreateVersion7();
        var root = new Node
        {
            LibraryId = libraryId,
            Name = name,
            NameKey = NodeName.KeyOf(name),
            ActiveNameKey = NodeName.KeyOf(name),
            Kind = NodeKind.Folder,
            ExternalPath = string.Empty,
            ExternalPathHash = ExternalFolder.HashPath(string.Empty),
            ModifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = adminId,
        };
        var library = new Library
        {
            Id = libraryId,
            Name = name,
            Kind = request.ShowInTimeline ? LibraryKind.Photos : LibraryKind.Folder,
            Mode = LibraryMode.External,
            ExternalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path.Trim())),
            CreatedByUserId = adminId,
            RootNodeId = root.Id,

            // Read-only until an admin enables writes, after a warning.
            ReadOnly = true,
            State = LibraryState.Online,
            CreatedAt = now,
        };
        db.Libraries.Add(library);
        db.Nodes.Add(root);
        db.LibraryGrants.AddRange(userIds.Select(u => new LibraryGrant
        {
            LibraryId = libraryId,
            UserId = u,
            Access = LibraryAccess.Read,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);

        await EnqueueScanAsync(library.Id, JobPriority.User, ct);
        return FileResult<Library>.Success(library);
    }

    /// <summary>
    /// Switches changes to the folder's files on or off (D-036). Switching
    /// on needs the library's name typed by the admin and proves the folder
    /// can be written; switching off needs nothing and takes effect at once.
    /// </summary>
    public async Task<FileResult<Library>> SetWritesAsync(
        Guid adminId, Guid id, bool enabled, string? confirmName, IReadOnlyList<Guid>? writerIds, CancellationToken ct)
    {
        var library = await db.Libraries.SingleOrDefaultAsync(l => l.Id == id && l.Kind != LibraryKind.Personal, ct);
        if (library is null)
        {
            return FileResult<Library>.Fail(FileErrors.NotFound);
        }

        if (library.Mode != LibraryMode.External)
        {
            return FileResult<Library>.Fail(LibraryErrors.NotExternal);
        }

        if (enabled)
        {
            if (!string.Equals(confirmName?.Trim(), library.Name, StringComparison.Ordinal))
            {
                return FileResult<Library>.Fail(LibraryErrors.ConfirmationMismatch);
            }

            if (!ExternalWriter.TryPrepare(library))
            {
                return FileResult<Library>.Fail(LibraryErrors.FolderReadOnly);
            }

            // Who may change files is chosen in the same step, so changes
            // are never on for a library nobody can actually change.
            if (writerIds is not null)
            {
                var grants = await db.LibraryGrants.Where(g => g.LibraryId == id).ToListAsync(ct);
                if (!writerIds.All(w => grants.Any(g => g.UserId == w)))
                {
                    return FileResult<Library>.Fail(LibraryErrors.UnknownUser);
                }

                foreach (var grant in grants)
                {
                    grant.Access = writerIds.Contains(grant.UserId) ? LibraryAccess.Write : LibraryAccess.Read;
                }
            }

            library.ReadOnly = false;
            library.WritesEnabledAt = time.GetUtcNow();
            library.WritesEnabledByUserId = adminId;
        }
        else
        {
            library.ReadOnly = true;
        }

        await db.SaveChangesAsync(ct);
        return FileResult<Library>.Success(library);
    }

    /// <summary>
    /// Renames a library and replaces who may see it and, of those, who may
    /// change its files once writes are enabled.
    /// </summary>
    public async Task<FileResult<Library>> UpdateAsync(
        Guid id, int expectedVersion, string? newName, IReadOnlyList<Guid>? userIds, IReadOnlyList<Guid>? writerIds, bool? showInTimeline, CancellationToken ct)
    {
        var library = await db.Libraries.SingleOrDefaultAsync(l => l.Id == id && l.Kind != LibraryKind.Personal, ct);
        if (library is null)
        {
            return FileResult<Library>.Fail(FileErrors.NotFound);
        }

        if (library.Version != expectedVersion)
        {
            return FileResult<Library>.Fail(FileErrors.VersionConflict);
        }

        if (newName is not null)
        {
            if (NormalizeName(newName) is not { } name)
            {
                return FileResult<Library>.Fail(LibraryErrors.InvalidName);
            }

            library.Name = name;
        }

        if (showInTimeline is { } timeline && library.Mode == LibraryMode.External)
        {
            library.Kind = timeline ? LibraryKind.Photos : LibraryKind.Folder;
        }

        if (userIds is not null)
        {
            var wanted = userIds.Distinct().ToList();
            if (!await AllUsersExistAsync(wanted, ct))
            {
                return FileResult<Library>.Fail(LibraryErrors.UnknownUser);
            }

            var writers = (writerIds ?? []).ToHashSet();
            if (!writers.IsSubsetOf(wanted))
            {
                return FileResult<Library>.Fail(LibraryErrors.UnknownUser);
            }

            var current = await db.LibraryGrants.Where(g => g.LibraryId == id).ToListAsync(ct);
            db.LibraryGrants.RemoveRange(current.Where(g => !wanted.Contains(g.UserId)));
            foreach (var grant in current.Where(g => wanted.Contains(g.UserId)))
            {
                grant.Access = writers.Contains(grant.UserId) ? LibraryAccess.Write : LibraryAccess.Read;
            }

            var now = time.GetUtcNow();
            db.LibraryGrants.AddRange(wanted.Where(u => current.All(g => g.UserId != u)).Select(u => new LibraryGrant
            {
                LibraryId = id,
                UserId = u,
                Access = writers.Contains(u) ? LibraryAccess.Write : LibraryAccess.Read,
                CreatedAt = now,
            }));

            // The grant list is part of the library: changing it is a change.
            db.Entry(library).State = EntityState.Modified;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FileResult<Library>.Fail(FileErrors.VersionConflict);
        }

        return FileResult<Library>.Success(library);
    }

    /// <summary>
    /// Removes the library from Atrium: its index, albums' references to it
    /// and its grants. The folder and every file in it stay as they are.
    /// </summary>
    public async Task<FileResult<bool>> DeleteAsync(Guid id, string? confirmName, CancellationToken ct)
    {
        var library = await db.Libraries.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id && l.Kind != LibraryKind.Personal, ct);
        if (library is null)
        {
            return FileResult<bool>.Fail(FileErrors.NotFound);
        }

        if (library.Mode != LibraryMode.External)
        {
            return FileResult<bool>.Fail(LibraryErrors.NotExternal);
        }

        if (!string.Equals(confirmName?.Trim(), library.Name, StringComparison.Ordinal))
        {
            return FileResult<bool>.Fail(LibraryErrors.ConfirmationMismatch);
        }

        // The root row first would leave the library pointing nowhere; the
        // library row cascades to every node, grant and upload.
        await db.Libraries.Where(l => l.Id == id).ExecuteDeleteAsync(ct);
        return FileResult<bool>.Success(true);
    }

    public Task<Job> EnqueueScanAsync(Guid libraryId, JobPriority priority, CancellationToken ct) =>
        queue.EnqueueAsync(
            new JobRequest(LibraryScanHandler.Type)
            {
                Payload = new LibraryScanPayload(libraryId),
                Priority = priority,
                ResourceClass = JobResourceClass.Io,
                DedupeKey = LibraryScanHandler.DedupeKeyFor(libraryId),
            },
            ct);

    public static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    private static bool Overlaps(string a, string b) =>
        ExternalFolder.IsSameOrInside(a, b) || ExternalFolder.IsSameOrInside(b, a);

    private async Task<bool> AllUsersExistAsync(List<Guid> userIds, CancellationToken ct) =>
        userIds.Count == 0
        || await db.Users.CountAsync(u => userIds.Contains(u.Id), ct) == userIds.Count;

    private async Task<List<string>> ProtectedPathsAsync(CancellationToken ct)
    {
        var paths = options.ProtectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var locations = await db.StorageLocations.AsNoTracking()
            .Where(l => l.Kind != StorageProviderKind.S3)
            .ToListAsync(ct);
        foreach (var location in locations)
        {
            if (providers.ReadConfig(location).Local?.Path is { } localPath)
            {
                paths.Add(localPath);
            }
        }

        return paths;
    }
}
