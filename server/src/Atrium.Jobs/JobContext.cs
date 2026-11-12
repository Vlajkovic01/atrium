using System.Text.Json;
using Atrium.Core.Jobs;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Jobs;

/// <summary>
/// What a handler sees of its job. Writes go straight to the database and
/// only while this run still holds the lease, so a run that lost its job to
/// another worker cannot overwrite that worker's progress.
/// </summary>
public sealed class JobContext
{
    private readonly AtriumDbContext _db;
    private readonly TimeProvider _time;
    private readonly JobOptions _options;
    private DateTimeOffset _lastProgressWrite = DateTimeOffset.MinValue;

    internal JobContext(Job job, string leaseOwner, AtriumDbContext db, TimeProvider time, JobOptions options)
    {
        Job = job;
        LeaseOwner = leaseOwner;
        _db = db;
        _time = time;
        _options = options;
        Checkpoint = job.Checkpoint;
    }

    public Job Job { get; }

    public Guid JobId => Job.Id;

    /// <summary>The checkpoint saved by an earlier run, or null on the first run.</summary>
    public string? Checkpoint { get; private set; }

    internal string LeaseOwner { get; }

    public T GetPayload<T>() =>
        Job.Payload is null
            ? throw new JobFailedException(JobErrorCodes.Unexpected, "The job has no payload.", permanent: true)
            : JsonSerializer.Deserialize<T>(Job.Payload, JobQueue.PayloadJson)
              ?? throw new JobFailedException(JobErrorCodes.Unexpected, "The job payload is empty.", permanent: true);

    /// <summary>Saves where the work stands. A restarted run receives it in <see cref="Checkpoint"/>.</summary>
    public async Task SaveCheckpointAsync(string checkpoint, CancellationToken ct)
    {
        await UpdateAsync(s => s.SetProperty(j => j.Checkpoint, checkpoint), ct);
        Checkpoint = checkpoint;
    }

    /// <summary>Reports progress from 0 to 1. Written at most once per <see cref="JobOptions.ProgressInterval"/>.</summary>
    public async Task ReportProgressAsync(double progress, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (progress < 1 && now - _lastProgressWrite < _options.ProgressInterval)
        {
            return;
        }

        _lastProgressWrite = now;
        var value = Math.Clamp(progress, 0, 1);
        await UpdateAsync(s => s.SetProperty(j => j.Progress, value), ct);
    }

    private async Task UpdateAsync(
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Job>> setters, CancellationToken ct)
    {
        var owner = LeaseOwner;
        var changed = await _db.Jobs
            .Where(j => j.Id == Job.Id && j.State == JobState.Running && j.LeaseOwner == owner)
            .ExecuteUpdateAsync(setters, ct);
        if (changed == 0)
        {
            throw new JobLeaseLostException();
        }
    }
}

/// <summary>This run no longer owns its job; it must stop without writing anything more.</summary>
public sealed class JobLeaseLostException() : Exception("The job's lease was lost.");
