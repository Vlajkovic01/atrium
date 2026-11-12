namespace Atrium.Core.Jobs;

/// <summary>
/// Order in which waiting jobs are started. Stored as its number, not its
/// name (an exception to D-020): the order is the whole meaning, and the
/// database sorts by it.
/// </summary>
public enum JobPriority
{
    /// <summary>Someone is waiting on the screen for the result.</summary>
    Interactive = 0,

    /// <summary>Started by a person, not awaited.</summary>
    User = 10,

    /// <summary>Housekeeping the server schedules itself.</summary>
    Background = 20,

    /// <summary>Runs only when nothing else is waiting.</summary>
    Idle = 30,
}

/// <summary>What a job mostly uses. Each class has its own concurrency limit.</summary>
public enum JobResourceClass
{
    Cpu,
    Io,
    Network,
    Transcode,
    Gpu,
}

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// A unit of background work, persisted so that a restart loses nothing
/// (DESIGN.md 5). Workers claim a job by setting a lease; a lease that is
/// not renewed returns the job to the queue.
/// </summary>
public sealed class Job
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Name of the handler that runs it, for example <c>jobs.cleanup</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Handler input as JSON. Never shown to anyone but the handler.</summary>
    public string? Payload { get; init; }

    public JobPriority Priority { get; init; }

    public JobResourceClass ResourceClass { get; init; }

    public JobState State { get; set; }

    /// <summary>The user the job works for, who may see and cancel it. Null for server housekeeping.</summary>
    public Guid? OwnerUserId { get; init; }

    public Guid? ParentJobId { get; init; }

    /// <summary>
    /// While the job is queued or running, a second job with the same key is
    /// not created (for example two scans of one library). Kept after the job
    /// ends, for history.
    /// </summary>
    public string? DedupeKey { get; init; }

    /// <summary>
    /// Equal to <see cref="DedupeKey"/> while the job is queued or running,
    /// null afterwards. Unique, so the database itself refuses a duplicate.
    /// </summary>
    public string? ActiveDedupeKey { get; set; }

    /// <summary>From 0 to 1, when the handler can tell.</summary>
    public double? Progress { get; set; }

    /// <summary>Opaque state the handler saves so a restart continues where it stopped.</summary>
    public string? Checkpoint { get; set; }

    /// <summary>Times the job was started, including the current run.</summary>
    public int Attempts { get; set; }

    public int MaxAttempts { get; init; } = 5;

    /// <summary>Not started before this time. Used for retry backoff and scheduling.</summary>
    public DateTimeOffset RunAfter { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Set by a cancel request while the job runs; the worker stops it at the next heartbeat.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>Stable error code of the last failure, for clients to translate.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Technical detail of the last failure. Shown to the owner and, for server jobs, to admins.</summary>
    public string? ErrorDetail { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsActive => State is JobState.Queued or JobState.Running;
}
