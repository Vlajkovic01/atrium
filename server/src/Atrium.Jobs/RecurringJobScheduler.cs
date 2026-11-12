using Atrium.Core.Jobs;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Jobs;

/// <summary>
/// Keeps exactly one waiting instance of each recurring job. The next run is
/// due one interval after the last one finished, whatever its outcome, so a
/// job that keeps failing does not run more often than planned. Everything
/// is derived from the jobs table: nothing to lose in a restart.
/// </summary>
public sealed class RecurringJobScheduler(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IEnumerable<RecurringJob> recurring)
{
    public async Task EnsureScheduledAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        foreach (var definition in recurring)
        {
            var key = definition.DedupeKey;
            if (await db.Jobs.AnyAsync(j => j.ActiveDedupeKey == key, ct))
            {
                continue;
            }

            var lastFinished = await db.Jobs
                .Where(j => j.DedupeKey == key && j.FinishedAt != null)
                .OrderByDescending(j => j.FinishedAt)
                .Select(j => j.FinishedAt)
                .FirstOrDefaultAsync(ct);

            var now = time.GetUtcNow();
            var due = lastFinished is { } last ? last + definition.Interval : now;
            await queue.EnqueueAsync(
                new JobRequest(definition.Type)
                {
                    Priority = definition.Priority,
                    ResourceClass = definition.ResourceClass,
                    DedupeKey = key,
                    RunAfter = due > now ? due : now,
                },
                ct);
        }
    }
}
