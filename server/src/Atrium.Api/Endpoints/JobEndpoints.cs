using Atrium.Api.Auth;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="ErrorDetail">Technical detail of the last failure. Only for the job's owner, or for an admin on a server job.</param>
public sealed record JobInfo(
    Guid Id,
    string Type,
    JobState State,
    JobPriority Priority,
    double? Progress,
    int Attempts,
    int MaxAttempts,
    bool CancelRequested,
    bool Mine,
    string? ErrorCode,
    string? ErrorDetail,
    DateTimeOffset CreatedAt,
    DateTimeOffset RunAfter,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record JobPage(IReadOnlyList<JobInfo> Items, string? NextCursor);

public static class JobEndpoints
{
    public const string JobNotActive = "job.not_active";
    public const string JobNotRetryable = "job.not_retryable";
    public const string JobAlreadyQueued = "job.already_queued";

    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        var jobs = routes.MapGroup("/jobs").WithTags("Jobs");

        jobs.MapGet("/", ListAsync).WithName("ListJobs").ProducesProblem(StatusCodes.Status400BadRequest);
        jobs.MapGet("/{id:guid}", GetAsync).WithName("GetJob").ProducesProblem(StatusCodes.Status404NotFound);

        // Changing a job is a write: a read-only device may watch but not cancel.
        var changes = jobs.MapGroup(string.Empty).RequireAuthorization(AuthSetup.FullSessionPolicy);
        changes.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithName("CancelJob")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        changes.MapPost("/{id:guid}/retry", RetryAsync)
            .WithName("RetryJob")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    /// <summary>Newest first. Users see their own jobs; admins see all.</summary>
    private static async Task<Results<Ok<JobPage>, ProblemHttpResult>> ListAsync(
        HttpContext http,
        AtriumDbContext db,
        JobState? state,
        string? type,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return ApiProblem.Invalid();
        }

        var query = Visible(db.Jobs.AsNoTracking(), http);
        if (state is { } s)
        {
            query = query.Where(j => j.State == s);
        }

        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(j => j.Type == type);
        }

        if (cursor is not null)
        {
            // Job IDs are UUIDv7: ordering by ID is ordering by creation time.
            if (!Guid.TryParse(cursor, out var after))
            {
                return ApiProblem.Invalid();
            }

            query = query.Where(j => j.Id.CompareTo(after) < 0);
        }

        var page = await query.OrderByDescending(j => j.Id).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take;
        var items = page.Take(take).Select(j => ToInfo(j, http)).ToList();
        return TypedResults.Ok(new JobPage(items, hasMore ? items[^1].Id.ToString() : null));
    }

    private static async Task<Results<Ok<JobInfo>, ProblemHttpResult>> GetAsync(
        Guid id, HttpContext http, AtriumDbContext db, CancellationToken ct) =>
        await Visible(db.Jobs.AsNoTracking(), http).SingleOrDefaultAsync(j => j.Id == id, ct) is { } job
            ? TypedResults.Ok(ToInfo(job, http))
            : ApiProblem.Missing();

    private static async Task<Results<NoContent, ProblemHttpResult>> CancelAsync(
        Guid id, HttpContext http, AtriumDbContext db, JobQueue queue, CancellationToken ct)
    {
        if (!await Visible(db.Jobs, http).AnyAsync(j => j.Id == id, ct))
        {
            return ApiProblem.Missing();
        }

        return await queue.CancelAsync(id, ct) switch
        {
            JobChangeResult.Done => TypedResults.NoContent(),
            JobChangeResult.WrongState => ApiProblem.Create(StatusCodes.Status409Conflict, JobNotActive),
            _ => ApiProblem.Missing(),
        };
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RetryAsync(
        Guid id, HttpContext http, AtriumDbContext db, JobQueue queue, CancellationToken ct)
    {
        if (!await Visible(db.Jobs, http).AnyAsync(j => j.Id == id, ct))
        {
            return ApiProblem.Missing();
        }

        return await queue.RetryAsync(id, ct) switch
        {
            JobChangeResult.Done => TypedResults.NoContent(),
            JobChangeResult.WrongState => ApiProblem.Create(StatusCodes.Status409Conflict, JobNotRetryable),
            JobChangeResult.Duplicate => ApiProblem.Create(StatusCodes.Status409Conflict, JobAlreadyQueued),
            _ => ApiProblem.Missing(),
        };
    }

    /// <summary>
    /// Someone else's job answers "not found", the same as a job that does
    /// not exist. Admins see every job, but only as type and state (D-004).
    /// </summary>
    private static IQueryable<Job> Visible(IQueryable<Job> jobs, HttpContext http)
    {
        if (http.User.IsInRole(nameof(UserRole.Admin)))
        {
            return jobs;
        }

        var userId = http.User.UserId();
        return jobs.Where(j => j.OwnerUserId == userId);
    }

    private static JobInfo ToInfo(Job job, HttpContext http)
    {
        var mine = job.OwnerUserId is { } owner && owner == http.User.UserId();
        var showDetail = mine || (job.OwnerUserId is null && http.User.IsInRole(nameof(UserRole.Admin)));
        return new JobInfo(
            job.Id,
            job.Type,
            job.State,
            job.Priority,
            job.Progress,
            job.Attempts,
            job.MaxAttempts,
            job.CancelRequested,
            mine,
            job.ErrorCode,
            showDetail ? job.ErrorDetail : null,
            job.CreatedAt,
            job.RunAfter,
            job.StartedAt,
            job.FinishedAt);
    }
}
