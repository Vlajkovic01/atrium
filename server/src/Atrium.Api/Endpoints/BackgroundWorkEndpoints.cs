using Atrium.Api.Auth;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Default">The limit the hardware profile gives this class.</param>
/// <param name="Override">The admin's limit, or null to use the default.</param>
/// <param name="Effective">What the workers obey now.</param>
public sealed record JobClassLimit(
    JobResourceClass ResourceClass, int Default, int? Override, int Effective, int Running, int Queued);

/// <param name="MemoryBytes">Memory available to the server, which respects container limits.</param>
/// <param name="Paused">Background work is paused, by an admin or by work that needs the machine.</param>
/// <param name="PausedByAdmin">An admin paused it; <paramref name="PausedUntil"/> says until when, null meaning until resumed.</param>
public sealed record BackgroundWorkInfo(
    HardwareProfile Profile,
    int ProcessorCount,
    long MemoryBytes,
    int MaxLimit,
    IReadOnlyList<JobClassLimit> Classes,
    bool Paused,
    bool PausedByAdmin,
    DateTimeOffset? PausedUntil);

/// <param name="Limit">Null uses the default of the hardware profile.</param>
public sealed record JobClassLimitSetting(JobResourceClass ResourceClass, int? Limit);

/// <param name="Limits">Limit per class; a class left out uses its default. Replaces all earlier overrides.</param>
public sealed record SetJobLimitsRequest(IReadOnlyList<JobClassLimitSetting> Limits);

/// <param name="Minutes">How long to pause, from 1 minute to 7 days. Null pauses until resumed.</param>
public sealed record PauseBackgroundRequest(int? Minutes);

/// <summary>Hardware profile, job limits and pausing background work (DESIGN.md 5.4).</summary>
public static class BackgroundWorkEndpoints
{
    public const int MaxPauseMinutes = 7 * 24 * 60;

    public static IEndpointRouteBuilder MapBackgroundWorkEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/admin/background-work")
            .WithTags("Administration")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);

        group.MapGet("/", GetAsync).WithName("GetBackgroundWork");
        group.MapPut("/limits", SetLimitsAsync)
            .WithName("SetJobLimits")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapPost("/pause", PauseAsync)
            .WithName("PauseBackgroundWork")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapPost("/resume", ResumeAsync).WithName("ResumeBackgroundWork");

        return routes;
    }

    private static async Task<Ok<BackgroundWorkInfo>> GetAsync(JobLimits limits, AtriumDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await InfoAsync(limits, db, ct));

    private static async Task<Results<Ok<BackgroundWorkInfo>, ProblemHttpResult>> SetLimitsAsync(
        SetJobLimitsRequest request, JobLimits limits, JobSettingsStore store, AtriumDbContext db, CancellationToken ct)
    {
        var overrides = new Dictionary<JobResourceClass, int>();
        var seen = new HashSet<JobResourceClass>();
        foreach (var (resourceClass, limit) in request.Limits ?? [])
        {
            if (!Enum.IsDefined(resourceClass) || limit is < 1 or > JobLimits.MaxLimit || !seen.Add(resourceClass))
            {
                return ApiProblem.Invalid();
            }

            if (limit is { } value)
            {
                overrides[resourceClass] = value;
            }
        }

        await store.SaveLimitsAsync(overrides, ct);
        return TypedResults.Ok(await InfoAsync(limits, db, ct));
    }

    private static async Task<Results<Ok<BackgroundWorkInfo>, ProblemHttpResult>> PauseAsync(
        PauseBackgroundRequest request,
        JobLimits limits,
        JobSettingsStore store,
        AtriumDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request.Minutes is < 1 or > MaxPauseMinutes)
        {
            return ApiProblem.Invalid();
        }

        DateTimeOffset? until = request.Minutes is { } minutes ? time.GetUtcNow().AddMinutes(minutes) : null;
        await store.PauseAsync(until, ct);
        return TypedResults.Ok(await InfoAsync(limits, db, ct));
    }

    private static async Task<Ok<BackgroundWorkInfo>> ResumeAsync(
        JobLimits limits, JobSettingsStore store, AtriumDbContext db, CancellationToken ct)
    {
        await store.ResumeAsync(ct);
        return TypedResults.Ok(await InfoAsync(limits, db, ct));
    }

    private static async Task<BackgroundWorkInfo> InfoAsync(JobLimits limits, AtriumDbContext db, CancellationToken ct)
    {
        var counts = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Queued || j.State == JobState.Running)
            .GroupBy(j => new { j.ResourceClass, j.State })
            .Select(g => new { g.Key.ResourceClass, g.Key.State, Count = g.Count() })
            .ToListAsync(ct);
        var overrides = limits.Overrides;
        var classes = Enum.GetValues<JobResourceClass>()
            .Select(c => new JobClassLimit(
                c,
                limits.Defaults[c],
                overrides.TryGetValue(c, out var o) ? o : null,
                limits.LimitFor(c),
                counts.Where(x => x.ResourceClass == c && x.State == JobState.Running).Sum(x => x.Count),
                counts.Where(x => x.ResourceClass == c && x.State == JobState.Queued).Sum(x => x.Count)))
            .ToList();
        var pause = limits.Pause;
        return new BackgroundWorkInfo(
            limits.Profile,
            limits.Hardware.ProcessorCount,
            limits.Hardware.MemoryBytes,
            JobLimits.MaxLimit,
            classes,
            limits.IsBackgroundPaused,
            pause.Paused,
            pause.Until);
    }
}
