using System.Globalization;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Files;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Atrium.Api.Endpoints;

/// <param name="Total">Number of children in the folder, for a scrollbar.</param>
public sealed record NodePage(IReadOnlyList<NodeInfo> Items, string? NextCursor, int Total);

public sealed record CreateFolderRequest(Guid ParentId, string Name);

/// <summary>Either field may be left out: a rename, a move, or both.</summary>
public sealed record UpdateNodeRequest(string? Name, Guid? ParentId);

/// <param name="Name">Name of the copy. When left out, the original name is used, numbered if it is taken.</param>
public sealed record CopyNodeRequest(Guid ParentId, string? Name);

public enum BatchOperation
{
    Move,
    Copy,
    Trash,
}

public sealed record BatchRequest(BatchOperation Operation, IReadOnlyList<Guid> Ids, Guid? ParentId);

/// <summary>The outcome for one item. A failure of one item does not stop the others.</summary>
public sealed record BatchItemResult(Guid Id, bool Ok, string? Code, Dictionary<string, object?>? Params, NodeInfo? Node);

public sealed record BatchResponse(IReadOnlyList<BatchItemResult> Items);

public static class NodeEndpoints
{
    public const int MaxBatchItems = 1000;
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    public static IEndpointRouteBuilder MapNodeEndpoints(this IEndpointRouteBuilder routes)
    {
        var nodes = routes.MapGroup("/nodes").WithTags("Files");

        nodes.MapGet("/{id:guid}", GetAsync).WithName("GetNode").ProducesProblem(StatusCodes.Status404NotFound);
        nodes.MapGet("/{id:guid}/children", ChildrenAsync).WithName("ListChildren")
            .WithDescription("sort: name, modified, size or type. kind: folder or file.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        nodes.MapGet("/{id:guid}/path", PathAsync).WithName("GetNodePath").ProducesProblem(StatusCodes.Status404NotFound);
        nodes.MapPost("/", CreateFolderAsync).WithName("CreateFolder")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        nodes.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateNode")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
        nodes.MapPost("/{id:guid}/copy", CopyAsync).WithName("CopyNode")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status507InsufficientStorage);
        nodes.MapDelete("/{id:guid}", TrashAsync).WithName("TrashNode")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
        nodes.MapPost("/batch", BatchAsync).WithName("BatchNodes").ProducesProblem(StatusCodes.Status400BadRequest);

        return routes;
    }

    private static async Task<Results<Ok<NodeInfo>, ProblemHttpResult>> GetAsync(
        Guid id, HttpContext http, NodeService nodes, CancellationToken ct)
    {
        if (await nodes.FindAsync(http.User.Actor(), id, ct) is not { } found)
        {
            return FileHttp.Problem(FileErrors.NotFound);
        }

        FileHttp.SetETag(http, found.Node);
        return TypedResults.Ok(FileHttp.ToInfo(found.Node, found.CanWrite && !found.InTrash));
    }

    private static async Task<Results<Ok<NodePage>, ProblemHttpResult>> ChildrenAsync(
        Guid id,
        HttpContext http,
        NodeService nodes,
        string? sort,
        bool? descending,
        string? kind,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        var offset = 0;
        if (take is < 1 or > MaxLimit
            || (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
            || !FileHttp.TryReadEnum<ChildSort>(sort, out var sortBy)
            || !FileHttp.TryReadEnum<NodeKind>(kind, out var kindFilter))
        {
            return ApiProblem.Invalid();
        }

        var actor = http.User.Actor();
        var result = await nodes.ChildrenAsync(actor, id, sortBy ?? ChildSort.Name, descending ?? false, kindFilter, offset, take, ct);
        if (!result.Ok)
        {
            return FileHttp.Problem(result);
        }

        var folder = await nodes.FindAsync(actor, id, ct);
        var canWrite = folder is { CanWrite: true, InTrash: false };
        var (items, total) = result.Value;
        var next = offset + items.Count < total ? (offset + items.Count).ToString(CultureInfo.InvariantCulture) : null;
        return TypedResults.Ok(new NodePage(items.Select(n => FileHttp.ToInfo(n, canWrite)).ToList(), next, total));
    }

    private static async Task<Results<Ok<List<NodeInfo>>, ProblemHttpResult>> PathAsync(
        Guid id, HttpContext http, NodeService nodes, CancellationToken ct)
    {
        var actor = http.User.Actor();
        var result = await nodes.PathAsync(actor, id, ct);
        if (!result.Ok)
        {
            return FileHttp.Problem(result);
        }

        var canWrite = (await nodes.FindAsync(actor, id, ct))?.CanWrite ?? false;
        return TypedResults.Ok(result.Value!.Select(n => FileHttp.ToInfo(n, canWrite)).ToList());
    }

    private static async Task<Results<Created<NodeInfo>, ProblemHttpResult>> CreateFolderAsync(
        CreateFolderRequest request, HttpContext http, NodeService nodes, CancellationToken ct)
    {
        var result = await nodes.CreateFolderAsync(http.User.Actor(), request.ParentId, request.Name, ct);
        return result.Ok
            ? TypedResults.Created($"/api/v1/nodes/{result.Value!.Id}", FileHttp.ToInfo(result.Value, canWrite: true))
            : FileHttp.Problem(result);
    }

    private static async Task<Results<Ok<NodeInfo>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateNodeRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        HttpContext http,
        NodeService nodes,
        CancellationToken ct)
    {
        // Two devices renaming or moving one file must not silently undo each other.
        if (!FileHttp.TryReadIfMatch(ifMatch, out var version))
        {
            return ApiProblem.Create(StatusCodes.Status428PreconditionRequired, ApiProblem.PreconditionRequired);
        }

        var result = await nodes.UpdateAsync(http.User.Actor(), id, version, request.Name, request.ParentId, ct);
        if (!result.Ok)
        {
            return FileHttp.Problem(result);
        }

        FileHttp.SetETag(http, result.Value!);
        return TypedResults.Ok(FileHttp.ToInfo(result.Value!, canWrite: true));
    }

    private static async Task<Results<Created<NodeInfo>, ProblemHttpResult>> CopyAsync(
        Guid id, CopyNodeRequest request, HttpContext http, NodeService nodes, CancellationToken ct)
    {
        var result = await nodes.CopyAsync(http.User.Actor(), id, request.ParentId, request.Name, ct);
        return result.Ok
            ? TypedResults.Created($"/api/v1/nodes/{result.Value!.Id}", FileHttp.ToInfo(result.Value, canWrite: true))
            : FileHttp.Problem(result);
    }

    /// <summary>Moves to the trash. <c>If-Match</c> is optional here: deleting is reversible.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> TrashAsync(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        HttpContext http,
        NodeService nodes,
        CancellationToken ct)
    {
        int? version = null;
        if (ifMatch is not null)
        {
            if (!FileHttp.TryReadIfMatch(ifMatch, out var parsed))
            {
                return ApiProblem.Invalid();
            }

            version = parsed;
        }

        var result = await nodes.TrashAsync(http.User.Actor(), id, version, ct);
        return result.Ok ? TypedResults.NoContent() : FileHttp.Problem(result);
    }

    /// <summary>Move, copy or trash many items. Each is checked and done on its own, as if sent alone.</summary>
    private static async Task<Results<Ok<BatchResponse>, ProblemHttpResult>> BatchAsync(
        BatchRequest request, HttpContext http, NodeService nodes, CancellationToken ct)
    {
        if (request.Ids is not { Count: > 0 and <= MaxBatchItems }
            || request.Ids.Distinct().Count() != request.Ids.Count
            || (request.Operation is BatchOperation.Move or BatchOperation.Copy && request.ParentId is null)
            || !Enum.IsDefined(request.Operation))
        {
            return ApiProblem.Invalid();
        }

        var actor = http.User.Actor();
        var results = new List<BatchItemResult>(request.Ids.Count);
        foreach (var id in request.Ids)
        {
            FileResult<Node> result;
            switch (request.Operation)
            {
                case BatchOperation.Move:
                    var current = await nodes.FindAsync(actor, id, ct);
                    result = current is null
                        ? FileResult<Node>.Fail(FileErrors.NotFound)
                        : await nodes.UpdateAsync(actor, id, current.Node.Version, null, request.ParentId, ct);
                    break;
                case BatchOperation.Copy:
                    result = await nodes.CopyAsync(actor, id, request.ParentId!.Value, null, ct);
                    break;
                default:
                    result = await nodes.TrashAsync(actor, id, null, ct);
                    break;
            }

            results.Add(new BatchItemResult(
                id,
                result.Ok,
                result.Error,
                result.Params?.ToDictionary(p => p.Key, p => p.Value),
                result.Ok && request.Operation != BatchOperation.Trash ? FileHttp.ToInfo(result.Value!, canWrite: true) : null));
        }

        return TypedResults.Ok(new BatchResponse(results));
    }
}
