using Atrium.Api.Auth;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Core.Media;
using Atrium.Files;
using Atrium.Media.Photos;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

/// <param name="CapturedAt">Local clock time it was taken, with a zero offset (D-034).</param>
/// <param name="Width">As shown, already turned upright; null until its metadata is read.</param>
/// <param name="ThumbnailUrl">Signed; stays the same for hours so it can be cached.</param>
public sealed record PhotoItemView(
    Guid Id,
    Guid LibraryId,
    Guid? ParentId,
    string Name,
    MediaKind Kind,
    string? MediaType,
    long Size,
    DateTimeOffset CapturedAt,
    int? Width,
    int? Height,
    long? DurationMs,
    string ThumbnailUrl);

public sealed record PhotoPageView(IReadOnlyList<PhotoItemView> Items, string? NextCursor);

public sealed record TimelineBucketView(int Year, int Month, int Count);

/// <param name="ThumbnailUrl">For photos and videos only.</param>
public sealed record SearchResultView(NodeInfo Node, MediaKind? Kind, DateTimeOffset? CapturedAt, int? Width, int? Height, string? ThumbnailUrl);

public sealed record SearchPageView(IReadOnlyList<SearchResultView> Items, string? NextCursor);

/// <param name="CoverUrl">The chosen cover, or the newest item.</param>
public sealed record AlbumView(
    Guid Id,
    string Name,
    int ItemCount,
    Guid? CoverNodeId,
    string? CoverUrl,
    DateTimeOffset? NewestAt,
    DateTimeOffset? OldestAt,
    DateTimeOffset UpdatedAt,
    int Version);

public sealed record CreateAlbumRequest(string Name, IReadOnlyList<Guid>? NodeIds);

public sealed record UpdateAlbumRequest(string? Name, Guid? CoverNodeId);

public sealed record AlbumItemsRequest(IReadOnlyList<Guid> NodeIds);

public sealed record AlbumItemsResult(int Changed);

/// <summary>The photo timeline, albums and search (API.md Photos).</summary>
public static class PhotoEndpoints
{
    public const int MaxItemsPerChange = 1000;

    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder routes)
    {
        var timeline = routes.MapGroup("/timeline").WithTags("Photos");
        timeline.MapGet("/buckets", BucketsAsync).WithName("GetTimelineBuckets")
            .WithDescription("Months with photos or videos, newest first, with their counts.");
        timeline.MapGet("/", TimelineAsync).WithName("GetTimeline")
            .WithDescription("Photos and videos newest first. from and to are local dates and times (inclusive, exclusive).")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        routes.MapGet("/search", SearchAsync).WithTags("Photos").WithName("Search")
            .WithDescription("type: photo, video, document or folder. q matches names ignoring case and diacritics.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        var albums = routes.MapGroup("/albums").WithTags("Albums");
        albums.MapGet("/", ListAlbumsAsync).WithName("ListAlbums");
        albums.MapGet("/{id:guid}", GetAlbumAsync).WithName("GetAlbum").ProducesProblem(StatusCodes.Status404NotFound);
        albums.MapGet("/{id:guid}/items", AlbumItemsAsync).WithName("GetAlbumItems").ProducesProblem(StatusCodes.Status404NotFound);

        var changes = albums.MapGroup(string.Empty).RequireAuthorization(AuthSetup.FullSessionPolicy);
        changes.MapPost("/", CreateAlbumAsync).WithName("CreateAlbum")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        changes.MapPatch("/{id:guid}", UpdateAlbumAsync).WithName("UpdateAlbum")
            .WithDescription("Rename, or choose the cover among the album's items. Requires If-Match.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
        changes.MapDelete("/{id:guid}", DeleteAlbumAsync).WithName("DeleteAlbum")
            .WithDescription("Deletes the album only; its photos stay where they are.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        changes.MapPost("/{id:guid}/items", AddItemsAsync).WithName("AddAlbumItems")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        changes.MapPost("/{id:guid}/items/remove", RemoveItemsAsync).WithName("RemoveAlbumItems")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<Ok<List<TimelineBucketView>>> BucketsAsync(
        Guid? libraryId, HttpContext http, PhotoService photos, CancellationToken ct) =>
        TypedResults.Ok((await photos.BucketsAsync(http.User.Actor(), libraryId, ct))
            .Select(b => new TimelineBucketView(b.Year, b.Month, b.Count)).ToList());

    private static async Task<Results<Ok<PhotoPageView>, ProblemHttpResult>> TimelineAsync(
        Guid? libraryId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit,
        HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        if (limit is < 1 or > PhotoService.MaxLimit)
        {
            return ApiProblem.Invalid();
        }

        var page = await photos.TimelineAsync(http.User.Actor(), libraryId, Local(from), Local(to), cursor, limit ?? 200, ct);
        return TypedResults.Ok(ToView(page, http, signer));
    }

    private static async Task<Results<Ok<SearchPageView>, ProblemHttpResult>> SearchAsync(
        string? q, string? type, DateTimeOffset? from, DateTimeOffset? to, Guid? libraryId, string? cursor, int? limit,
        HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        if (limit is < 1 or > PhotoService.MaxLimit || !FileHttp.TryReadEnum<SearchType>(type, out var searchType) || q is { Length: > 200 })
        {
            return ApiProblem.Invalid();
        }

        var actor = http.User.Actor();
        var page = await photos.SearchAsync(
            actor, new SearchRequest(q, searchType, Local(from), Local(to), libraryId, cursor, limit ?? 100), ct);
        var sessionId = http.User.SessionId()!.Value;
        var items = page.Items.Select(i =>
        {
            var (width, height) = Upright(i.Info);
            return new SearchResultView(
                FileHttp.ToInfo(i.Node, canWrite: false),
                i.Node.MediaKind,
                i.Node.CapturedAt,
                width,
                height,
                i.Node.MediaKind is null ? null : ThumbnailUrl(signer, i.Node.Id, sessionId));
        }).ToList();
        return TypedResults.Ok(new SearchPageView(items, page.NextCursor));
    }

    private static async Task<Ok<List<AlbumView>>> ListAlbumsAsync(HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        var actor = http.User.Actor();
        var result = new List<AlbumView>();
        foreach (var album in await photos.AlbumsAsync(actor, ct))
        {
            result.Add(await AlbumViewAsync(album, actor, http, photos, signer, ct));
        }

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<AlbumView>, ProblemHttpResult>> GetAlbumAsync(
        Guid id, HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        var actor = http.User.Actor();
        return await photos.AlbumAsync(actor, id, ct) is { } album
            ? TypedResults.Ok(await AlbumViewAsync(album, actor, http, photos, signer, ct))
            : ApiProblem.Missing();
    }

    private static async Task<Results<Ok<PhotoPageView>, ProblemHttpResult>> AlbumItemsAsync(
        Guid id, string? cursor, int? limit, HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        var actor = http.User.Actor();
        if (limit is < 1 or > PhotoService.MaxLimit)
        {
            return ApiProblem.Invalid();
        }

        if (await photos.AlbumAsync(actor, id, ct) is null)
        {
            return ApiProblem.Missing();
        }

        return TypedResults.Ok(ToView(await photos.AlbumItemsAsync(actor, id, cursor, limit ?? 200, ct), http, signer));
    }

    private static async Task<Results<Created<AlbumView>, ProblemHttpResult>> CreateAlbumAsync(
        CreateAlbumRequest request, HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        if (request.NodeIds is { Count: > MaxItemsPerChange })
        {
            return ApiProblem.Invalid();
        }

        var actor = http.User.Actor();
        var created = await photos.CreateAlbumAsync(actor, request.Name, request.NodeIds ?? [], ct);
        if (!created.Ok)
        {
            return Problem(created.Error!);
        }

        return TypedResults.Created($"/api/v1/albums/{created.Value!.Id}", await AlbumViewAsync(created.Value, actor, http, photos, signer, ct));
    }

    private static async Task<Results<Ok<AlbumView>, ProblemHttpResult>> UpdateAlbumAsync(
        Guid id, UpdateAlbumRequest request, HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        if (!FileHttp.TryReadIfMatch(http.Request.Headers.IfMatch, out var version))
        {
            return ApiProblem.Create(StatusCodes.Status428PreconditionRequired, ApiProblem.PreconditionRequired);
        }

        var actor = http.User.Actor();
        var updated = await photos.UpdateAlbumAsync(actor, id, version, request.Name, request.CoverNodeId, ct);
        return updated.Ok ? TypedResults.Ok(await AlbumViewAsync(updated.Value!, actor, http, photos, signer, ct)) : Problem(updated.Error!);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAlbumAsync(Guid id, HttpContext http, PhotoService photos, CancellationToken ct) =>
        await photos.DeleteAlbumAsync(http.User.Actor(), id, ct) ? TypedResults.NoContent() : ApiProblem.Missing();

    private static async Task<Results<Ok<AlbumItemsResult>, ProblemHttpResult>> AddItemsAsync(
        Guid id, AlbumItemsRequest request, HttpContext http, PhotoService photos, CancellationToken ct)
    {
        if (request.NodeIds is not { Count: > 0 and <= MaxItemsPerChange })
        {
            return ApiProblem.Invalid();
        }

        var added = await photos.AddItemsAsync(http.User.Actor(), id, request.NodeIds, ct);
        return added.Ok ? TypedResults.Ok(new AlbumItemsResult(added.Value)) : Problem(added.Error!);
    }

    private static async Task<Results<Ok<AlbumItemsResult>, ProblemHttpResult>> RemoveItemsAsync(
        Guid id, AlbumItemsRequest request, HttpContext http, PhotoService photos, CancellationToken ct)
    {
        if (request.NodeIds is not { Count: > 0 and <= MaxItemsPerChange })
        {
            return ApiProblem.Invalid();
        }

        var removed = await photos.RemoveItemsAsync(http.User.Actor(), id, request.NodeIds, ct);
        return removed.Ok ? TypedResults.Ok(new AlbumItemsResult(removed.Value)) : Problem(removed.Error!);
    }

    private static ProblemHttpResult Problem(string code) => code switch
    {
        FileErrors.NotFound => ApiProblem.Missing(),
        FileErrors.VersionConflict => ApiProblem.Create(StatusCodes.Status412PreconditionFailed, ApiProblem.VersionConflict),
        FileErrors.ReadOnly => ApiProblem.Create(StatusCodes.Status403Forbidden, code),
        _ => ApiProblem.Create(StatusCodes.Status400BadRequest, code),
    };

    private static async Task<AlbumView> AlbumViewAsync(
        Album album, FileActor actor, HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        var (count, newest, oldest, newestId) = await photos.AlbumSummaryAsync(actor, album.Id, ct);
        var cover = album.CoverNodeId ?? newestId;
        return new AlbumView(
            album.Id,
            album.Name,
            count,
            album.CoverNodeId,
            cover is { } c && count > 0 ? ThumbnailUrl(signer, c, http.User.SessionId()!.Value) : null,
            newest,
            oldest,
            album.UpdatedAt,
            album.Version);
    }

    private static PhotoPageView ToView(PhotoPage page, HttpContext http, SignedUrls signer)
    {
        var sessionId = http.User.SessionId()!.Value;
        return new PhotoPageView(
            page.Items.Select(i =>
            {
                var (width, height) = Upright(i.Info);
                return new PhotoItemView(
                    i.Node.Id,
                    i.Node.LibraryId,
                    i.Node.ParentId,
                    i.Node.Name,
                    i.Node.MediaKind!.Value,
                    i.Node.MediaType,
                    i.Node.Size,
                    i.Node.CapturedAt!.Value,
                    width,
                    height,
                    i.Info?.DurationMs,
                    ThumbnailUrl(signer, i.Node.Id, sessionId));
            }).ToList(),
            page.NextCursor);
    }

    /// <summary>EXIF orientations 5 to 8 turn the picture a quarter: what is shown is the other way round.</summary>
    private static (int? Width, int? Height) Upright(MediaInfo? info) =>
        info?.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info?.Width, info?.Height);

    private static string ThumbnailUrl(SignedUrls signer, Guid nodeId, Guid sessionId) =>
        signer.SignForCaching(
            $"/api/v1/nodes/{nodeId}/thumbnail",
            sessionId,
            new Dictionary<string, string> { [MediaEndpoints.SizeOption] = "small" }).Url;

    /// <summary>Dates from the client name local clock time; any offset sent with them is ignored (D-034).</summary>
    private static DateTimeOffset? Local(DateTimeOffset? value) =>
        value is { } v ? new DateTimeOffset(v.DateTime, TimeSpan.Zero) : null;
}
