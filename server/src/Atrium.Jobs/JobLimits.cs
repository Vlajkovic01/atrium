using Atrium.Core.Jobs;

namespace Atrium.Jobs;

/// <summary>
/// The limits the workers obey right now: how many jobs of each class run
/// at once, and whether background work is paused. Defaults come from the
/// hardware profile; an admin can override each class and pause background
/// work. Changes take effect without a restart.
/// </summary>
/// <remarks>
/// Pausing affects jobs of <see cref="JobPriority.Background"/> and
/// <see cref="JobPriority.Idle"/> priority only. Work a person started or
/// waits for keeps running. Background jobs that are running when a pause
/// begins are stopped at once and go back to the queue without using an
/// attempt, the same as at shutdown; they continue from their checkpoint.
/// </remarks>
public sealed class JobLimits
{
    /// <summary>Highest limit an admin can set for one class.</summary>
    public const int MaxLimit = 32;

    private readonly TimeProvider _time;
    private readonly JobSignal _signal;
    private readonly Lock _gate = new();
    private readonly Dictionary<JobResourceClass, int> _overrides = [];
    private CancellationTokenSource _backgroundStop = new();
    private PauseState _pause = PauseState.None;
    private int _holds;

    public JobLimits(HardwareInfo hardware, JobOptions options, TimeProvider time, JobSignal signal)
    {
        Hardware = hardware;
        _time = time;
        _signal = signal;
        var defaults = hardware.DefaultLimits();
        Defaults = options.Concurrency is { } fixedLimits
            ? Enum.GetValues<JobResourceClass>().ToDictionary(c => c, c => fixedLimits.GetValueOrDefault(c, defaults[c]))
            : defaults;
    }

    public HardwareInfo Hardware { get; }

    public HardwareProfile Profile => Hardware.Profile;

    public IReadOnlyDictionary<JobResourceClass, int> Defaults { get; }

    public IReadOnlyDictionary<JobResourceClass, int> Overrides
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<JobResourceClass, int>(_overrides);
            }
        }
    }

    /// <summary>The manual pause an admin set, if any. Expired pauses read as none.</summary>
    public PauseState Pause
    {
        get
        {
            lock (_gate)
            {
                return CurrentPause();
            }
        }
    }

    /// <summary>True while background and idle jobs must not start.</summary>
    public bool IsBackgroundPaused
    {
        get
        {
            lock (_gate)
            {
                return CurrentPause().Paused || _holds > 0;
            }
        }
    }

    public int LimitFor(JobResourceClass resourceClass)
    {
        lock (_gate)
        {
            return _overrides.TryGetValue(resourceClass, out var limit) ? limit : Defaults.GetValueOrDefault(resourceClass, 1);
        }
    }

    /// <summary>Replaces the admin's overrides; a class left out uses its default.</summary>
    public void SetOverrides(IReadOnlyDictionary<JobResourceClass, int> overrides)
    {
        foreach (var limit in overrides.Values)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
        }

        lock (_gate)
        {
            _overrides.Clear();
            foreach (var (resourceClass, limit) in overrides)
            {
                _overrides[resourceClass] = limit;
            }
        }

        // Higher limits may let waiting jobs start now.
        _signal.Notify();
    }

    /// <summary>Pauses background work until <paramref name="until"/>, or until resumed when null.</summary>
    public void PauseBackground(DateTimeOffset? until)
    {
        CancellationTokenSource stop;
        lock (_gate)
        {
            _pause = new PauseState(true, until);
            stop = _backgroundStop;
        }

        stop.Cancel();
    }

    public void ResumeBackground()
    {
        lock (_gate)
        {
            _pause = PauseState.None;
        }

        _signal.Notify();
    }

    /// <summary>
    /// Pauses background work for as long as the returned object is not
    /// disposed, independent of the admin's pause. Used by work that needs
    /// the machine for a person waiting on it, such as a live transcode.
    /// </summary>
    public IDisposable HoldBackground()
    {
        CancellationTokenSource stop;
        lock (_gate)
        {
            _holds++;
            stop = _backgroundStop;
        }

        stop.Cancel();
        return new Hold(this);
    }

    /// <summary>
    /// A token cancelled when background work is paused. A run of a
    /// background job listens to it; a pause stops the run and returns the
    /// job to the queue.
    /// </summary>
    public CancellationToken BackgroundStopToken
    {
        get
        {
            lock (_gate)
            {
                if (_backgroundStop.IsCancellationRequested && !CurrentPause().Paused && _holds == 0)
                {
                    _backgroundStop.Dispose();
                    _backgroundStop = new CancellationTokenSource();
                }

                return _backgroundStop.Token;
            }
        }
    }

    public static bool IsBackground(JobPriority priority) => priority >= JobPriority.Background;

    private PauseState CurrentPause() =>
        _pause.Paused && _pause.Until is { } until && until <= _time.GetUtcNow() ? PauseState.None : _pause;

    private void Release()
    {
        lock (_gate)
        {
            _holds--;
        }

        _signal.Notify();
    }

    private sealed class Hold(JobLimits owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}

/// <param name="Paused">Background work is paused.</param>
/// <param name="Until">When the pause ends by itself; null means until resumed.</param>
public sealed record PauseState(bool Paused, DateTimeOffset? Until)
{
    public static PauseState None { get; } = new(false, null);
}
