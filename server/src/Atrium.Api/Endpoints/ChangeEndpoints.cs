using Atrium.Api.Files;
using Atrium.Files;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

/// <param name="Removed">The item is gone for the caller, with everything below it. <paramref name="Node"/> is then null.</param>
/// <param name="InTrash">The item or a folder above it is in the trash.</param>
public sealed record ChangeInfo(Guid NodeId, Guid LibraryId, bool Removed, bool InTrash, NodeInfo? Node);

/// <param name="Cursor">Send it back next time to get what changed after this answer.</param>
/// <param name="HasMore">More changes are waiting; ask again at once.</param>
public sealed record ChangePage(IReadOnlyList<ChangeInfo> Items, string Cursor, bool HasMore);

public static class ChangeEndpoints
{
    private const int DefaultLimit = 500;

    public static IEndpointRouteBuilder MapChangeEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/changes", ListAsync)
            .WithTags("Files")
            .WithName("ListChanges")
            .WithDescription(
                "Without a cursor: no items and a cursor that means now. With one: the files and folders to look at "
                + "again in the libraries the caller can read, each once, as they are now. A change to a folder "
                + "(trashed, restored, removed) speaks for everything below it. 410 changes.cursor_expired: read everything again.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status410Gone);

        return routes;
    }

    private static async Task<Results<Ok<ChangePage>, ProblemHttpResult>> ListAsync(
        HttpContext http, ChangeFeed feed, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > ChangeFeed.MaxLimit)
        {
            return ApiProblem.Invalid();
        }

        var result = await feed.ReadAsync(http.User.Actor(), cursor, take, ct);
        if (!result.Ok)
        {
            return ApiProblem.Create(StatusCodes.Status410Gone, result.Error!);
        }

        var batch = result.Value!;
        return TypedResults.Ok(new ChangePage(
            batch.Items
                .Select(i => new ChangeInfo(
                    i.NodeId, i.LibraryId, i.Node is null, i.InTrash, i.Node is null ? null : FileHttp.ToInfo(i.Node, i.CanWrite)))
                .ToList(),
            batch.Cursor,
            batch.HasMore));
    }
}
