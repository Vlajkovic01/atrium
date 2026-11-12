using Atrium.Core.Jobs;

namespace Atrium.Jobs;

/// <summary>
/// Runs one type of job. Handlers must be idempotent and resumable: running
/// one twice, or resuming from its last checkpoint, gives the same result
/// (DESIGN.md 5.2). A job can run again after a crash or restart.
/// </summary>
public interface IJobHandler
{
    /// <summary>
    /// Does the work. Throw <see cref="JobFailedException"/> for a known
    /// failure; any other exception is retried as unexpected. The token is
    /// cancelled on shutdown and when someone cancels the job.
    /// </summary>
    Task RunAsync(JobContext context, CancellationToken ct);
}

/// <summary>A job to create.</summary>
public sealed record JobRequest(string Type)
{
    /// <summary>Handler input, serialized as JSON.</summary>
    public object? Payload { get; init; }

    public JobPriority Priority { get; init; } = JobPriority.Background;

    public JobResourceClass ResourceClass { get; init; } = JobResourceClass.Io;

    public Guid? OwnerUserId { get; init; }

    public Guid? ParentJobId { get; init; }

    /// <summary>While a job with this key is queued or running, no second one is created.</summary>
    public string? DedupeKey { get; init; }

    /// <summary>Not before this time; now when null.</summary>
    public DateTimeOffset? RunAfter { get; init; }

    public int MaxAttempts { get; init; } = 5;
}

/// <summary>A job that runs on its own every <paramref name="Interval"/>.</summary>
public sealed record RecurringJob(
    string Type,
    TimeSpan Interval,
    JobPriority Priority = JobPriority.Background,
    JobResourceClass ResourceClass = JobResourceClass.Io)
{
    /// <summary>The dedupe key that keeps exactly one instance waiting.</summary>
    public string DedupeKey => $"recurring:{Type}";
}

/// <summary>Links a job type to the handler class that runs it.</summary>
public sealed record JobHandlerRegistration(string Type, Type HandlerType);

/// <summary>
/// A failure the handler understands. <paramref name="Code"/> is a stable
/// error code clients translate. A permanent failure is not retried.
/// </summary>
public sealed class JobFailedException(string code, string? detail = null, bool permanent = false)
    : Exception(detail ?? code)
{
    public string Code { get; } = code;

    public string? Detail { get; } = detail;

    public bool Permanent { get; } = permanent;
}

/// <summary>Error codes the queue itself records on jobs.</summary>
public static class JobErrorCodes
{
    public const string Unexpected = "job.unexpected_error";
    public const string UnknownType = "job.unknown_type";
    public const string LeaseLost = "job.lease_lost";
}
