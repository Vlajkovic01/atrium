using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Files.Storage;
using Atrium.Files.Uploads;
using Atrium.Identity.Sessions;
using Atrium.Jobs;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files.Users;

public enum DeletionError
{
    NotFound,
    Self,
    ConfirmationMismatch,
    LastAdmin,
    AlreadyScheduled,
    NotScheduled,
}

/// <summary>What deleting an account would remove: counts and sizes only, never names (D-004).</summary>
public sealed record DeletionImpact(int Files, int Folders, long Bytes);

public sealed record DeletionPayload(Guid UserId);

/// <summary>
/// Deleting a user (D-025). Scheduling disables the account and signs it
/// out everywhere at once; its files are removed only when the delay has
/// passed, and until then an admin can cancel. After that, content still
/// goes through the blob collector's grace period.
/// </summary>
public sealed class UserDeletionService(
    AtriumDbContext db,
    SessionService sessions,
    JobQueue jobs,
    TimeProvider time,
    FilesOptions options)
{
    public static string DedupeKeyFor(Guid userId) => $"users.delete:{userId}";

    public async Task<DeletionImpact?> ImpactAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
        {
            return null;
        }

        var libraryId = await db.Libraries.Where(l => l.OwnerUserId == userId).Select(l => (Guid?)l.Id).SingleOrDefaultAsync(ct);
        if (libraryId is null)
        {
            return new DeletionImpact(0, 0, 0);
        }

        var files = await db.Nodes.CountAsync(n => n.LibraryId == libraryId && n.Kind == NodeKind.File, ct);
        var folders = await db.Nodes.CountAsync(n => n.LibraryId == libraryId && n.Kind == NodeKind.Folder && n.ParentId != null, ct);
        var usage = await UsageService.ForLibraryAsync(db, libraryId.Value, ct);
        return new DeletionImpact(files, folders, usage.UsedBytes);
    }

    /// <summary>
    /// Schedules the deletion. The admin types the account's name to show
    /// they mean this account; an admin cannot delete themselves, and the
    /// last active admin cannot be deleted.
    /// </summary>
    public async Task<(User? User, DeletionError? Error)> ScheduleAsync(Guid adminId, Guid userId, string? confirmName, CancellationToken ct)
    {
        if (userId == adminId)
        {
            return (null, DeletionError.Self);
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return (null, DeletionError.NotFound);
        }

        if (confirmName is null || UserName.KeyOf(confirmName) != user.NameKey)
        {
            return (null, DeletionError.ConfirmationMismatch);
        }

        if (user.DeletionScheduledAt is not null)
        {
            return (null, DeletionError.AlreadyScheduled);
        }

        if (user is { Role: UserRole.Admin, Disabled: false }
            && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin && !u.Disabled, ct))
        {
            return (null, DeletionError.LastAdmin);
        }

        var now = time.GetUtcNow();
        user.Disabled = true;
        user.DeletionScheduledAt = now;
        user.DeleteAfter = now + options.UserDeletionDelay;
        await db.SaveChangesAsync(ct);
        await sessions.RevokeAllAsync(userId, exceptSessionId: null, ct);
        await jobs.EnqueueAsync(
            new JobRequest(UserDeletionHandler.Type)
            {
                Payload = new DeletionPayload(userId),
                RunAfter = user.DeleteAfter,
                DedupeKey = DedupeKeyFor(userId),
                Priority = JobPriority.Background,
            },
            ct);
        return (user, null);
    }

    /// <summary>Cancels a scheduled deletion. The account stays disabled until an admin enables it.</summary>
    public async Task<(User? User, DeletionError? Error)> CancelAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return (null, DeletionError.NotFound);
        }

        if (user.DeletionScheduledAt is null)
        {
            return (null, DeletionError.NotScheduled);
        }

        user.DeletionScheduledAt = null;
        user.DeleteAfter = null;
        await db.SaveChangesAsync(ct);

        var key = DedupeKeyFor(userId);
        if (await db.Jobs.AsNoTracking().Where(j => j.ActiveDedupeKey == key).Select(j => (Guid?)j.Id).SingleOrDefaultAsync(ct) is { } jobId)
        {
            await jobs.CancelAsync(jobId, ct);
        }

        return (user, null);
    }
}

/// <summary>
/// Removes an account whose deletion delay has passed. Checks again that
/// the deletion is still wanted: a cancelled deletion does nothing even if
/// its job runs.
/// </summary>
public sealed class UserDeletionHandler(
    AtriumDbContext db,
    UploadService uploads,
    TimeProvider time,
    ILogger<UserDeletionHandler> logger) : IJobHandler
{
    public const string Type = "users.delete";

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var userId = context.GetPayload<DeletionPayload>().UserId;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return;
        }

        if (user.DeletionScheduledAt is null || user.DeleteAfter is not { } deleteAfter || deleteAfter > time.GetUtcNow())
        {
            logger.LogInformation("Deletion of user {UserId} is no longer due; nothing was removed.", userId);
            return;
        }

        foreach (var upload in await db.Uploads.AsNoTracking().Where(u => u.UserId == userId && u.State == UploadState.Active).ToListAsync(ct))
        {
            try
            {
                await uploads.DiscardAsync(upload, UploadState.Cancelled, null, ct);
            }
            catch (Exception e) when (e is LocationUnavailableException or StorageException)
            {
                // Parts of an unfinished upload are not user files; they are
                // left for the storage's own lifecycle rather than blocking this.
                logger.LogWarning(e, "Parts of upload {UploadId} could not be removed.", upload.Id);
            }
        }

        // Rows only. Content goes through the blob collector and its grace
        // period, like any other deleted file.
        var libraries = await db.Libraries.Where(l => l.OwnerUserId == userId).Select(l => l.Id).ToListAsync(ct);
        await db.Nodes.Where(n => libraries.Contains(n.LibraryId) && n.ParentId == null).ExecuteDeleteAsync(ct);
        await db.Nodes.Where(n => libraries.Contains(n.LibraryId)).ExecuteDeleteAsync(ct);
        await db.Libraries.Where(l => libraries.Contains(l.Id)).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        logger.LogWarning("User {UserId} and their personal library were deleted as scheduled.", userId);
    }
}
