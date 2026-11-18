using Atrium.Api.Auth;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Core.Media;
using Atrium.Data;
using Atrium.Files;
using Atrium.Media.Playback;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Profile">What the client plays itself, built by the shared package (DESIGN.md 7.2).</param>
/// <param name="AudioStream">The audio track to play, by its stream index; the file's default when left out.</param>
public sealed record PlaybackRequest(ClientProfile Profile, int? AudioStream = null);

/// <summary>How a video will be played, why, and what it holds (DESIGN.md 7.3).</summary>
/// <param name="Reasons">Why the original is not played as it is; empty for direct play. Clients translate the codes.</param>
/// <param name="Container">The file's container as clients name it: mp4, mkv, webm...</param>
/// <param name="Streams">Every video, audio and subtitle stream, for the track menus.</param>
/// <param name="Original">For direct play: a signed URL of the original, with range requests, that lasts the length of the video and two hours more.</param>
public sealed record PlaybackView(
    PlaybackMethod Method,
    IReadOnlyList<string> Reasons,
    string Container,
    long? DurationMs,
    int VideoStream,
    int? AudioStream,
    IReadOnlyList<MediaStream> Streams,
    SignedUrlResponse? Original);

/// <summary>Deciding how a video is played (DESIGN.md 7.2 and 7.3). Serving it comes with direct play and HLS.</summary>
public static class PlaybackEndpoints
{
    public const string NotVideo = "playback.not_video";
    public const string NotReady = "playback.not_ready";
    public const string Unreadable = "playback.unreadable";
    public const string CannotStream = "playback.client_cannot_stream";

    /// <summary>A profile longer than this is not a real client; each list stays small.</summary>
    private const int MaxEntries = 64;

    private const int MaxName = 32;

    /// <summary>
    /// How long the URL of an original lasts: the video's length and two
    /// hours for pauses, at most a day (<see cref="SignedUrls.MaxLifetime"/>).
    /// A longer pause, or a revoked session, ends it; the player then asks
    /// again.
    /// </summary>
    public static TimeSpan OriginalLifetime(long? durationMs) =>
        TimeSpan.FromMilliseconds(Math.Max(0, durationMs ?? 0)) + TimeSpan.FromHours(2);

    public static IEndpointRouteBuilder MapPlaybackEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/nodes/{id:guid}/playback", DecideAsync)
            .WithTags("Playback")
            .WithName("DecidePlayback")
            .WithDescription(
                "How this client plays the video: the original, a remux, the audio converted, or the video encoded again, " +
                "with the reasons, and for direct play a signed URL of the original. 409 playback.not_ready while the file has not been read yet.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return routes;
    }

    private static async Task<Results<Ok<PlaybackView>, ProblemHttpResult>> DecideAsync(
        Guid id, PlaybackRequest request, HttpContext http, NodeService nodes, AtriumDbContext db, SignedUrls signer, CancellationToken ct)
    {
        var (decided, problem) = await DecideForAsync(id, request.Profile, request.AudioStream, http, nodes, db, ct);
        if (decided is not { } d)
        {
            return problem!;
        }

        var (found, info, container, decision) = d;
        var node = found.Node;
        SignedUrlResponse? original = null;
        if (decision.Method == PlaybackMethod.DirectPlay)
        {
            var (url, expires) = signer.Sign(
                $"/api/v1/nodes/{node.Id}/content",
                http.User.SessionId()!.Value,
                new Dictionary<string, string> { [ContentEndpoints.InlineOption] = "1" },
                OriginalLifetime(info.DurationMs));
            original = new SignedUrlResponse(url, expires);
        }

        return TypedResults.Ok(new PlaybackView(
            decision.Method, decision.Reasons, container, info.DurationMs, decision.VideoStream, decision.AudioStream, info.Streams, original));
    }

    /// <summary>A video's facts and the decision for a client, as the decision and a stream both need them.</summary>
    internal sealed record Decided(AccessibleNode Found, MediaInfo Info, string Container, PlaybackDecision Decision);

    internal static async Task<(Decided? Decided, ProblemHttpResult? Problem)> DecideForAsync(
        Guid id, ClientProfile profile, int? audioStream, HttpContext http, NodeService nodes, AtriumDbContext db, CancellationToken ct)
    {
        if (!Valid(profile))
        {
            return (null, ApiProblem.Invalid());
        }

        if (await nodes.FindAsync(http.User.Actor(), id, ct) is not { } found || found.Node.Kind != NodeKind.File)
        {
            return (null, ApiProblem.Missing());
        }

        var node = found.Node;
        if (node.MediaKind != MediaKind.Video)
        {
            return (null, ApiProblem.Create(StatusCodes.Status400BadRequest, NotVideo));
        }

        var info = node.BlobId is { } blobId
            ? await db.MediaInfos.AsNoTracking().SingleOrDefaultAsync(m => m.BlobId == blobId, ct)
            : await db.MediaInfos.AsNoTracking().SingleOrDefaultAsync(m => m.NodeId == node.Id, ct);

        // Facts read by an earlier version lack the streams; the media job reads them again.
        if (info is null || info.ExtractorVersion < MediaInfo.CurrentExtractorVersion)
        {
            return (null, ApiProblem.Create(StatusCodes.Status409Conflict, NotReady));
        }

        var container = PlaybackDecider.Container(info.Format, node.Name, info.Streams);
        if (PlaybackDecider.Decide(profile, container, info.Streams, info.BitRate, audioStream) is not { } decision)
        {
            return (null, ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, Unreadable));
        }

        if (decision.Method != PlaybackMethod.DirectPlay && !profile.Hls)
        {
            return (null, ApiProblem.Create(
                StatusCodes.Status422UnprocessableEntity, CannotStream, new() { ["reasons"] = decision.Reasons }));
        }

        return (new Decided(found, info, container, decision), null);
    }

    private static bool Valid(ClientProfile? profile) =>
        profile is { Containers: not null, VideoCodecs: not null, AudioCodecs: not null, Hdr: not null }
        && profile.Containers.Count <= MaxEntries && profile.Containers.All(Name)
        && profile.VideoCodecs.Count <= MaxEntries
        && profile.VideoCodecs.All(v => v is not null && Name(v.Codec) && (v.Profiles is null || (v.Profiles.Count <= MaxEntries && v.Profiles.All(Name))))
        && profile.AudioCodecs.Count <= MaxEntries && profile.AudioCodecs.All(a => a is not null && Name(a.Codec))
        && profile.Hdr.Count <= MaxEntries
        && (profile.DolbyVisionProfiles is null || profile.DolbyVisionProfiles.Count <= MaxEntries)
        && profile.MaxBitRate is null or > 0;

    private static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxName;
}
