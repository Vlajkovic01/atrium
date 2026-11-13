using Atrium.Core.Jobs;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Atrium.Jobs.Tests;

/// <summary>
/// The job queue's guarantees, checked against each real database: work is
/// never lost, never run twice at once, retried with backoff, and resumed
/// from its checkpoint after a restart.
/// </summary>
public abstract class JobQueueContractTests : IAsyncLifetime
{
    private JobTestHost? _host;

    protected abstract void UseDatabase(DbContextOptionsBuilder builder);

    protected virtual Task StartDatabaseAsync() => Task.CompletedTask;

    protected virtual Task StopDatabaseAsync() => Task.CompletedTask;

    protected JobTestHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async Task InitializeAsync()
    {
        await StartDatabaseAsync();
        _host = CreateHost();
        await _host.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await StopDatabaseAsync();
    }

    protected JobTestHost CreateHost(JobOptions? options = null, params RecurringJob[] recurring) =>
        new(UseDatabase, options, recurring);

    [Fact]
    public async Task An_enqueued_job_runs_and_succeeds()
    {
        var ran = 0;
        Host.Behaviors.ByType["test.a"] = (_, _) => { ran++; return Task.CompletedTask; };
        var job = await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "a" });

        Assert.True(await Host.RunNextAsync());

        var done = await Host.GetAsync(job.Id);
        Assert.Equal(1, ran);
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(1, done.Attempts);
        Assert.Equal(1.0, done.Progress);
        Assert.Null(done.ActiveDedupeKey);
        Assert.Equal("a", done.DedupeKey);
        Assert.Null(done.LeaseOwner);
        Assert.NotNull(done.FinishedAt);
        Assert.False(await Host.RunNextAsync());
    }

    [Fact]
    public async Task More_urgent_jobs_run_first_and_equal_ones_in_order()
    {
        var order = new List<string>();
        foreach (var type in JobTestHost.TestTypes)
        {
            Host.Behaviors.ByType[type] = (c, _) => { order.Add(c.Job.Type); return Task.CompletedTask; };
        }

        await Host.EnqueueAsync(new JobRequest("test.a") { Priority = JobPriority.Idle });
        await Host.EnqueueAsync(new JobRequest("test.b") { Priority = JobPriority.Background });
        Host.Time.Advance(TimeSpan.FromSeconds(1));
        await Host.EnqueueAsync(new JobRequest("test.c") { Priority = JobPriority.Background });
        await Host.EnqueueAsync(new JobRequest("test.d") { Priority = JobPriority.Interactive });

        while (await Host.RunNextAsync())
        {
        }

        Assert.Equal(["test.d", "test.b", "test.c", "test.a"], order);
    }

    [Fact]
    public async Task A_job_waits_until_its_time()
    {
        await Host.EnqueueAsync(new JobRequest("test.a") { RunAfter = Host.Time.GetUtcNow().AddMinutes(5) });

        Assert.False(await Host.RunNextAsync());
        Host.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await Host.RunNextAsync());
    }

    [Fact]
    public async Task Only_classes_with_free_capacity_are_claimed()
    {
        await Host.EnqueueAsync(new JobRequest("test.a") { ResourceClass = JobResourceClass.Cpu });

        Assert.Null(await Host.Runner.ClaimAsync([JobResourceClass.Io], CancellationToken.None));
        Assert.NotNull(await Host.Runner.ClaimAsync([JobResourceClass.Cpu], CancellationToken.None));
    }

    [Fact]
    public async Task A_second_job_with_the_same_key_is_not_created_while_the_first_is_active()
    {
        var first = await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "scan:1" });
        var second = await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "scan:1" });

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await Host.AllJobsAsync());

        await Host.RunNextAsync();
        var third = await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "scan:1" });

        Assert.NotEqual(first.Id, third.Id);
    }

    [Fact]
    public async Task The_database_refuses_two_active_jobs_with_one_key()
    {
        await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "k" });

        await Host.WithDbAsync(async db =>
        {
            db.Jobs.Add(new Job { Type = "test.a", DedupeKey = "k", ActiveDedupeKey = "k", CreatedAt = Host.Time.GetUtcNow() });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_job()
    {
        await Host.EnqueueAsync(new JobRequest("test.a"));

        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Host.Runner.ClaimAsync(JobTestHost.AllClasses, CancellationToken.None))));

        Assert.Single(claims, c => c is not null);
    }

    [Fact]
    public async Task A_failure_is_retried_later_with_growing_delays()
    {
        var runs = 0;
        Host.Behaviors.ByType["test.a"] = (_, _) =>
            ++runs < 3 ? throw new JobFailedException("test.flaky", "try again") : Task.CompletedTask;
        var job = await Host.EnqueueAsync(new JobRequest("test.a"));
        var start = Host.Time.GetUtcNow();

        await Host.RunNextAsync();
        var afterFirst = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Queued, afterFirst.State);
        Assert.Equal("test.flaky", afterFirst.ErrorCode);
        Assert.Equal(start + TimeSpan.FromSeconds(30), afterFirst.RunAfter);
        Assert.False(await Host.RunNextAsync());

        Host.Time.Advance(TimeSpan.FromSeconds(30));
        await Host.RunNextAsync();
        var afterSecond = await Host.GetAsync(job.Id);
        Assert.Equal(Host.Time.GetUtcNow() + TimeSpan.FromSeconds(60), afterSecond.RunAfter);

        Host.Time.Advance(TimeSpan.FromSeconds(60));
        await Host.RunNextAsync();
        var done = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(3, done.Attempts);
        Assert.Null(done.ErrorCode);
    }

    [Fact]
    public async Task A_job_fails_for_good_after_its_last_attempt()
    {
        Host.Behaviors.ByType["test.a"] = (_, _) => throw new InvalidOperationException("boom");
        var job = await Host.EnqueueAsync(new JobRequest("test.a") { MaxAttempts = 2, DedupeKey = "x" });

        await Host.RunNextAsync();
        Host.Time.Advance(TimeSpan.FromHours(1));
        await Host.RunNextAsync();

        var failed = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal(JobErrorCodes.Unexpected, failed.ErrorCode);
        Assert.Contains("boom", failed.ErrorDetail, StringComparison.Ordinal);
        Assert.Null(failed.ActiveDedupeKey);
        Assert.Equal(2, failed.Attempts);
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried()
    {
        Host.Behaviors.ByType["test.a"] = (_, _) => throw new JobFailedException("test.bad_input", permanent: true);
        var job = await Host.EnqueueAsync(new JobRequest("test.a"));

        await Host.RunNextAsync();

        var failed = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal(1, failed.Attempts);
    }

    [Fact]
    public async Task A_job_without_a_handler_fails_for_good()
    {
        var job = await Host.EnqueueAsync(new JobRequest("test.nobody"));

        await Host.RunNextAsync();

        var failed = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal(JobErrorCodes.UnknownType, failed.ErrorCode);
    }

    [Fact]
    public async Task Jobs_left_running_by_a_crash_are_queued_again_at_startup()
    {
        var job = await Host.EnqueueAsync(new JobRequest("test.a"));
        Assert.NotNull(await Host.Runner.ClaimAsync(JobTestHost.AllClasses, CancellationToken.None));

        // The process dies here: no outcome is recorded. A new one starts.
        await Host.Runner.RecoverAfterRestartAsync(CancellationToken.None);

        var recovered = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Queued, recovered.State);
        Assert.Null(recovered.LeaseOwner);
        Assert.Equal(1, recovered.Attempts);
        Assert.True(await Host.RunNextAsync());
    }

    [Fact]
    public async Task A_job_whose_lease_ran_out_is_queued_again()
    {
        var job = await Host.EnqueueAsync(new JobRequest("test.a"));
        await Host.Runner.ClaimAsync(JobTestHost.AllClasses, CancellationToken.None);

        Assert.Equal(0, await Host.Runner.RecoverExpiredLeasesAsync(CancellationToken.None));
        Host.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await Host.Runner.RecoverExpiredLeasesAsync(CancellationToken.None));

        Assert.Equal(JobState.Queued, (await Host.GetAsync(job.Id)).State);
    }

    [Fact]
    public async Task A_run_that_lost_its_lease_cannot_write_progress_or_an_outcome()
    {
        var job = await Host.EnqueueAsync(new JobRequest("test.a"));
        Host.Behaviors.ByType["test.a"] = async (context, ct) =>
        {
            // Another worker takes the job over while this run is still going.
            await Host.WithDbAsync(db => db.Jobs.Where(j => j.Id == context.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseOwner, "someone-else")));
            await context.SaveCheckpointAsync("stale", ct);
        };

        await Host.RunNextAsync();

        var stored = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Running, stored.State);
        Assert.Equal("someone-else", stored.LeaseOwner);
        Assert.Null(stored.Checkpoint);
    }

    [Fact]
    public async Task Shutdown_returns_the_job_with_its_checkpoint_and_without_using_an_attempt()
    {
        using var stopping = new CancellationTokenSource();
        var seen = new List<string?>();
        Host.Behaviors.ByType["test.a"] = async (context, ct) =>
        {
            seen.Add(context.Checkpoint);
            if (context.Checkpoint is null)
            {
                await context.SaveCheckpointAsync("half", ct);
                await stopping.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var job = await Host.EnqueueAsync(new JobRequest("test.a"));

        await Host.Runner.RunNextAsync(JobTestHost.AllClasses, stopping.Token);

        var interrupted = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Queued, interrupted.State);
        Assert.Equal(0, interrupted.Attempts);
        Assert.Equal("half", interrupted.Checkpoint);

        await Host.RunNextAsync();

        Assert.Equal([null, "half"], seen);
        Assert.Equal(JobState.Succeeded, (await Host.GetAsync(job.Id)).State);
    }

    [Fact]
    public async Task Progress_is_written_while_the_job_runs()
    {
        double? observed = null;
        Host.Behaviors.ByType["test.a"] = async (context, ct) =>
        {
            await context.ReportProgressAsync(0.25, ct);
            observed = (await Host.GetAsync(context.JobId)).Progress;
        };
        await Host.EnqueueAsync(new JobRequest("test.a"));

        await Host.RunNextAsync();

        Assert.Equal(0.25, observed);
    }

    [Fact]
    public async Task A_payload_reaches_the_handler()
    {
        string? received = null;
        Host.Behaviors.ByType["test.a"] = (context, _) =>
        {
            received = context.GetPayload<TestPayload>().Name;
            return Task.CompletedTask;
        };
        await Host.EnqueueAsync(new JobRequest("test.a") { Payload = new TestPayload("Ana") });

        await Host.RunNextAsync();

        Assert.Equal("Ana", received);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_cancels_it_at_once()
    {
        var job = await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "k" });

        Assert.Equal(JobChangeResult.Done, await Host.WithQueueAsync(q => q.CancelAsync(job.Id, CancellationToken.None)));

        var cancelled = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Null(cancelled.ActiveDedupeKey);
        Assert.False(await Host.RunNextAsync());
        Assert.Equal(JobChangeResult.WrongState, await Host.WithQueueAsync(q => q.CancelAsync(job.Id, CancellationToken.None)));
        Assert.Equal(JobChangeResult.NotFound, await Host.WithQueueAsync(q => q.CancelAsync(Guid.NewGuid(), CancellationToken.None)));
    }

    [Fact]
    public async Task Cancelling_a_running_job_stops_its_handler()
    {
        var started = new TaskCompletionSource();
        Host.Behaviors.ByType["test.a"] = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var job = await Host.EnqueueAsync(new JobRequest("test.a"));

        var run = Host.RunNextAsync();
        await started.Task;
        Assert.Equal(JobChangeResult.Done, await Host.WithQueueAsync(q => q.CancelAsync(job.Id, CancellationToken.None)));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(JobState.Cancelled, (await Host.GetAsync(job.Id)).State);
    }

    [Fact]
    public async Task A_failed_job_can_be_retried_with_fresh_attempts()
    {
        var fail = true;
        Host.Behaviors.ByType["test.a"] = (_, _) => fail ? throw new JobFailedException("test.x", permanent: true) : Task.CompletedTask;
        var job = await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "k" });
        await Host.RunNextAsync();

        fail = false;
        Assert.Equal(JobChangeResult.Done, await Host.WithQueueAsync(q => q.RetryAsync(job.Id, CancellationToken.None)));
        var queued = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Queued, queued.State);
        Assert.Equal(0, queued.Attempts);
        Assert.Equal("k", queued.ActiveDedupeKey);
        Assert.Null(queued.ErrorCode);

        await Host.RunNextAsync();
        Assert.Equal(JobState.Succeeded, (await Host.GetAsync(job.Id)).State);
        Assert.Equal(JobChangeResult.WrongState, await Host.WithQueueAsync(q => q.RetryAsync(job.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task Retrying_is_refused_while_an_identical_job_is_active()
    {
        var job = await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "k" });
        await Host.WithQueueAsync(q => q.CancelAsync(job.Id, CancellationToken.None));
        await Host.EnqueueAsync(new JobRequest("test.a") { DedupeKey = "k" });

        Assert.Equal(JobChangeResult.Duplicate, await Host.WithQueueAsync(q => q.RetryAsync(job.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task A_recurring_job_has_one_waiting_instance_due_one_interval_after_the_last_run()
    {
        var schedule = new RecurringJob("test.a", TimeSpan.FromHours(6));
        await using var host = CreateHost(null, schedule);
        var scheduler = host.Services.GetRequiredService<RecurringJobScheduler>();

        await scheduler.EnsureScheduledAsync(CancellationToken.None);
        await scheduler.EnsureScheduledAsync(CancellationToken.None);
        var first = Assert.Single(await host.AllJobsAsync(), j => j.Type == "test.a");
        Assert.Equal(host.Time.GetUtcNow(), first.RunAfter);

        await host.RunNextAsync();
        var finished = host.Time.GetUtcNow();
        host.Time.Advance(TimeSpan.FromMinutes(10));
        await scheduler.EnsureScheduledAsync(CancellationToken.None);

        var next = Assert.Single(await host.AllJobsAsync(), j => j.Type == "test.a" && j.State == JobState.Queued);
        Assert.Equal(finished + TimeSpan.FromHours(6), next.RunAfter);
    }

    [Fact]
    public async Task Cleanup_removes_old_finished_jobs_and_keeps_the_rest()
    {
        var now = Host.Time.GetUtcNow();
        Job Finished(JobState state, TimeSpan age) => new()
        {
            Type = "test.a",
            State = state,
            CreatedAt = now - age,
            FinishedAt = now - age,
        };

        var oldSucceeded = Finished(JobState.Succeeded, TimeSpan.FromDays(8));
        var recentSucceeded = Finished(JobState.Succeeded, TimeSpan.FromDays(6));
        var oldFailed = Finished(JobState.Failed, TimeSpan.FromDays(31));
        var recentFailed = Finished(JobState.Failed, TimeSpan.FromDays(8));
        var queued = new Job { Type = "test.a", State = JobState.Queued, CreatedAt = now - TimeSpan.FromDays(90), RunAfter = now.AddDays(1) };
        await Host.WithDbAsync(async db =>
        {
            db.Jobs.AddRange(oldSucceeded, recentSucceeded, oldFailed, recentFailed, queued);
            await db.SaveChangesAsync();
        });
        await Host.EnqueueAsync(new JobRequest(JobCleanupHandler.Type));

        await Host.RunNextAsync();

        var left = (await Host.AllJobsAsync()).Select(j => j.Id).ToHashSet();
        Assert.DoesNotContain(oldSucceeded.Id, left);
        Assert.DoesNotContain(oldFailed.Id, left);
        Assert.Contains(recentSucceeded.Id, left);
        Assert.Contains(recentFailed.Id, left);
        Assert.Contains(queued.Id, left);
    }

    [Fact]
    public async Task The_worker_runs_everything_and_respects_each_class_limit()
    {
        var options = new JobOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            PollInterval = TimeSpan.FromMilliseconds(50),
            Concurrency = new() { [JobResourceClass.Io] = 2, [JobResourceClass.Cpu] = 1 },
        };
        await using var host = CreateHost(options);
        var running = 0;
        var maxRunning = 0;
        var done = 0;
        var gate = new Lock();
        host.Behaviors.ByType["test.a"] = async (_, ct) =>
        {
            lock (gate)
            {
                running++;
                maxRunning = Math.Max(maxRunning, running);
            }

            await Task.Delay(30, ct);
            lock (gate)
            {
                running--;
                done++;
            }
        };
        for (var i = 0; i < 6; i++)
        {
            await host.EnqueueAsync(new JobRequest("test.a") { ResourceClass = JobResourceClass.Io });
        }

        var worker = host.Services.GetServices<IHostedService>().OfType<JobWorker>().SingleOrDefault()
            ?? ActivatorUtilities.CreateInstance<JobWorker>(host.Services);
        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref done) < 6 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(6, done);
        Assert.InRange(maxRunning, 1, 2);
        // The worker also schedules its own housekeeping job; only the test's jobs are checked.
        Assert.All(
            (await host.AllJobsAsync()).Where(j => j.Type == "test.a"),
            j => Assert.Equal(JobState.Succeeded, j.State));
    }

    [Fact]
    public async Task While_background_work_is_paused_only_jobs_for_people_run()
    {
        var limits = Host.Services.GetRequiredService<JobLimits>();
        var background = await Host.EnqueueAsync(new JobRequest("test.a") { Priority = JobPriority.Background });
        var idle = await Host.EnqueueAsync(new JobRequest("test.b") { Priority = JobPriority.Idle });
        var user = await Host.EnqueueAsync(new JobRequest("test.c") { Priority = JobPriority.User });

        limits.PauseBackground(until: null);

        Assert.True(await Host.RunNextAsync());
        Assert.False(await Host.RunNextAsync());
        Assert.Equal(JobState.Succeeded, (await Host.GetAsync(user.Id)).State);
        Assert.Equal(JobState.Queued, (await Host.GetAsync(background.Id)).State);
        Assert.Equal(JobState.Queued, (await Host.GetAsync(idle.Id)).State);

        limits.ResumeBackground();

        Assert.True(await Host.RunNextAsync());
        Assert.True(await Host.RunNextAsync());
    }

    [Fact]
    public async Task A_pause_with_an_end_ends_by_itself()
    {
        var limits = Host.Services.GetRequiredService<JobLimits>();
        await Host.EnqueueAsync(new JobRequest("test.a") { Priority = JobPriority.Background });

        limits.PauseBackground(Host.Time.GetUtcNow().AddHours(1));
        Assert.False(await Host.RunNextAsync());

        Host.Time.Advance(TimeSpan.FromHours(1));
        Assert.False(limits.Pause.Paused);
        Assert.True(await Host.RunNextAsync());
    }

    [Fact]
    public async Task A_pause_stops_a_running_background_job_without_using_an_attempt()
    {
        var limits = Host.Services.GetRequiredService<JobLimits>();
        var seen = new List<string?>();
        Host.Behaviors.ByType["test.a"] = async (context, ct) =>
        {
            seen.Add(context.Checkpoint);
            if (context.Checkpoint is null)
            {
                await context.SaveCheckpointAsync("half", ct);
                limits.PauseBackground(until: null);
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var job = await Host.EnqueueAsync(new JobRequest("test.a") { Priority = JobPriority.Background });

        await Host.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var paused = await Host.GetAsync(job.Id);
        Assert.Equal(JobState.Queued, paused.State);
        Assert.Equal(0, paused.Attempts);
        Assert.Equal("half", paused.Checkpoint);

        limits.ResumeBackground();
        await Host.RunNextAsync();

        Assert.Equal([null, "half"], seen);
        Assert.Equal(JobState.Succeeded, (await Host.GetAsync(job.Id)).State);
    }

    [Fact]
    public async Task A_pause_does_not_stop_a_job_someone_waits_for()
    {
        var limits = Host.Services.GetRequiredService<JobLimits>();
        Host.Behaviors.ByType["test.a"] = async (_, ct) =>
        {
            limits.PauseBackground(until: null);
            await Task.Delay(50, ct);
        };
        var job = await Host.EnqueueAsync(new JobRequest("test.a") { Priority = JobPriority.Interactive });

        await Host.RunNextAsync();

        Assert.Equal(JobState.Succeeded, (await Host.GetAsync(job.Id)).State);
    }

    [Fact]
    public async Task Limits_and_pause_survive_a_restart()
    {
        var until = Host.Time.GetUtcNow().AddHours(2);
        await using (var scope = Host.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<JobSettingsStore>();
            await store.SaveLimitsAsync(new Dictionary<JobResourceClass, int> { [JobResourceClass.Cpu] = 5 }, CancellationToken.None);
            await store.PauseAsync(until, CancellationToken.None);
        }

        await using var restarted = CreateHost();
        await using (var scope = restarted.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobSettingsStore>().LoadAsync(CancellationToken.None);
        }

        var limits = restarted.Services.GetRequiredService<JobLimits>();
        Assert.Equal(5, limits.LimitFor(JobResourceClass.Cpu));
        Assert.Equal(new PauseState(true, until), limits.Pause);

        await using (var scope = restarted.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobSettingsStore>().ResumeAsync(CancellationToken.None);
        }

        await using var again = CreateHost();
        await using (var scope = again.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobSettingsStore>().LoadAsync(CancellationToken.None);
        }

        Assert.False(again.Services.GetRequiredService<JobLimits>().IsBackgroundPaused);
    }

    private sealed record TestPayload(string Name);
}
