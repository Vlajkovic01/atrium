using Atrium.Api.Auth;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Files.Blobs;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Report">The last finished check; null before the first one.</param>
/// <param name="RunningJobId">A check in progress, if any.</param>
public sealed record IntegrityStatus(IntegrityReport? Report, Guid? RunningJobId);

/// <summary>The storage integrity report (DESIGN.md 4.5, release gate item 5). Admins only.</summary>
public static class IntegrityEndpoints
{
    private static readonly string ScrubKey = ScrubHandler.Schedule.DedupeKey;

    public static IEndpointRouteBuilder MapIntegrityEndpoints(this IEndpointRouteBuilder routes)
    {
        var integrity = routes.MapGroup("/admin/integrity")
            .WithTags("Administration")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);

        integrity.MapGet("/", async (AtriumDbContext db, CancellationToken ct) =>
            {
                var running = await db.Jobs.AsNoTracking()
                    .Where(j => j.Type == ScrubHandler.Type && j.State == JobState.Running)
                    .Select(j => (Guid?)j.Id)
                    .FirstOrDefaultAsync(ct);
                return new IntegrityStatus(await ScrubHandler.LastReportAsync(db, ct), running);
            })
            .WithName("GetIntegrity");

        // Runs the weekly check now: the waiting instance is moved up rather
        // than a second check started.
        integrity.MapPost("/check", async (AtriumDbContext db, JobQueue queue, TimeProvider time, CancellationToken ct) =>
            {
                var now = time.GetUtcNow();
                await db.Jobs
                    .Where(j => j.ActiveDedupeKey == ScrubKey && j.State == JobState.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.RunAfter, now).SetProperty(j => j.Priority, JobPriority.User), ct);
                var job = await queue.EnqueueAsync(
                    new JobRequest(ScrubHandler.Type) { DedupeKey = ScrubKey, Priority = JobPriority.User, ResourceClass = JobResourceClass.Io },
                    ct);
                return TypedResults.Accepted($"/api/v1/jobs/{job.Id}", new JobStarted(job.Id));
            })
            .WithName("StartIntegrityCheck");

        return routes;
    }
}
