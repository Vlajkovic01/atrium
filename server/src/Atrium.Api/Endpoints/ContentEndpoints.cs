using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Atrium.Api.Auth;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Inline">Ask for the file to be shown rather than saved. Honoured only for types that are safe to show.</param>
/// <param name="VersionId">Sign the URL of an earlier version instead of the current content.</param>
public sealed record ContentUrlRequest(bool? Inline, Guid? VersionId);

public sealed record SignedUrlResponse(string Url, DateTimeOffset ExpiresAt);

public sealed record ArchiveRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// File content: downloads, range requests for players, signed URLs for
/// links and media elements, and ZIP archives of several items.
/// </summary>
public static class ContentEndpoints
{
    public const string InlineOption = "inline";
    public const int MaxArchiveItems = 50_000;
    public const int MaxArchiveRoots = 1000;

    private static readonly string[] ContentOptions = [InlineOption];

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder routes)
    {
        // Open to signed requests, so they cannot rely on the fallback
        // policy; each handler establishes the actor itself and refuses
        // when there is none.
        routes.MapMethods("/nodes/{id:guid}/content", ["GET", "HEAD"], ContentAsync)
            .AllowAnonymous()
            .WithTags("Files")
            .WithName("GetNodeContent")
            .WithDescription("Download or stream a file. Range requests and If-None-Match are supported. Authorized by a bearer token or a signed URL.");
        routes.MapMethods("/nodes/{id:guid}/versions/{versionId:guid}/content", ["GET", "HEAD"], VersionContentAsync)
            .AllowAnonymous()
            .WithTags("Files")
            .WithName("GetVersionContent");
        routes.MapPost("/nodes/{id:guid}/content-url", ContentUrlAsync)
            .WithTags("Files")
            .WithName("CreateContentUrl")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapPost("/downloads/archive", CreateArchiveAsync)
            .WithTags("Files")
            .WithName("CreateArchive")
            .WithDescription("Prepares a ZIP of the given files and folders and returns a signed URL that streams it.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        routes.MapGet("/downloads/archive/{token}", ArchiveAsync)
            .AllowAnonymous()
            .WithTags("Files")
            .WithName("GetArchive");

        return routes;
    }

    private static async Task<IResult> ContentAsync(
        Guid id, HttpContext http, AtriumDbContext db, NodeService nodes, NodeContent content, SignedUrls signer, CancellationToken ct)
    {
        if (await ActorAsync(http, signer, db, ContentOptions, ct) is not { } actor)
        {
            return Results.Unauthorized();
        }

        if (await nodes.FindAsync(actor, id, ct) is not { } found)
        {
            return Results.NotFound();
        }

        if (found.Node.IsFolder)
        {
            return FileHttp.Problem(FileErrors.NotAFile);
        }

        var inline = http.Request.Query[InlineOption] == "1";
        return await ServeAsync(http, () => content.SourceAsync(found.Node, found.Library, ct), found.Node.Name, found.Node.MediaType, inline, ct);
    }

    private static async Task<IResult> VersionContentAsync(
        Guid id, Guid versionId, HttpContext http, AtriumDbContext db, VersionService versions, NodeService nodes,
        NodeContent content, SignedUrls signer, CancellationToken ct)
    {
        if (await ActorAsync(http, signer, db, [], ct) is not { } actor)
        {
            return Results.Unauthorized();
        }

        var version = await versions.FindAsync(actor, id, versionId, ct);
        if (!version.Ok || await nodes.FindAsync(actor, id, ct) is not { } found)
        {
            return Results.NotFound();
        }

        var v = version.Value!;
        var blob = await db.Blobs.AsNoTracking().SingleAsync(b => b.Id == v.BlobId, ct);
        return await ServeAsync(http, () => content.BlobSourceAsync(blob, v.ModifiedAt, ct), found.Node.Name, found.Node.MediaType, inline: false, ct);
    }

    private static async Task<Results<Ok<SignedUrlResponse>, ProblemHttpResult>> ContentUrlAsync(
        Guid id, ContentUrlRequest? request, HttpContext http, NodeService nodes, VersionService versions, SignedUrls signer, CancellationToken ct)
    {
        var actor = http.User.Actor();
        if (await nodes.FindAsync(actor, id, ct) is not { } found)
        {
            return FileHttp.Problem(FileErrors.NotFound);
        }

        if (found.Node.IsFolder)
        {
            return FileHttp.Problem(FileErrors.NotAFile);
        }

        var sessionId = http.User.SessionId()!.Value;
        if (request?.VersionId is { } versionId)
        {
            if (!(await versions.FindAsync(actor, id, versionId, ct)).Ok)
            {
                return FileHttp.Problem(FileErrors.NotFound);
            }

            var (versionUrl, versionExpires) = signer.Sign($"/api/v1/nodes/{id}/versions/{versionId}/content", sessionId);
            return TypedResults.Ok(new SignedUrlResponse(versionUrl, versionExpires));
        }

        var options = request?.Inline == true ? new Dictionary<string, string> { [InlineOption] = "1" } : null;
        var (url, expires) = signer.Sign($"/api/v1/nodes/{id}/content", sessionId, options);
        return TypedResults.Ok(new SignedUrlResponse(url, expires));
    }

    private static async Task<Results<Ok<SignedUrlResponse>, ProblemHttpResult>> CreateArchiveAsync(
        ArchiveRequest request, HttpContext http, NodeService nodes, ArchiveRequests archives, SignedUrls signer, CancellationToken ct)
    {
        if (request.Ids is not { Count: > 0 and <= MaxArchiveRoots } || request.Ids.Distinct().Count() != request.Ids.Count)
        {
            return ApiProblem.Invalid();
        }

        var actor = http.User.Actor();
        foreach (var id in request.Ids)
        {
            if (await nodes.FindAsync(actor, id, ct) is not { InTrash: false })
            {
                return FileHttp.Problem(FileErrors.NotFound);
            }
        }

        var token = archives.Add(actor.UserId, request.Ids);
        var (url, expires) = signer.Sign($"/api/v1/downloads/archive/{token}", http.User.SessionId()!.Value);
        return TypedResults.Ok(new SignedUrlResponse(url, expires));
    }

    /// <summary>
    /// Streams a ZIP while reading the files, never holding an archive in
    /// memory or on disk. Folders are included with everything in them
    /// that is not in the trash.
    /// </summary>
    private static async Task<IResult> ArchiveAsync(
        string token, HttpContext http, AtriumDbContext db, NodeService nodes, NodeContent content,
        ArchiveRequests archives, SignedUrls signer, CancellationToken ct)
    {
        if (await signer.VerifyAsync(http.Request, [], db, ct) is not { } actor
            || archives.Find(token, actor.UserId) is not { } ids)
        {
            return Results.NotFound();
        }

        var entries = new List<(string Path, Node Node)>();
        var libraries = new Dictionary<Guid, Library>();
        foreach (var id in ids)
        {
            if (await nodes.FindAsync(actor, id, ct) is not { InTrash: false } found)
            {
                return Results.NotFound();
            }

            libraries[found.Library.Id] = found.Library;
            entries.Add((found.Node.Name, found.Node));
            if (found.Node.IsFolder)
            {
                var below = await NodeTree.DescendantsAsync(db, [found.Node.Id], MaxArchiveItems, ct);
                if (below is null)
                {
                    return FileHttp.Problem(FileErrors.TooManyItems, new Dictionary<string, object?> { ["max"] = MaxArchiveItems });
                }

                // Files a scan no longer finds are not offered.
                var live = NodeService.LiveSubtree(found.Node.Id, below.Where(n => n.MissingSince is null).ToList());
                AddPaths(entries, found.Node, live);
            }

            if (entries.Count > MaxArchiveItems)
            {
                return FileHttp.Problem(FileErrors.TooManyItems, new Dictionary<string, object?> { ["max"] = MaxArchiveItems });
            }
        }

        var name = ids.Count == 1 && entries[0].Node.IsFolder ? entries[0].Node.Name : "Atrium";
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = FileContent.Disposition(name + ".zip", inline: false);
        http.Response.Headers.ContentSecurityPolicy = FileContent.SandboxPolicy;
        http.Response.Headers.CacheControl = "no-store";

        await using var zip = new ZipArchive(http.Response.BodyWriter.AsStream(), ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (path, node) in entries)
        {
            if (node.IsFolder)
            {
                zip.CreateEntry(path + "/");
                continue;
            }

            ContentSource source;
            try
            {
                source = await content.SourceAsync(node, libraries[node.LibraryId], ct);
            }
            catch (ContentUnavailableException)
            {
                // An external file removed since the last scan is left out
                // rather than breaking the whole archive.
                continue;
            }

            var entry = zip.CreateEntry(path, CompressionFor(node.MediaType));
            entry.LastWriteTime = Clamp(node.ModifiedAt);
            await using var target = await entry.OpenAsync(ct);
            if (source.Size > 0)
            {
                await using var stream = await source.OpenAsync(null, ct);
                await FileContent.CopyAsync(stream, target, source.Size, ct);
            }
        }

        return Results.Empty;
    }

    private static void AddPaths(List<(string Path, Node Node)> entries, Node root, List<Node> subtree)
    {
        var paths = new Dictionary<Guid, string> { [root.Id] = root.Name };
        foreach (var node in subtree)
        {
            var path = paths[node.ParentId!.Value] + "/" + node.Name;
            paths[node.Id] = path;
            entries.Add((path, node));
        }
    }

    // Media is already compressed; compressing it again costs CPU on small
    // devices for nothing.
    private static CompressionLevel CompressionFor(string? mediaType) =>
        mediaType is not null && (mediaType.StartsWith("image/", StringComparison.Ordinal)
            || mediaType.StartsWith("video/", StringComparison.Ordinal)
            || mediaType.StartsWith("audio/", StringComparison.Ordinal)
            || mediaType is "application/zip" or "application/gzip" or "application/x-7z-compressed")
            ? CompressionLevel.NoCompression
            : CompressionLevel.Fastest;

    // ZIP stores dates between 1980 and 2107.
    private static DateTimeOffset Clamp(DateTimeOffset value) =>
        value.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
        : value.Year > 2107 ? new DateTimeOffset(2107, 12, 31, 0, 0, 0, TimeSpan.Zero)
        : value;

    private static async Task<IResult> ServeAsync(
        HttpContext http, Func<Task<ContentSource>> source, string name, string? mediaType, bool inline, CancellationToken ct)
    {
        try
        {
            await FileContent.ServeAsync(http, await source(), name, mediaType, inline, ct);
            return Results.Empty;
        }
        catch (LocationUnavailableException e)
        {
            return ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, e.Code);
        }
        catch (ContentUnavailableException) when (!http.Response.HasStarted)
        {
            return Results.NotFound();
        }
    }

    /// <summary>The signed-in user, or the one a valid signed URL stands for.</summary>
    private static async Task<FileActor?> ActorAsync(
        HttpContext http, SignedUrls signer, AtriumDbContext db, IReadOnlyCollection<string> options, CancellationToken ct) =>
        http.User.Identity?.IsAuthenticated == true
            ? http.User.Actor()
            : await signer.VerifyAsync(http.Request, options, db, ct);
}

/// <summary>
/// Archive requests waiting to be downloaded, in memory for ten minutes.
/// The list of items is kept here rather than in the URL, which would grow
/// too long for a large selection. A restart forgets them; the client then
/// asks again.
/// </summary>
public sealed class ArchiveRequests(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (Guid UserId, IReadOnlyList<Guid> Ids, DateTimeOffset Expires)> _requests = new();

    public string Add(Guid userId, IReadOnlyList<Guid> ids)
    {
        var now = time.GetUtcNow();
        foreach (var stale in _requests.Where(r => r.Value.Expires <= now).Select(r => r.Key).ToList())
        {
            _requests.TryRemove(stale, out _);
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        _requests[token] = (userId, ids.ToList(), now + Lifetime);
        return token;
    }

    public IReadOnlyList<Guid>? Find(string token, Guid userId) =>
        _requests.TryGetValue(token, out var request) && request.UserId == userId && request.Expires > time.GetUtcNow()
            ? request.Ids
            : null;
}
