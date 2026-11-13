using Atrium.Core.Jobs;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atrium.Jobs;

/// <summary>
/// Claims jobs and runs them: one claim, one handler run, one outcome. The
/// worker loop (<see cref="JobWorker"/>) decides when and how many; tests
/// call this directly so they need no timing.
/// </summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes,
    TimeProvider time,
    JobOptions options,
    JobLimits limits,
    IEnumerable<JobHandlerRegistration> registrations,
    ILogger<JobRunner> logger)
{
    private readonly Dictionary<string, Type> _handlers =
        registrations.ToDictionary(r => r.Type, r => r.HandlerType, StringComparer.Ordinal);

    /// <summary>
    /// Returns jobs that were running when the server stopped to the queue.
    /// Only one server runs on a database (InstanceLock), so at startup no
    /// job can really be running. The attempt stays counted: a job that
    /// crashes the whole server must eventually stop being retried.
    /// </summary>
    public async Task RecoverAfterRestartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var recovered = await db.Jobs
            .Where(j => j.State == JobState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Queued)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), ct);
        if (recovered > 0)
        {
            logger.LogInformation("Returned {Count} interrupted jobs to the queue.", recovered);
        }
    }

    /// <summary>Returns running jobs whose lease ran out to the queue, for example after a hung heartbeat.</summary>
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var now = time.GetUtcNow();
        return await db.Jobs
            .Where(j => j.State == JobState.Running && j.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Queued)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), ct);
    }

    /// <summary>
    /// Claims the most urgent job of one of the given classes. The claim is
    /// a conditional update, so two workers can never both win the same job,
    /// on SQLite and PostgreSQL alike. While background work is paused only
    /// interactive and user jobs are claimed.
    /// </summary>
    public async Task<Job?> ClaimAsync(IReadOnlyCollection<JobResourceClass> classes, CancellationToken ct)
    {
        if (classes.Count == 0)
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var now = time.GetUtcNow();
        var query = db.Jobs.Where(j => j.State == JobState.Queued && j.RunAfter <= now && classes.Contains(j.ResourceClass));
        if (limits.IsBackgroundPaused)
        {
            query = query.Where(j => j.Priority < JobPriority.Background);
        }

        var candidates = await query
            .OrderBy(j => j.Priority)
            .ThenBy(j => j.RunAfter)
            .ThenBy(j => j.Id)
            .Select(j => j.Id)
            .Take(8)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            var owner = Guid.NewGuid().ToString("N");
            var leaseExpires = now + options.LeaseDuration;
            var claimed = await db.Jobs
                .Where(j => j.Id == id && j.State == JobState.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Running)
                    .SetProperty(j => j.LeaseOwner, owner)
                    .SetProperty(j => j.LeaseExpiresAt, leaseExpires)
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.StartedAt, now), ct);
            if (claimed == 1)
            {
                return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, ct);
            }
        }

        return null;
    }

    /// <summary>Claims and runs one job inline. Returns false when nothing was ready.</summary>
    public async Task<bool> RunNextAsync(IReadOnlyCollection<JobResourceClass> classes, CancellationToken ct)
    {
        var job = await ClaimAsync(classes, ct);
        if (job is null)
        {
            return false;
        }

        await RunAsync(job, ct);
        return true;
    }

    /// <summary>
    /// Runs a claimed job to an outcome. <paramref name="stopping"/> is the
    /// server shutting down: the job then goes back to the queue unharmed.
    /// </summary>
    public async Task RunAsync(Job job, CancellationToken stopping)
    {
        var owner = job.LeaseOwner ?? throw new InvalidOperationException("The job was not claimed.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        // A pause of background work stops a background run the same way
        // shutdown does: back to the queue, without using an attempt.
        var paused = JobLimits.IsBackground(job.Priority) ? limits.BackgroundStopToken : CancellationToken.None;
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(stopping, paused);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(interrupt.Token);
        var context = new JobContext(job, owner, db, time, options);
        using var heartbeat = new CancellationTokenSource();
        var heartbeatTask = HeartbeatAsync(job.Id, owner, run, heartbeat.Token);

        Outcome outcome;
        try
        {
            if (!_handlers.TryGetValue(job.Type, out var handlerType))
            {
                throw new JobFailedException(JobErrorCodes.UnknownType, $"No handler for job type '{job.Type}'.", permanent: true);
            }

            var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(handlerType);
            await handler.RunAsync(context, run.Token);
            outcome = Outcome.Success();
        }
        catch (JobLeaseLostException)
        {
            outcome = Outcome.LeaseLost();
        }
        catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
        {
            outcome = Outcome.Interrupted();
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // Not the server stopping, so the heartbeat cancelled it: a cancel
            // request, or the lease went to someone else.
            outcome = Outcome.CancelledOrLost();
        }
        catch (JobFailedException failure)
        {
            outcome = Outcome.Failure(failure.Code, failure.Detail, failure.Permanent);
            logger.LogWarning("Job {JobId} of type {Type} failed: {Code}.", job.Id, job.Type, failure.Code);
        }
        catch (Exception exception)
        {
            outcome = Outcome.Failure(JobErrorCodes.Unexpected, $"{exception.GetType().Name}: {exception.Message}", permanent: false);
            logger.LogError(exception, "Job {JobId} of type {Type} failed unexpectedly.", job.Id, job.Type);
        }
        finally
        {
            await heartbeat.CancelAsync();
            await heartbeatTask;
        }

        // The outcome is recorded even while the server stops: it is short,
        // and losing it would run a finished job again.
        await RecordAsync(job, owner, outcome, CancellationToken.None);
    }

    private async Task HeartbeatAsync(Guid id, string owner, CancellationTokenSource run, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.HeartbeatInterval, time, ct);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
                var leaseExpires = time.GetUtcNow() + options.LeaseDuration;
                var renewed = await db.Jobs
                    .Where(j => j.Id == id && j.State == JobState.Running && j.LeaseOwner == owner && !j.CancelRequested)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, leaseExpires), ct);
                if (renewed == 0)
                {
                    // Cancel requested, or the lease is gone: stop the handler.
                    await run.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A heartbeat that cannot reach the database lets the lease run
            // out; the job is then retried. Nothing else is needed here.
            logger.LogWarning(exception, "Heartbeat for job {JobId} failed.", id);
        }
    }

    private async Task RecordAsync(Job job, string owner, Outcome outcome, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var now = time.GetUtcNow();
        var mine = db.Jobs.Where(j => j.Id == job.Id && j.State == JobState.Running && j.LeaseOwner == owner);

        switch (outcome.Kind)
        {
            case OutcomeKind.Success:
                await mine.ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Succeeded)
                    .SetProperty(j => j.Progress, 1.0)
                    .SetProperty(j => j.ActiveDedupeKey, (string?)null)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.ErrorCode, (string?)null)
                    .SetProperty(j => j.ErrorDetail, (string?)null)
                    .SetProperty(j => j.FinishedAt, now), ct);
                break;

            case OutcomeKind.Interrupted:
                // Shutdown is not the job's fault, so this run does not count.
                await mine.ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Queued)
                    .SetProperty(j => j.Attempts, j => j.Attempts - 1)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), ct);
                break;

            case OutcomeKind.CancelledOrLost:
                await mine.Where(j => j.CancelRequested).ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Cancelled)
                    .SetProperty(j => j.ActiveDedupeKey, (string?)null)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.FinishedAt, now), ct);
                break;

            case OutcomeKind.LeaseLost:
                // Another worker owns the job now; it records the outcome.
                break;

            case OutcomeKind.Failure:
                var detail = outcome.Detail is { Length: > 4000 } text ? text[..4000] : outcome.Detail;
                var final = outcome.Permanent || job.Attempts >= job.MaxAttempts;
                if (final)
                {
                    await mine.ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.State, JobState.Failed)
                        .SetProperty(j => j.ActiveDedupeKey, (string?)null)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(j => j.ErrorCode, outcome.Code)
                        .SetProperty(j => j.ErrorDetail, detail)
                        .SetProperty(j => j.FinishedAt, now), ct);
                }
                else
                {
                    var runAfter = now + options.BackoffAfter(job.Attempts);
                    await mine.ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.State, JobState.Queued)
                        .SetProperty(j => j.RunAfter, runAfter)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(j => j.ErrorCode, outcome.Code)
                        .SetProperty(j => j.ErrorDetail, detail), ct);
                }

                break;
        }
    }

    private enum OutcomeKind
    {
        Success,
        Failure,
        Interrupted,
        CancelledOrLost,
        LeaseLost,
    }

    private sealed record Outcome(OutcomeKind Kind, string? Code = null, string? Detail = null, bool Permanent = false)
    {
        public static Outcome Success() => new(OutcomeKind.Success);

        public static Outcome Failure(string code, string? detail, bool permanent) => new(OutcomeKind.Failure, code, detail, permanent);

        public static Outcome Interrupted() => new(OutcomeKind.Interrupted);

        public static Outcome CancelledOrLost() => new(OutcomeKind.CancelledOrLost);

        public static Outcome LeaseLost() => new(OutcomeKind.LeaseLost);
    }
}
