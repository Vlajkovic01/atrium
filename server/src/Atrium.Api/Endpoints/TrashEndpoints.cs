using Atrium.Api.Files;
using Atrium.Files;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

/// <param name="Bytes">Space a permanent deletion would stop charging.</param>
/// <param name="ItemCount">The item itself and everything inside it.</param>
/// <param name="DeleteAt">When retention removes it for good.</param>
public sealed record TrashItemInfo(NodeInfo Node, long Bytes, int ItemCount, DateTimeOffset DeleteAt);

/// <param name="ParentId">Where to restore to. Needed when the original folder is in the trash too.</param>
/// <param name="OnConflict"><c>fail</c> or <c>rename</c> (keep both).</param>
public sealed record RestoreRequest(Guid? ParentId, ConflictPolicy? OnConflict);

public sealed record EmptyTrashResponse(int Removed);

public sealed record VersionInfo(Guid Id, long Size, DateTimeOffset ModifiedAt, DateTimeOffset ReplacedAt);

public static class TrashEndpoints
{
    public static IEndpointRouteBuilder MapTrashEndpoints(this IEndpointRouteBuilder routes)
    {
        var trash = routes.MapGroup("/trash").WithTags("Trash");

        trash.MapGet("/", async (HttpContext http, TrashService service, CancellationToken ct) =>
                (await service.ListAsync(http.User.Actor(), ct))
                    .Select(i => new TrashItemInfo(FileHttp.ToInfo(i.Node, canWrite: false), i.Bytes, i.ItemCount, i.DeleteAt))
                    .ToList())
            .WithName("ListTrash");

        trash.MapPost("/{id:guid}/restore", async Task<Results<Ok<NodeInfo>, ProblemHttpResult>> (
                Guid id, RestoreRequest? request, HttpContext http, TrashService service, CancellationToken ct) =>
            {
                var policy = request?.OnConflict ?? ConflictPolicy.Fail;
                if (policy == ConflictPolicy.Replace)
                {
                    // Restoring never overwrites anything.
                    return ApiProblem.Invalid();
                }

                var result = await service.RestoreAsync(http.User.Actor(), id, request?.ParentId, policy, ct);
                return result.Ok ? TypedResults.Ok(FileHttp.ToInfo(result.Value!, canWrite: true)) : FileHttp.Problem(result);
            })
            .WithName("RestoreFromTrash")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        trash.MapDelete("/{id:guid}", async Task<Results<NoContent, ProblemHttpResult>> (
                Guid id, HttpContext http, TrashService service, CancellationToken ct) =>
            {
                var result = await service.PurgeAsync(http.User.Actor(), id, ct);
                return result.Ok ? TypedResults.NoContent() : FileHttp.Problem(result);
            })
            .WithName("DeleteFromTrash")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        trash.MapDelete("/", async Task<Results<Ok<EmptyTrashResponse>, ProblemHttpResult>> (
                HttpContext http, TrashService service, CancellationToken ct) =>
            {
                var result = await service.EmptyAsync(http.User.Actor(), ct);
                return result.Ok ? TypedResults.Ok(new EmptyTrashResponse(result.Value)) : FileHttp.Problem(result);
            })
            .WithName("EmptyTrash")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var versions = routes.MapGroup("/nodes/{id:guid}/versions").WithTags("Files");

        versions.MapGet("/", async Task<Results<Ok<List<VersionInfo>>, ProblemHttpResult>> (
                Guid id, HttpContext http, VersionService service, CancellationToken ct) =>
            {
                var result = await service.ListAsync(http.User.Actor(), id, ct);
                return result.Ok
                    ? TypedResults.Ok(result.Value!.Select(v => new VersionInfo(v.Id, v.Size, v.ModifiedAt, v.CreatedAt)).ToList())
                    : FileHttp.Problem(result);
            })
            .WithName("ListVersions")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        versions.MapPost("/{versionId:guid}/restore", async Task<Results<Ok<NodeInfo>, ProblemHttpResult>> (
                Guid id, Guid versionId, HttpContext http, VersionService service, CancellationToken ct) =>
            {
                var result = await service.RestoreAsync(http.User.Actor(), id, versionId, ct);
                return result.Ok ? TypedResults.Ok(FileHttp.ToInfo(result.Value!, canWrite: true)) : FileHttp.Problem(result);
            })
            .WithName("RestoreVersion")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        versions.MapDelete("/{versionId:guid}", async Task<Results<NoContent, ProblemHttpResult>> (
                Guid id, Guid versionId, HttpContext http, VersionService service, CancellationToken ct) =>
            {
                var result = await service.DeleteAsync(http.User.Actor(), id, versionId, ct);
                return result.Ok ? TypedResults.NoContent() : FileHttp.Problem(result);
            })
            .WithName("DeleteVersion")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapGet("/me/usage", async (HttpContext http, UsageService usage, CancellationToken ct) =>
                await usage.ForUserAsync(http.User.Actor().UserId, ct))
            .WithTags("Me")
            .WithName("GetMyUsage");

        return routes;
    }
}
