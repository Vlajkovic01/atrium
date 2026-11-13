using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Core.Media;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.Storage;
using Atrium.Jobs;
using Atrium.Media;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="CapturedAt">Local clock time the photo or video was taken, with a zero offset (D-034).</param>
/// <param name="CapturedOffsetMinutes">The offset from UTC the camera recorded, when it did.</param>
public sealed record MediaInfoView(
    MediaKind Kind,
    int? Width,
    int? Height,
    long? DurationMs,
    string? Format,
    string? VideoCodec,
    string? AudioCodec,
    DateTimeOffset? CapturedAt,
    int? CapturedOffsetMinutes,
    string? CameraMake,
    string? CameraModel,
    ThumbnailState ThumbnailState);

/// <summary>
/// Only a few thumbnails are made inline at once; the rest wait. A grid of
/// a hundred new photos must not start a hundred decoders on a small device.
/// </summary>
public sealed class ThumbnailGate(JobLimits limits)
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, limits.LimitFor(Atrium.Core.Jobs.JobResourceClass.Cpu)) + 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Exit(_gate);
    }

    private sealed class Exit(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

/// <summary>Thumbnails, previews and technical details of photos and videos (DESIGN.md 7.1).</summary>
public static class MediaEndpoints
{
    public const string SizeOption = "size";

    private static readonly string[] ThumbnailOptions = [SizeOption];

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder routes)
    {
        // Open to signed requests, for image and video elements.
        routes.MapGet("/nodes/{id:guid}/thumbnail", ThumbnailAsync)
            .AllowAnonymous()
            .WithTags("Photos")
            .WithName("GetThumbnail")
            .WithDescription("size: small (grid) or large (viewer). A WebP image without any metadata. Bearer token or signed URL.");
        routes.MapGet("/nodes/{id:guid}/preview", PreviewAsync)
            .AllowAnonymous()
            .WithTags("Photos")
            .WithName("GetPreview")
            .WithDescription("A video's short silent preview clip (MP4). Bearer token or signed URL.");
        routes.MapGet("/nodes/{id:guid}/media-info", MediaInfoAsync)
            .WithTags("Photos")
            .WithName("GetMediaInfo")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return routes;
    }

    private static async Task<IResult> ThumbnailAsync(
        Guid id, string? size, HttpContext http, AtriumDbContext db, NodeService nodes, MediaService media, SignedUrls signer,
        ThumbnailGate gate, CancellationToken ct)
    {
        if (await ActorAsync(http, signer, db, ThumbnailOptions, ct) is not { } actor)
        {
            return Results.Unauthorized();
        }

        var variant = size switch
        {
            null or "small" => ThumbnailVariant.Small,
            "large" => ThumbnailVariant.Large,
            _ => (ThumbnailVariant?)null,
        };
        if (variant is null)
        {
            return ApiProblem.Invalid();
        }

        if (await nodes.FindAsync(actor, id, ct) is not { } found || found.Node.MediaKind is null)
        {
            return Results.NotFound();
        }

        return await ServeAsync(http, found, variant.Value, "image/webp", media, gate, ct);
    }

    private static async Task<IResult> PreviewAsync(
        Guid id, HttpContext http, AtriumDbContext db, NodeService nodes, MediaService media, SignedUrls signer, ThumbnailGate gate, CancellationToken ct)
    {
        if (await ActorAsync(http, signer, db, [], ct) is not { } actor)
        {
            return Results.Unauthorized();
        }

        if (await nodes.FindAsync(actor, id, ct) is not { Node.MediaKind: MediaKind.Video } found)
        {
            return Results.NotFound();
        }

        return await ServeAsync(http, found, ThumbnailVariant.Preview, "video/mp4", media, gate, ct);
    }

    private static async Task<IResult> ServeAsync(
        HttpContext http, AccessibleNode found, ThumbnailVariant variant, string contentType, MediaService media, ThumbnailGate gate, CancellationToken ct)
    {
        string? path;
        try
        {
            using (await gate.EnterAsync(ct))
            {
                path = await media.ThumbnailAsync(found.Node, found.Library, variant, background: false, ct);
            }
        }
        catch (LocationUnavailableException e)
        {
            return ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, e.Code);
        }

        if (path is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, MediaErrors.NoThumbnail);
        }

        // The file's name is its content: the same URL with the same content
        // can be cached as long as the URL lives.
        http.Response.Headers.CacheControl = "private, max-age=21600";
        http.Response.Headers.ContentSecurityPolicy = FileContent.SandboxPolicy;
        http.Response.Headers.XContentTypeOptions = "nosniff";
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{Path.GetFileNameWithoutExtension(path)}\"");
        return Results.File(path, contentType, enableRangeProcessing: true, entityTag: etag);
    }

    private static async Task<Results<Ok<MediaInfoView>, ProblemHttpResult>> MediaInfoAsync(
        Guid id, HttpContext http, NodeService nodes, AtriumDbContext db, CancellationToken ct)
    {
        if (await nodes.FindAsync(http.User.Actor(), id, ct) is not { } found || found.Node.MediaKind is null)
        {
            return ApiProblem.Missing();
        }

        var node = found.Node;
        var info = node.BlobId is { } blobId
            ? await db.MediaInfos.AsNoTracking().SingleOrDefaultAsync(m => m.BlobId == blobId, ct)
            : await db.MediaInfos.AsNoTracking().SingleOrDefaultAsync(m => m.NodeId == node.Id, ct);
        return TypedResults.Ok(info is null
            ? new MediaInfoView(node.MediaKind.Value, null, null, null, null, null, null, node.CapturedAt, null, null, null, ThumbnailState.Pending)
            : new MediaInfoView(
                info.Kind, info.Width, info.Height, info.DurationMs, info.Format, info.VideoCodec, info.AudioCodec,
                info.CapturedAt, info.CapturedOffsetMinutes, info.CameraMake, info.CameraModel, info.ThumbnailState));
    }

    private static async Task<FileActor?> ActorAsync(
        HttpContext http, SignedUrls signer, AtriumDbContext db, IReadOnlyCollection<string> options, CancellationToken ct) =>
        http.User.Identity?.IsAuthenticated == true
            ? http.User.Actor()
            : await signer.VerifyAsync(http.Request, options, db, ct);
}
