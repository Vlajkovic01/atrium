using Atrium.Core.Jobs;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Jobs;

/// <summary>Removes finished jobs after their retention, so the table stays small on a long-running server.</summary>
public sealed class JobCleanupHandler(AtriumDbContext db, TimeProvider time, JobOptions options) : IJobHandler
{
    public const string Type = "jobs.cleanup";

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromDays(1), JobPriority.Idle, JobResourceClass.Io);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var succeededBefore = now - options.SucceededRetention;
        var failedBefore = now - options.FailedRetention;

        // Children first is not needed: deleting a parent cascades to them.
        await db.Jobs
            .Where(j => j.Id != context.JobId
                && ((j.State == JobState.Succeeded || j.State == JobState.Cancelled) && j.FinishedAt < succeededBefore
                    || j.State == JobState.Failed && j.FinishedAt < failedBefore))
            .ExecuteDeleteAsync(ct);
    }
}
