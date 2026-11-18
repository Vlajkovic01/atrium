using System.Globalization;
using Atrium.Api.Auth;
using Atrium.Api.Files;
using Atrium.Core.Media;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.Storage;
using Atrium.Identity.Sessions;
using Atrium.Media;
using Atrium.Media.Playback;
using Atrium.Media.Streaming;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="StartMs">Where watching starts, for a resume; the start when left out.</param>
public sealed record StreamRequest(Guid NodeId, ClientProfile Profile, int? AudioStream = null, long? StartMs = null);

/// <summary>A stream that was opened (D-062).</summary>
/// <param name="Id">The stream's identifier, which its URLs carry.</param>
/// <param name="MasterUrl">The HLS master playlist; the stream's other URLs are relative to it.</param>
public sealed record StreamView(
    string Id,
    PlaybackMethod Method,
    IReadOnlyList<string> Reasons,
    string MasterUrl,
    long? DurationMs,
    int VideoStream,
    int? AudioStream,
    IReadOnlyList<MediaStream> Streams);

/// <summary>Streaming a video as HLS, produced on demand (DESIGN.md 7.4, D-062).</summary>
public static class StreamingEndpoints
{
    public const string DirectPlay = "playback.direct_play";
    public const string Busy = "playback.busy";
    public const string TooManyStreams = "playback.too_many_streams";
    public const string RemoteStorage = "playback.remote_storage";
    public const string StreamFailed = "playback.stream_failed";

    /// <summary>Files larger than this are not scanned for keyframes while a person waits; their video is encoded instead.</summary>
    private const long MaxScanWhileWaiting = 2L * 1024 * 1024 * 1024;

    public static IEndpointRouteBuilder MapStreamingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/playback/sessions", OpenAsync)
            .WithTags("Playback")
            .WithName("OpenStream")
            .WithDescription(
                "Opens an HLS stream of a video that does not play as it is. The decision is made again here. " +
                "409 playback.direct_play when the original plays; 503 playback.busy at the server's limit; " +
                "429 playback.too_many_streams at the person's.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        routes.MapDelete("/playback/sessions/{id}", CloseAsync)
            .WithTags("Playback")
            .WithName("CloseStream")
            .WithDescription("Ends a stream and frees what it holds on the server.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Media elements send no token: the stream's identifier, random and
        // kept only for the session that opened it, is the access (D-062).
        var stream = routes.MapGroup("/stream/{id}").AllowAnonymous().WithTags("Playback");
        stream.MapGet("/master.m3u8", MasterAsync).WithName("GetStreamMaster");
        stream.MapGet("/index.m3u8", Index).WithName("GetStreamPlaylist");
        stream.MapGet("/init.mp4", InitAsync).WithName("GetStreamInit");
        stream.MapGet("/{segment:int}.m4s", SegmentAsync).WithName("GetStreamSegment");

        // FFmpeg's way to files on remote storage; never part of the public API (D-063).
        routes.MapGet("/internal/sources/{token}", SourceAsync).AllowAnonymous().ExcludeFromDescription();
        return routes;
    }

    private static async Task<Results<Created<StreamView>, ProblemHttpResult>> OpenAsync(
        StreamRequest request,
        HttpContext http,
        NodeService nodes,
        AtriumDbContext db,
        MediaFiles files,
        KeyframeIndex keyframes,
        StreamSessions sessions,
        LoopbackSources loopback,
        CancellationToken ct)
    {
        var (decided, problem) = await PlaybackEndpoints.DecideForAsync(
            request.NodeId, request.Profile, request.AudioStream, http, nodes, db, ct);
        if (decided is not { } d)
        {
            return problem!;
        }

        var (found, info, container, decision) = d;
        if (decision.Method == PlaybackMethod.DirectPlay)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, DirectPlay);
        }

        ContentSource source;
        string? localPath;
        try
        {
            (source, localPath) = await files.LocateAsync(found.Node, found.Library, ct);
        }
        catch (LocationUnavailableException e)
        {
            return ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, e.Code);
        }
        catch (ContentUnavailableException)
        {
            return ApiProblem.Missing();
        }

        // FFmpeg reads remote storage through a URL on this machine, never with its credentials.
        string? token = null;
        var input = localPath;
        if (input is null)
        {
            if (loopback.Register(found.Node.Id) is not { } registered)
            {
                return ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, RemoteStorage);
            }

            (input, token) = registered;
        }

        var video = info.Streams.First(s => s.Index == decision.VideoStream);
        var audio = info.Streams.FirstOrDefault(s => s.Index == decision.AudioStream);
        var durationUs = (info.DurationMs ?? 0) * 1000;
        IReadOnlyList<Segment> segments;
        if (decision.Method is PlaybackMethod.Remux or PlaybackMethod.AudioTranscode)
        {
            var times = await keyframes.GetAsync(source, container, input, scan: source.Size <= MaxScanWhileWaiting, ct);
            if (times is null)
            {
                decision = decision with
                {
                    Method = PlaybackMethod.Transcode,
                    Reasons = [.. decision.Reasons, PlaybackReasons.KeyframesUnknown],
                };
                segments = SegmentPlan.Fixed(durationUs, SegmentPlan.TargetUs);
            }
            else
            {
                segments = SegmentPlan.AtKeyframes(times, durationUs, SegmentPlan.TargetUs);
            }
        }
        else
        {
            segments = SegmentPlan.Fixed(durationUs, SegmentPlan.TargetUs);
        }

        if (segments.Count == 0)
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, PlaybackEndpoints.Unreadable);
        }

        var output = StreamOutput.For(decision, video, request.Profile);
        var origin = segments[0].StartUs;
        var start = SegmentPlan.At(segments, origin + (Math.Max(0, request.StartMs ?? 0) * 1000))!.Index;
        var (session, refusal) = await sessions.OpenAsync(
            http.User.UserId()!.Value,
            http.User.SessionId()!.Value,
            found.Node.Id,
            decision,
            output,
            segments,
            video,
            audio,
            start,
            _ => Task.FromResult(input));
        if (session is null)
        {
            if (token is not null)
            {
                loopback.Remove(token);
            }

            return refusal == StreamRefusal.TooManyForUser
                ? ApiProblem.Create(StatusCodes.Status429TooManyRequests, TooManyStreams)
                : ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy);
        }

        if (token is not null)
        {
            var id = session.Id;
            loopback.Bind(token, () => sessions.Find(id) is not null);
        }

        var view = new StreamView(
            session.Id,
            decision.Method,
            decision.Reasons,
            $"/api/v1/stream/{session.Id}/master.m3u8",
            info.DurationMs,
            decision.VideoStream,
            decision.AudioStream,
            info.Streams);
        return TypedResults.Created(view.MasterUrl, view);
    }

    private static async Task<IResult> CloseAsync(string id, HttpContext http, StreamSessions sessions)
    {
        if (sessions.Find(id) is not { } session || session.UserId != http.User.UserId())
        {
            return ApiProblem.Missing();
        }

        await sessions.CloseAsync(id);
        return Results.NoContent();
    }

    private static Task<IResult> MasterAsync(string id, StreamSessions sessions, SessionRevocationCache revocations)
    {
        if (Open(id, sessions, revocations) is not { } session)
        {
            return Task.FromResult(Results.NotFound());
        }

        var output = session.Output;
        string? codecs;
        long bandwidth;
        int? width = session.Video.Width;
        int? height = session.Video.Height;
        if (output.CopyVideo)
        {
            var audio = output.CopyAudio ? session.Audio : session.Audio is null ? null : new MediaStream(0, MediaStreamType.Audio, "aac");
            codecs = HlsPlaylist.Codecs(session.Video, audio);
            bandwidth = (session.Video.BitRate ?? 8_000_000) + (session.Audio?.BitRate ?? StreamOutput.AudioBitRate);
        }
        else
        {
            // Encoded: H.264 High, level 4.0 for 1080 lines, 3.1 below.
            var level = output.MaxHeight > 720 ? 40 : 31;
            var encoded = new MediaStream(0, MediaStreamType.Video, "h264", "High", level);
            codecs = HlsPlaylist.Codecs(encoded, session.Audio is null ? null : new MediaStream(0, MediaStreamType.Audio, "aac"));
            bandwidth = output.VideoBitRate + StreamOutput.AudioBitRate;
            if (width is { } w && height is { } h && h > 0)
            {
                var scale = Math.Min(1.0, output.MaxHeight / (double)Math.Min(w, h));
                (width, height) = ((int)Math.Round(w * scale / 2) * 2, (int)Math.Round(h * scale / 2) * 2);
            }
        }

        return Task.FromResult(Playlist(HlsPlaylist.Master("index.m3u8", bandwidth, width, height, codecs)));
    }

    private static IResult Index(string id, StreamSessions sessions, SessionRevocationCache revocations) =>
        Open(id, sessions, revocations) is { } session
            ? Playlist(HlsPlaylist.Media(session.Segments, "init.mp4", n => $"{n.ToString(CultureInfo.InvariantCulture)}.m4s"))
            : Results.NotFound();

    private static async Task<IResult> InitAsync(
        string id, StreamSessions sessions, SessionRevocationCache revocations, ILoggerFactory loggers, CancellationToken ct)
    {
        if (Open(id, sessions, revocations) is not { } session)
        {
            return Results.NotFound();
        }

        try
        {
            return Media(await session.InitAsync(ct), "video/mp4");
        }
        catch (StreamFailedException e)
        {
            loggers.CreateLogger(typeof(StreamingEndpoints)).LogWarning("A stream's init segment failed: {Reason}", e.Message);
            return ApiProblem.Create(StatusCodes.Status500InternalServerError, StreamFailed);
        }
    }

    private static async Task<IResult> SegmentAsync(
        string id, int segment, StreamSessions sessions, SessionRevocationCache revocations, ILoggerFactory loggers, CancellationToken ct)
    {
        if (Open(id, sessions, revocations) is not { } session || segment < 0 || segment >= session.Segments.Count)
        {
            return Results.NotFound();
        }

        try
        {
            return Media(await session.SegmentAsync(segment, ct), "video/iso.segment");
        }
        catch (StreamFailedException e)
        {
            loggers.CreateLogger(typeof(StreamingEndpoints)).LogWarning("A stream's segment failed: {Reason}", e.Message);
            return ApiProblem.Create(StatusCodes.Status500InternalServerError, StreamFailed);
        }
    }

    private static async Task<IResult> SourceAsync(
        string token, HttpContext http, LoopbackSources loopback, AtriumDbContext db, NodeContent content, CancellationToken ct)
    {
        if (!LoopbackSources.FromThisMachine(http) || loopback.Find(token) is not { } nodeId
            || await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, ct) is not { } node)
        {
            return Results.NotFound();
        }

        var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == node.LibraryId, ct);
        try
        {
            await FileContent.ServeAsync(http, await content.SourceAsync(node, library, ct), node.Name, node.MediaType, inline: false, ct);
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

    /// <summary>The stream, if it is open and the session that opened it has not been signed out.</summary>
    private static StreamSession? Open(string id, StreamSessions sessions, SessionRevocationCache revocations) =>
        sessions.Find(id) is { } session && !revocations.IsRevoked(session.AuthSessionId) ? session : null;

    private static IResult Playlist(string text) =>
        Results.Text(text, "application/vnd.apple.mpegurl");

    private static IResult Media(byte[] bytes, string type) => Results.Bytes(bytes, type);
}
