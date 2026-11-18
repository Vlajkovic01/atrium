using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Core.Media;
using Atrium.Files;
using Atrium.Files.Blobs;
using Atrium.Media.Playback;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class PlaybackApiTests : IDisposable
{
    private static readonly MediaStream[] FilmStreams =
    [
        new(0, MediaStreamType.Video, "h264", "High", 41, 8, 1920, 1080, 23.976),
        new(1, MediaStreamType.Audio, "aac", Channels: 2, Language: "eng", IsDefault: true),
        new(2, MediaStreamType.Audio, "dts", Channels: 6, Language: "srp"),
        new(3, MediaStreamType.Subtitle, "subrip", Language: "srp", IsForced: true),
    ];

    private static readonly ClientProfile Browser = new(
        ["mp4", "webm"],
        [new VideoCodecSupport("h264", MaxLevel: 5.1)],
        [new AudioCodecSupport("aac", 2)],
        [],
        Hls: true);

    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public PlaybackApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// A file of the user's with the given name and, when given, facts as the
    /// media job would have read them. It is added under a name no media job
    /// reads and then named, so no job can replace the facts set here.
    /// </summary>
    private async Task<(string Token, Node Node)> FileAsync(string user, string name, int? extractorVersion = MediaInfo.CurrentExtractorVersion)
    {
        if (!await _factory.WithDbResultAsync(db => db.StorageLocations.AnyAsync()))
        {
            await _factory.AddLocalStorageAsync();
        }

        if (!await _factory.WithDbResultAsync(db => db.Users.AnyAsync(u => u.Name == user)))
        {
            await _factory.CreateUserAsync(user, TestApi.Password);
        }

        var token = (await _client.SignInAsync(user)).AccessToken;
        await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Atrium.Data.AtriumDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Name == user);
        var library = await db.Libraries.SingleAsync(l => l.OwnerUserId == owner.Id);
        var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>()
            .StoreAsync(library.StorageLocationId!.Value, new MemoryStream([1, 2, 3, (byte)user.Length]), null, CancellationToken.None);
        var node = (await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
            new FileActor(owner.Id, false, false, false), library.RootNodeId, name + ".part", blob, name.EndsWith(".mkv", StringComparison.Ordinal) ? "video/x-matroska" : null, null, ConflictPolicy.Fail, CancellationToken.None)).Value!;
        var stored = await db.Nodes.SingleAsync(n => n.Id == node.Id);
        stored.Name = name;
        stored.MediaKind = Path.GetExtension(name) == ".jpg" ? MediaKind.Photo : MediaKind.Video;
        await db.SaveChangesAsync();
        node = stored;

        if (extractorVersion is { } version)
        {
            var info = await db.MediaInfos.SingleOrDefaultAsync(m => m.BlobId == node.BlobId);
            if (info is null)
            {
                info = new MediaInfo { BlobId = node.BlobId };
                db.MediaInfos.Add(info);
            }

            info.Kind = MediaKind.Video;
            info.Format = "matroska,webm";
            info.DurationMs = 5_400_000;
            info.BitRate = 9_000_000;
            info.Streams = FilmStreams;
            info.ExtractorVersion = version;
            await db.SaveChangesAsync();
        }

        return (token, node);
    }

    private Task<HttpResponseMessage> DecideAsync(Guid id, string? token, ClientProfile profile, int? audioStream = null) =>
        _client.SendAsync(HttpMethod.Post, $"/api/v1/nodes/{id}/playback", token, new PlaybackRequest(profile, audioStream));

    [Fact]
    public async Task A_browser_gets_a_remux_of_an_mkv_with_the_reason_and_every_track()
    {
        var (token, film) = await FileAsync("ana", "film.mkv");

        var response = await DecideAsync(film.Id, token, Browser);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.ReadAsync<PlaybackView>();
        Assert.Equal(PlaybackMethod.Remux, view.Method);
        Assert.Equal([PlaybackReasons.Container], view.Reasons);
        Assert.Equal(("mkv", 5_400_000L, 0, 1), (view.Container, view.DurationMs!.Value, view.VideoStream, view.AudioStream!.Value));

        // The streams came back from the database as they were stored.
        Assert.Equal(FilmStreams, view.Streams);

        // Streaming comes from a session, not from the original.
        Assert.Null(view.Original);
    }

    [Fact]
    public async Task Direct_play_gets_a_signed_URL_of_the_original_that_lasts_the_film_and_serves_ranges()
    {
        var (token, film) = await FileAsync("ana", "film.mkv");
        var before = DateTimeOffset.UtcNow;

        var view = await (await DecideAsync(film.Id, token, Browser with { Containers = ["mkv"] })).ReadAsync<PlaybackView>();

        var original = Assert.IsType<SignedUrlResponse>(view.Original);
        var lasts = original.ExpiresAt - before;
        Assert.InRange(lasts, TimeSpan.FromHours(3.5) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(3.5) + TimeSpan.FromMinutes(1));

        // A media element sends no token; the signature is the access, and it seeks with ranges.
        using var request = new HttpRequestMessage(HttpMethod.Get, original.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
        var part = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("bytes 1-2/4", part.Content.Headers.ContentRange?.ToString());
        Assert.Equal(new byte[] { 2, 3 }, await part.Content.ReadAsByteArrayAsync());
        Assert.Equal("inline", part.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("video/x-matroska", part.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void No_signed_URL_lasts_longer_than_a_day()
    {
        var signer = _factory.Services.GetRequiredService<Atrium.Api.Files.SignedUrls>();
        var before = DateTimeOffset.UtcNow;

        var (_, expires) = signer.Sign("/api/v1/nodes/x/content", Guid.NewGuid(), null, TimeSpan.FromHours(30));

        Assert.InRange(expires - before, TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(null, 2.0)]
    [InlineData(1_800_000L, 2.5)]
    [InlineData(5_400_000L, 3.5)]
    [InlineData(36_000_000L, 12.0)]
    public void The_original_lasts_the_video_and_two_hours(long? durationMs, double hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), PlaybackEndpoints.OriginalLifetime(durationMs));
    }

    [Fact]
    public async Task A_client_that_opens_mkv_plays_the_original_and_another_track_changes_the_decision()
    {
        var (token, film) = await FileAsync("ana", "film.mkv");
        var player = Browser with { Containers = ["mkv"] };

        var original = await (await DecideAsync(film.Id, token, player)).ReadAsync<PlaybackView>();
        var serbian = await (await DecideAsync(film.Id, token, player, audioStream: 2)).ReadAsync<PlaybackView>();

        Assert.Equal(PlaybackMethod.DirectPlay, original.Method);
        Assert.Empty(original.Reasons);
        Assert.Equal((PlaybackMethod.AudioTranscode, 2), (serbian.Method, serbian.AudioStream!.Value));
        Assert.Equal([PlaybackReasons.AudioCodec], serbian.Reasons);
    }

    [Fact]
    public async Task A_client_without_HLS_is_told_it_cannot_play_what_needs_streaming()
    {
        var (token, film) = await FileAsync("ana", "film.mkv");

        var response = await DecideAsync(film.Id, token, Browser with { Hls = false });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PlaybackEndpoints.CannotStream, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_video_not_read_yet_or_read_by_an_older_version_is_not_ready()
    {
        var (token, unread) = await FileAsync("ana", "new.mp4", extractorVersion: null);
        var (_, old) = await FileAsync("ana", "old.mkv", extractorVersion: 1);

        foreach (var id in new[] { unread.Id, old.Id })
        {
            var response = await DecideAsync(id, token, Browser);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(PlaybackEndpoints.NotReady, await response.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task Only_the_owners_videos_and_only_valid_profiles_are_decided()
    {
        var (token, film) = await FileAsync("ana", "film.mkv");
        var (bob, _) = await FileAsync("bob", "other.mkv");
        var (_, photo) = await FileAsync("ana", "photo.jpg", extractorVersion: null);

        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(film.Id, bob, Browser)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DecideAsync(film.Id, null, Browser)).StatusCode);

        var notVideo = await DecideAsync(photo.Id, token, Browser);
        Assert.Equal(HttpStatusCode.BadRequest, notVideo.StatusCode);
        Assert.Equal(PlaybackEndpoints.NotVideo, await notVideo.ProblemCodeAsync());

        var endless = Browser with { Containers = Enumerable.Range(0, 1000).Select(i => $"c{i}").ToList() };
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(film.Id, token, endless)).StatusCode);
        var unnamed = Browser with { VideoCodecs = [new VideoCodecSupport(new string('x', 200))] };
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(film.Id, token, unnamed)).StatusCode);
    }
}
