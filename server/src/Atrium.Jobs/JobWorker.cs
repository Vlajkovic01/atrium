using Atrium.Core.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atrium.Jobs;

/// <summary>
/// The worker loop in the server process: keeps each resource class busy up
/// to its limit, wakes on a signal when a job is enqueued, and polls as a
/// fallback. On shutdown it waits for running jobs to hand their work back.
/// </summary>
public sealed class JobWorker(
    JobRunner runner,
    RecurringJobScheduler scheduler,
    JobSignal signal,
    JobOptions options,
    TimeProvider time,
    ILogger<JobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<JobResourceClass, int> _running = [];
    private readonly List<Task> _tasks = [];
    private readonly Lock _gate = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the server finish starting before background work competes with it.
        await Task.Yield();

        await runner.RecoverAfterRestartAsync(stoppingToken);
        var nextHousekeeping = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (time.GetUtcNow() >= nextHousekeeping)
                {
                    await runner.RecoverExpiredLeasesAsync(stoppingToken);
                    await scheduler.EnsureScheduledAsync(stoppingToken);
                    nextHousekeeping = time.GetUtcNow() + HousekeepingInterval;
                }

                var started = await StartWhatFitsAsync(stoppingToken);
                if (!started)
                {
                    await signal.WaitAsync(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database may be briefly unavailable; keep the loop alive.
                logger.LogError(exception, "The job worker loop failed; retrying.");
                await signal.WaitAsync(options.PollInterval, stoppingToken);
            }
        }

        Task[] running;
        lock (_gate)
        {
            running = [.. _tasks];
        }

        await Task.WhenAll(running);
    }

    private async Task<bool> StartWhatFitsAsync(CancellationToken stoppingToken)
    {
        var free = FreeClasses();
        if (free.Count == 0)
        {
            return false;
        }

        var job = await runner.ClaimAsync(free, stoppingToken);
        if (job is null)
        {
            return false;
        }

        lock (_gate)
        {
            _running[job.ResourceClass] = _running.GetValueOrDefault(job.ResourceClass) + 1;
            Task? task = null;
            task = Task.Run(async () =>
            {
                try
                {
                    await runner.RunAsync(job, stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Recording the outcome of job {JobId} failed.", job.Id);
                }
                finally
                {
                    lock (_gate)
                    {
                        _running[job.ResourceClass]--;
                        _tasks.Remove(task!);
                    }

                    signal.Notify();
                }
            }, CancellationToken.None);
            _tasks.Add(task);
        }

        return true;
    }

    private List<JobResourceClass> FreeClasses()
    {
        lock (_gate)
        {
            return Enum.GetValues<JobResourceClass>()
                .Where(c => _running.GetValueOrDefault(c) < options.LimitFor(c))
                .ToList();
        }
    }
}
