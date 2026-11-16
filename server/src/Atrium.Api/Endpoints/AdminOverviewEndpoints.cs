using Atrium.Api.Auth;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Data.Backup;
using Atrium.Files.Blobs;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Code">What needs attention, for the client to word: <c>storage.offline</c>, <c>library.offline</c>,
/// <c>jobs.failed</c>, <c>jobs.paused</c>, <c>backup.none</c>, <c>backup.old</c>, <c>integrity.problems</c>,
/// <c>integrity.none</c>, <c>integrity.old</c>, <c>people.deletion</c>.</param>
/// <param name="Severity"><c>warning</c> needs the admin; <c>info</c> is worth knowing.</param>
/// <param name="Section">The administration section that deals with it.</param>
/// <param name="Name">The location, folder or person concerned, when there is one.</param>
/// <param name="Count">How many, when it is a count.</param>
public sealed record AdminAttention(string Code, string Severity, string Section, string? Name, long? Count);

public sealed record OverviewPeople(int Total, int Admins, int Disabled, int PendingDeletion);

/// <param name="StoredBytes">What Atrium keeps there, after deduplication.</param>
public sealed record OverviewLocation(Guid Id, string Name, StorageLocationState State, DateTimeOffset? LastCheckedAt, long StoredBytes);

public sealed record OverviewLibraries(int Folders, int Offline, int ChangesAllowed);

/// <param name="FailedLastDay">Jobs that failed for good in the last 24 hours.</param>
public sealed record OverviewJobs(int Running, int Waiting, int FailedLastDay, bool Paused, DateTimeOffset? PausedUntil);

public sealed record OverviewBackup(int Count, DateTimeOffset? LastAt, long? LastSize);

/// <param name="Problems">Missing and damaged files found by the last check.</param>
public sealed record OverviewIntegrity(DateTimeOffset? LastAt, long Problems, bool Running);

/// <summary>
/// The state of the server at a glance (UX.md, Administration). Counts and
/// sizes only: an admin sees no file names or content here.
/// </summary>
public sealed record AdminOverview(
    IReadOnlyList<AdminAttention> Attention,
    OverviewPeople People,
    IReadOnlyList<OverviewLocation> Storage,
    OverviewLibraries Libraries,
    OverviewJobs Jobs,
    OverviewBackup Backup,
    OverviewIntegrity Integrity);

public static class AdminOverviewEndpoints
{
    /// <summary>Backups run daily; one older than this means the schedule is not keeping up.</summary>
    internal static readonly TimeSpan BackupTooOld = TimeSpan.FromDays(2);

    /// <summary>The check runs weekly; one older than this has been missed.</summary>
    internal static readonly TimeSpan IntegrityTooOld = TimeSpan.FromDays(14);

    public static IEndpointRouteBuilder MapAdminOverviewEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/admin/overview", GetAsync)
            .WithTags("Administration")
            .WithName("GetAdminOverview")
            .RequireAuthorization(AuthSetup.AdminPolicy);
        return routes;
    }

    private static async Task<AdminOverview> GetAsync(
        AtriumDbContext db, JobLimits limits, BackupOptions backups, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var attention = new List<AdminAttention>();

        var users = await db.Users.AsNoTracking()
            .Select(u => new { u.Name, u.Role, u.Disabled, u.DeletionScheduledAt })
            .ToListAsync(ct);
        var people = new OverviewPeople(
            users.Count,
            users.Count(u => u.Role == UserRole.Admin),
            users.Count(u => u.Disabled),
            users.Count(u => u.DeletionScheduledAt is not null));
        if (people.PendingDeletion > 0)
        {
            attention.Add(new("people.deletion", "info", "people", null, people.PendingDeletion));
        }

        var stored = await db.Blobs.AsNoTracking()
            .GroupBy(b => b.StorageLocationId)
            .Select(g => new { Id = g.Key, Bytes = g.Sum(b => b.Size) })
            .ToDictionaryAsync(x => x.Id, x => x.Bytes, ct);
        var locations = (await db.StorageLocations.AsNoTracking().ToListAsync(ct))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => new OverviewLocation(l.Id, l.Name, l.State, l.LastCheckedAt, stored.GetValueOrDefault(l.Id)))
            .ToList();
        attention.AddRange(locations
            .Where(l => l.State == StorageLocationState.Offline)
            .Select(l => new AdminAttention("storage.offline", "warning", "storage", l.Name, null)));

        var external = await db.Libraries.AsNoTracking()
            .Where(l => l.Mode == LibraryMode.External)
            .Select(l => new { l.Name, l.State, l.ReadOnly })
            .ToListAsync(ct);
        var libraries = new OverviewLibraries(external.Count, external.Count(l => l.State == LibraryState.Offline), external.Count(l => !l.ReadOnly));
        attention.AddRange(external
            .Where(l => l.State == LibraryState.Offline)
            .Select(l => new AdminAttention("library.offline", "warning", "libraries", l.Name, null)));

        var dayAgo = now.AddDays(-1);
        var active = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Queued || j.State == JobState.Running)
            .GroupBy(j => j.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var failed = await db.Jobs.AsNoTracking().CountAsync(j => j.State == JobState.Failed && j.FinishedAt >= dayAgo, ct);
        var pause = limits.Pause;
        var jobs = new OverviewJobs(
            active.Where(a => a.State == JobState.Running).Sum(a => a.Count),
            active.Where(a => a.State == JobState.Queued).Sum(a => a.Count),
            failed,
            pause.Paused,
            pause.Until);
        if (failed > 0)
        {
            attention.Add(new("jobs.failed", "warning", "background-work", null, failed));
        }

        if (pause.Paused)
        {
            attention.Add(new("jobs.paused", "info", "background-work", null, null));
        }

        var files = BackupFiles.List(backups.Directory);
        var last = files.MaxBy(f => f.CreatedAt);
        var backup = new OverviewBackup(files.Count, last?.CreatedAt, last?.Size);
        if (last is null)
        {
            attention.Add(new("backup.none", "warning", "backups", null, null));
        }
        else if (now - last.CreatedAt > BackupTooOld)
        {
            attention.Add(new("backup.old", "warning", "backups", null, null));
        }

        var report = await ScrubHandler.LastReportAsync(db, ct);
        var running = await db.Jobs.AsNoTracking().AnyAsync(j => j.Type == ScrubHandler.Type && j.State == JobState.Running, ct);
        var problems = report is null ? 0 : report.Missing + report.Damaged;
        var integrity = new OverviewIntegrity(report?.FinishedAt ?? report?.StartedAt, problems, running);
        if (problems > 0)
        {
            attention.Add(new("integrity.problems", "warning", "backups", null, problems));
        }
        else if (report is null)
        {
            attention.Add(new("integrity.none", "info", "backups", null, null));
        }
        else if (now - integrity.LastAt > IntegrityTooOld)
        {
            attention.Add(new("integrity.old", "info", "backups", null, null));
        }

        // Warnings first, each kind together.
        var ordered = attention.OrderBy(a => a.Severity == "warning" ? 0 : 1).ToList();
        return new AdminOverview(ordered, people, locations, libraries, jobs, backup, integrity);
    }
}
