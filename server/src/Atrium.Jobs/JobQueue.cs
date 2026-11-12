using System.Data.Common;
using System.Text.Json;
using Atrium.Core.Jobs;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Jobs;

public enum JobChangeResult
{
    Done,
    NotFound,

    /// <summary>The job is not in a state that allows this change.</summary>
    WrongState,

    /// <summary>Retrying would create a second active job with the same dedupe key.</summary>
    Duplicate,
}

/// <summary>Creates, cancels and retries jobs. Running them is <see cref="JobRunner"/>'s work.</summary>
public sealed class JobQueue(AtriumDbContext db, TimeProvider time, JobSignal signal)
{
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Adds a job, or returns the active job with the same dedupe key if
    /// there is one. Saved immediately, independent of other pending
    /// changes in the context: a job is never lost to a later rollback.
    /// </summary>
    public async Task<Job> EnqueueAsync(JobRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);
        if (request.DedupeKey is { } key && await FindActiveAsync(key, ct) is { } existing)
        {
            return existing;
        }

        var now = time.GetUtcNow();
        var job = new Job
        {
            Type = request.Type,
            Payload = request.Payload is null ? null : JsonSerializer.Serialize(request.Payload, PayloadJson),
            Priority = request.Priority,
            ResourceClass = request.ResourceClass,
            State = JobState.Queued,
            OwnerUserId = request.OwnerUserId,
            ParentJobId = request.ParentJobId,
            DedupeKey = request.DedupeKey,
            ActiveDedupeKey = request.DedupeKey,
            MaxAttempts = Math.Max(1, request.MaxAttempts),
            RunAfter = request.RunAfter ?? now,
            CreatedAt = now,
        };

        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (request.DedupeKey is { } raced)
        {
            // Someone created the same job between the check and the insert.
            db.Entry(job).State = EntityState.Detached;
            return await FindActiveAsync(raced, ct)
                ?? throw new InvalidOperationException($"Job with key '{raced}' could not be created or found.");
        }

        signal.Notify();
        return job;
    }

    /// <summary>
    /// A queued job is cancelled at once. A running one is asked to stop and
    /// is marked cancelled by its worker at the next heartbeat.
    /// </summary>
    public async Task<JobChangeResult> CancelAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var queued = await db.Jobs
            .Where(j => j.Id == id && j.State == JobState.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Cancelled)
                .SetProperty(j => j.ActiveDedupeKey, (string?)null)
                .SetProperty(j => j.FinishedAt, now), ct);
        if (queued == 1)
        {
            return JobChangeResult.Done;
        }

        var running = await db.Jobs
            .Where(j => j.Id == id && j.State == JobState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequested, true), ct);
        if (running == 1)
        {
            return JobChangeResult.Done;
        }

        return await db.Jobs.AnyAsync(j => j.Id == id, ct) ? JobChangeResult.WrongState : JobChangeResult.NotFound;
    }

    /// <summary>Queues a failed or cancelled job again with a fresh set of attempts.</summary>
    public async Task<JobChangeResult> RetryAsync(Guid id, CancellationToken ct)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return JobChangeResult.NotFound;
        }

        if (job.State is not (JobState.Failed or JobState.Cancelled))
        {
            return JobChangeResult.WrongState;
        }

        if (job.DedupeKey is { } key && await FindActiveAsync(key, ct) is not null)
        {
            return JobChangeResult.Duplicate;
        }

        int changed;
        try
        {
            changed = await db.Jobs
                .Where(j => j.Id == id && (j.State == JobState.Failed || j.State == JobState.Cancelled))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Queued)
                    .SetProperty(j => j.ActiveDedupeKey, job.DedupeKey)
                    .SetProperty(j => j.Attempts, 0)
                    .SetProperty(j => j.CancelRequested, false)
                    .SetProperty(j => j.RunAfter, time.GetUtcNow())
                    .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.ErrorCode, (string?)null)
                    .SetProperty(j => j.ErrorDetail, (string?)null), ct);
        }
        catch (DbException) when (job.DedupeKey is not null)
        {
            // The unique key refused it: an identical job became active meanwhile.
            return JobChangeResult.Duplicate;
        }

        if (changed == 0)
        {
            return JobChangeResult.WrongState;
        }

        signal.Notify();
        return JobChangeResult.Done;
    }

    private Task<Job?> FindActiveAsync(string dedupeKey, CancellationToken ct) =>
        db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.ActiveDedupeKey == dedupeKey, ct);
}
