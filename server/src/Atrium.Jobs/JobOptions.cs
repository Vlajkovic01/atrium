using Atrium.Core.Jobs;

namespace Atrium.Jobs;

/// <summary>Tuning of the workers. Defaults suit the smallest supported device (D-003).</summary>
public sealed class JobOptions
{
    /// <summary>
    /// Fixed default limits per class instead of those of the hardware
    /// profile (<see cref="HardwareInfo.DefaultLimits"/>). For tests; an
    /// admin changes limits through <see cref="JobLimits"/>.
    /// </summary>
    public Dictionary<JobResourceClass, int>? Concurrency { get; init; }

    /// <summary>A running job whose lease is not renewed for this long is returned to the queue.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Fallback when no in-process signal arrives, for example for a job whose RunAfter has passed.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>First retry delay; doubles with each attempt up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Progress is written at most this often, so a chatty handler cannot flood the database.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Finished jobs are kept this long for the activity view, then removed.</summary>
    public TimeSpan SucceededRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Failed jobs are kept longer, so an admin can still find out what went wrong.</summary>
    public TimeSpan FailedRetention { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan BackoffAfter(int attempts)
    {
        var factor = Math.Pow(2, Math.Clamp(attempts - 1, 0, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryDelay.Ticks * factor, MaxRetryDelay.Ticks));
        return delay;
    }
}

/// <summary>Wakes the workers when a job is enqueued, so they need not wait for the next poll.</summary>
public sealed class JobSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        // At most one pending wake-up; more would be redundant.
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another thread released it first.
            }
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _signal.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
