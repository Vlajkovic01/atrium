using Atrium.Api.Auth;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.External;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Path">The folder on the server, for an external library.</param>
/// <param name="FileCount">Files currently indexed, not counting those a scan no longer finds.</param>
/// <param name="UserIds">Who may see the library.</param>
/// <param name="Scan">The scan that is waiting or running, if any.</param>
public sealed record AdminLibraryView(
    Guid Id,
    string Name,
    LibraryKind Kind,
    LibraryMode Mode,
    Guid RootNodeId,
    string? Path,
    bool ReadOnly,
    LibraryState State,
    string? StateReason,
    DateTimeOffset? LastScanAt,
    int FileCount,
    IReadOnlyList<Guid> UserIds,
    IReadOnlyList<Guid> WriterIds,
    bool ShowInTimeline,
    DateTimeOffset? WritesEnabledAt,
    JobInfoBrief? Scan,
    int Version);

public sealed record JobInfoBrief(Guid Id, JobState State, double? Progress);

public sealed record CheckPathRequest(string? Path);

/// <param name="Ok">The folder can become a library.</param>
/// <param name="ErrorCode">Why not, when it cannot.</param>
/// <param name="EntryCount">Files and folders directly inside it.</param>
public sealed record CheckPathResponse(bool Ok, string? ErrorCode, int? EntryCount);

/// <param name="ShowInTimeline">Its photos and videos also appear on the timeline (default). Every file shows in Files either way.</param>
public sealed record CreateExternalLibraryRequest(string Name, string Path, IReadOnlyList<Guid>? UserIds, bool? ShowInTimeline);

/// <param name="WriterIds">Of <paramref name="UserIds">, who may change files once writes are enabled. Sent together with UserIds.</param>
public sealed record UpdateLibraryRequest(string? Name, IReadOnlyList<Guid>? UserIds, IReadOnlyList<Guid>? WriterIds, bool? ShowInTimeline);

/// <param name="ConfirmName">The library's name, required to enable writes.</param>
/// <param name="WriterIds">When enabling: who of those who see the library may change its files. Left out, the earlier choice stays.</param>
public sealed record SetLibraryWritesRequest(bool Enabled, string? ConfirmName, IReadOnlyList<Guid>? WriterIds);

/// <summary>
/// External libraries for admins (DESIGN.md 4.4). Personal libraries are
/// never listed here: admins see neither their names nor their content
/// (D-004).
/// </summary>
public static class AdminLibraryEndpoints
{
    public static IEndpointRouteBuilder MapAdminLibraryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/admin/libraries")
            .WithTags("Administration")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);

        group.MapGet("/", ListAsync).WithName("ListAdminLibraries");
        group.MapPost("/check-path", async (CheckPathRequest request, ExternalLibraryService service, CancellationToken ct) =>
            {
                var check = await service.CheckPathAsync(request.Path, null, ct);
                return TypedResults.Ok(new CheckPathResponse(check.Ok, check.ErrorCode, check.EntryCount));
            })
            .WithName("CheckLibraryPath")
            .WithDescription("Tries a folder before adding it as a library. Reads only; always 200 with the outcome inside.");
        group.MapPost("/", CreateAsync)
            .WithName("CreateExternalLibrary")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateLibrary")
            .WithDescription("Rename, or replace who may see the library. Requires If-Match with the version read.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
        group.MapPut("/{id:guid}/writes", SetWritesAsync)
            .WithName("SetLibraryWrites")
            .WithDescription("Lets Atrium change the folder's files (rename, move, delete into the library's own trash, upload). Enabling needs confirmName, the library's name; disabling takes effect at once.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{id:guid}/scan", ScanAsync)
            .WithName("ScanLibrary")
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteLibrary")
            .WithDescription("Removes an external library from Atrium. Its folder and files are not touched. confirmName must be the library's name.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<Ok<List<AdminLibraryView>>> ListAsync(AtriumDbContext db, CancellationToken ct)
    {
        var libraries = await db.Libraries.AsNoTracking()
            .Where(l => l.Kind != LibraryKind.Personal)
            .OrderBy(l => l.Name)
            .ToListAsync(ct);
        var result = new List<AdminLibraryView>();
        foreach (var library in libraries)
        {
            result.Add(await ViewAsync(db, library, ct));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Created<AdminLibraryView>, ProblemHttpResult>> CreateAsync(
        CreateExternalLibraryRequest request, HttpContext http, ExternalLibraryService service, AtriumDbContext db, CancellationToken ct)
    {
        var created = await service.CreateAsync(
            http.User.UserId()!.Value,
            new NewExternalLibrary(request.Name ?? string.Empty, request.Path ?? string.Empty, request.UserIds ?? [], request.ShowInTimeline ?? true),
            ct);
        if (!created.Ok)
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, created.Error!);
        }

        return TypedResults.Created($"/api/v1/admin/libraries/{created.Value!.Id}", await ViewAsync(db, created.Value, ct));
    }

    private static async Task<Results<Ok<AdminLibraryView>, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateLibraryRequest request, HttpContext http, ExternalLibraryService service, AtriumDbContext db, CancellationToken ct)
    {
        if (!FileHttp.TryReadIfMatch(http.Request.Headers.IfMatch, out var version))
        {
            return ApiProblem.Create(StatusCodes.Status428PreconditionRequired, ApiProblem.PreconditionRequired);
        }

        var updated = await service.UpdateAsync(id, version, request.Name, request.UserIds, request.WriterIds, request.ShowInTimeline, ct);
        return updated.Ok ? TypedResults.Ok(await ViewAsync(db, updated.Value!, ct)) : Problem(updated.Error!);
    }

    private static async Task<Results<Ok<AdminLibraryView>, ProblemHttpResult>> SetWritesAsync(
        Guid id, SetLibraryWritesRequest request, HttpContext http, ExternalLibraryService service, AtriumDbContext db, CancellationToken ct)
    {
        var changed = await service.SetWritesAsync(http.User.UserId()!.Value, id, request.Enabled, request.ConfirmName, request.WriterIds, ct);
        return changed.Ok ? TypedResults.Ok(await ViewAsync(db, changed.Value!, ct)) : Problem(changed.Error!);
    }

    private static async Task<Results<Accepted<JobStarted>, ProblemHttpResult>> ScanAsync(
        Guid id, ExternalLibraryService service, AtriumDbContext db, CancellationToken ct)
    {
        if (!await db.Libraries.AnyAsync(l => l.Id == id && l.Mode == LibraryMode.External, ct))
        {
            return ApiProblem.Missing();
        }

        var job = await service.EnqueueScanAsync(id, JobPriority.User, ct);
        return TypedResults.Accepted($"/api/v1/jobs/{job.Id}", new JobStarted(job.Id));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, string? confirmName, ExternalLibraryService service, CancellationToken ct)
    {
        var deleted = await service.DeleteAsync(id, confirmName, ct);
        return deleted.Ok ? TypedResults.NoContent() : Problem(deleted.Error!);
    }

    private static ProblemHttpResult Problem(string code) => code switch
    {
        FileErrors.NotFound => ApiProblem.Missing(),
        FileErrors.VersionConflict => ApiProblem.Create(StatusCodes.Status412PreconditionFailed, ApiProblem.VersionConflict),
        _ => ApiProblem.Create(StatusCodes.Status400BadRequest, code),
    };

    internal static async Task<AdminLibraryView> ViewAsync(AtriumDbContext db, Library library, CancellationToken ct)
    {
        var fileCount = await db.Nodes.CountAsync(
            n => n.LibraryId == library.Id && n.Kind == NodeKind.File && n.MissingSince == null && !n.InTrash, ct);
        var grants = await db.LibraryGrants.AsNoTracking()
            .Where(g => g.LibraryId == library.Id)
            .ToListAsync(ct);
        var key = LibraryScanHandler.DedupeKeyFor(library.Id);
        var scan = await db.Jobs.AsNoTracking()
            .Where(j => j.ActiveDedupeKey == key)
            .Select(j => new JobInfoBrief(j.Id, j.State, j.Progress))
            .SingleOrDefaultAsync(ct);
        return new AdminLibraryView(
            library.Id,
            library.Name,
            library.Kind,
            library.Mode,
            library.RootNodeId,
            library.ExternalPath,
            library.ReadOnly,
            library.State,
            library.StateReason,
            library.LastScanAt,
            fileCount,
            grants.Select(g => g.UserId).ToList(),
            grants.Where(g => g.Access == LibraryAccess.Write).Select(g => g.UserId).ToList(),
            Library.IsOnTimeline(library.Kind),
            library.WritesEnabledAt,
            scan,
            library.Version);
    }
}
