using System.Globalization;
using System.Net;
using System.Text;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Core.Media;
using Atrium.Files;
using Atrium.Files.Blobs;
using Atrium.Media;
using Atrium.Media.Playback;
using Atrium.Media.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

/// <summary>HLS made on demand by a real FFmpeg, through the API (DESIGN.md 7.4, D-062).</summary>
public sealed class StreamingApiTests : IDisposable
{
    /// <summary>Plays MP4 with H.264 and AAC through HLS, but opens no MKV.</summary>
    private static readonly ClientProfile Browser = new(
        ["mp4"], [new VideoCodecSupport("h264"), new VideoCodecSupport("vp9")], [new AudioCodecSupport("aac")], [], Hls: true);

    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public StreamingApiTests()
    {
        _client = _factory.CreateClient();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task An_mkv_is_remuxed_at_its_keyframes_and_a_seek_first_still_joins_up_with_the_start()
    {
        var (token, film) = await FilmAsync("ana", "film.mkv", ["-c:v", "libx264", "-preset", "ultrafast", "-g", "48", "-keyint_min", "48", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var opened = await _client.SendAsync(HttpMethod.Post, "/api/v1/playback/sessions", token, new StreamRequest(film.Id, Browser));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var stream = await opened.ReadAsync<StreamView>();
        Assert.Equal(PlaybackMethod.Remux, stream.Method);
        Assert.Equal([PlaybackReasons.Container], stream.Reasons);

        var master = await _client.GetStringAsync(stream.MasterUrl);
        Assert.Contains("CODECS=\"avc1.", master, StringComparison.Ordinal);
        Assert.Contains("mp4a.40.2\"", master, StringComparison.Ordinal);

        var playlist = await _client.GetStringAsync(Relative(stream.MasterUrl, "index.m3u8"));
        Assert.Equal([6.0, 6.0, 6.0, 2.0], Durations(playlist).Select(d => Math.Round(d, 1)));
        Assert.EndsWith("#EXT-X-ENDLIST\n", playlist, StringComparison.Ordinal);

        // A seek before anything was made: segment 2 first, then from the start.
        var init = await _client.GetByteArrayAsync(Relative(stream.MasterUrl, "init.mp4"));
        var parts = new Dictionary<int, byte[]> { [2] = await _client.GetByteArrayAsync(Relative(stream.MasterUrl, "2.m4s")) };
        foreach (var n in new[] { 0, 1, 3 })
        {
            parts[n] = await _client.GetByteArrayAsync(Relative(stream.MasterUrl, $"{n}.m4s"));
        }

        var joined = await JoinAsync(init, parts);
        var (duration, videoStart, errors) = await ProbeAsync(joined);
        Assert.InRange(duration, 19.8, 20.3);
        Assert.InRange(videoStart, -0.01, 0.1);
        Assert.Equal(string.Empty, errors);

        // Segment 2 on its own starts at its place in the playlist.
        var (_, secondStart, _) = await ProbeAsync(await JoinAsync(init, new() { [2] = parts[2] }));
        Assert.InRange(secondStart, 11.9, 12.1);
    }

    [Fact]
    public async Task Video_the_client_cannot_play_is_encoded_in_six_second_segments()
    {
        var client = Browser with { VideoCodecs = [new VideoCodecSupport("h264", MaxWidth: 160, MaxHeight: 90)] };
        var (token, film) = await FilmAsync("ana", "film.mp4", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac"], size: "320x180");

        var stream = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/playback/sessions", token, new StreamRequest(film.Id, client))).ReadAsync<StreamView>();

        Assert.Equal(PlaybackMethod.Transcode, stream.Method);
        Assert.Equal([PlaybackReasons.VideoResolution], stream.Reasons);
        var playlist = await _client.GetStringAsync(Relative(stream.MasterUrl, "index.m3u8"));
        Assert.Equal([6.0, 6.0, 6.0, 2.0], Durations(playlist).Select(d => Math.Round(d, 1)));

        var init = await _client.GetByteArrayAsync(Relative(stream.MasterUrl, "init.mp4"));
        var parts = new Dictionary<int, byte[]>();
        for (var n = 0; n < 4; n++)
        {
            parts[n] = await _client.GetByteArrayAsync(Relative(stream.MasterUrl, $"{n}.m4s"));
        }

        var (duration, _, errors) = await ProbeAsync(await JoinAsync(init, parts));
        Assert.InRange(duration, 19.8, 20.3);
        Assert.Equal(string.Empty, errors);
    }

    [Fact]
    public async Task A_stream_is_only_for_the_session_that_opened_it_and_ends_when_closed()
    {
        var (token, film) = await FilmAsync("ana", "film.mkv", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        var (bob, _) = await FilmAsync("bob", "other.mkv", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        var stream = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/playback/sessions", token, new StreamRequest(film.Id, Browser))).ReadAsync<StreamView>();

        // Another person can neither open this video nor end the stream.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Post, "/api/v1/playback/sessions", bob, new StreamRequest(film.Id, Browser))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Delete, $"/api/v1/playback/sessions/{stream.Id}", bob)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/stream/{stream.Id}x/index.m3u8")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Delete, $"/api/v1/playback/sessions/{stream.Id}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(stream.MasterUrl)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_streams_of_that_session()
    {
        var (token, film) = await FilmAsync("ana", "film.mkv", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        var stream = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/playback/sessions", token, new StreamRequest(film.Id, Browser))).ReadAsync<StreamView>();
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(stream.MasterUrl)).StatusCode);

        await _client.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", token);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(stream.MasterUrl)).StatusCode);
    }

    [Fact]
    public async Task The_original_is_not_streamed()
    {
        var (token, film) = await FilmAsync("ana", "film.mp4", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var response = await _client.SendAsync(HttpMethod.Post, "/api/v1/playback/sessions", token, new StreamRequest(film.Id, Browser));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(StreamingEndpoints.DirectPlay, await response.ProblemCodeAsync());
    }

    internal static string Relative(string master, string name) => master[..(master.LastIndexOf('/') + 1)] + name;

    private static List<double> Durations(string playlist) =>
        playlist.Split('\n')
            .Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
            .Select(l => double.Parse(l[8..].TrimEnd(','), CultureInfo.InvariantCulture))
            .ToList();

    private Task<string> JoinAsync(byte[] init, Dictionary<int, byte[]> parts) => JoinAsync(_dir, init, parts);

    internal static async Task<string> JoinAsync(string dir, byte[] init, Dictionary<int, byte[]> parts)
    {
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.mp4");
        await using var file = File.Create(path);
        await file.WriteAsync(init);
        foreach (var (_, part) in parts.OrderBy(p => p.Key))
        {
            await file.WriteAsync(part);
        }

        return path;
    }

    /// <summary>The duration, the video's first time, and whatever FFmpeg complains of while decoding all of it.</summary>
    internal static async Task<(double Duration, double VideoStart, string Errors)> ProbeAsync(string path)
    {
        var probe = Encoding.UTF8.GetString(await ToolRunner.RunAsync(
            "ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "format=duration:stream=start_time", "-of", "default=nw=1", path],
            TimeSpan.FromMinutes(1),
            false,
            65536,
            CancellationToken.None));
        double Value(string key) => double.Parse(
            probe.Split('\n').First(l => l.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..].Trim(), CultureInfo.InvariantCulture);
        var errors = Encoding.UTF8.GetString(await ToolRunner.RunAsync(
            "ffmpeg", ["-nostdin", "-v", "error", "-i", path, "-f", "null", "-"], TimeSpan.FromMinutes(1), false, 65536, CancellationToken.None));
        return (Value("duration"), Value("start_time"), errors.Trim());
    }

    /// <summary>A twenty-second film made by FFmpeg, stored as the user's, with its facts read as the media job would.</summary>
    private Task<(string Token, Node Node)> FilmAsync(string user, string name, string[] output, string size = "160x90") =>
        FilmAsync(_factory, _client, _dir, user, name, output, size, addLocalStorage: true);

    internal static async Task<(string Token, Node Node)> FilmAsync(
        AtriumFactory factory, HttpClient client, string dir, string user, string name, string[] output, string size = "160x90", bool addLocalStorage = true, int seconds = 20)
    {
        var source = Path.Combine(dir, $"{Guid.NewGuid():N}{Path.GetExtension(name)}");
        await ToolRunner.RunAsync(
            "ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size={size}:rate=24", "-f", "lavfi", "-i", $"sine=duration={seconds}",
                "-shortest", .. output, "-y", source],
            TimeSpan.FromMinutes(2),
            false,
            4096,
            CancellationToken.None);

        if (addLocalStorage && !await factory.WithDbResultAsync(db => db.StorageLocations.AnyAsync()))
        {
            await factory.AddLocalStorageAsync();
        }

        if (!await factory.WithDbResultAsync(db => db.Users.AnyAsync(u => u.Name == user)))
        {
            await factory.CreateUserAsync(user, TestApi.Password);
        }

        var token = (await client.SignInAsync(user)).AccessToken;
        await client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Atrium.Data.AtriumDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Name == user);
        var library = await db.Libraries.SingleAsync(l => l.OwnerUserId == owner.Id);
        await using var content = File.OpenRead(source);
        var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>().StoreAsync(library.StorageLocationId!.Value, content, null, CancellationToken.None);

        // Added under a name no media job reads, then named, so the facts below stay as set.
        var node = (await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
            new FileActor(owner.Id, false, false, false), library.RootNodeId, name + ".part", blob, null, null, ConflictPolicy.Fail, CancellationToken.None)).Value!;
        var stored = await db.Nodes.SingleAsync(n => n.Id == node.Id);
        stored.Name = name;
        stored.MediaKind = MediaKind.Video;

        var facts = await scope.ServiceProvider.GetRequiredService<MetadataReader>().ReadAsync(source, MediaKind.Video, false, CancellationToken.None);
        db.MediaInfos.Add(new MediaInfo
        {
            BlobId = node.BlobId,
            Kind = MediaKind.Video,
            Format = facts.Format,
            DurationMs = facts.DurationMs,
            BitRate = facts.BitRate,
            Width = facts.Width,
            Height = facts.Height,
            Streams = facts.Streams ?? [],
            ExtractorVersion = MediaInfo.CurrentExtractorVersion,
        });
        await db.SaveChangesAsync();
        return (token, stored);
    }
}
