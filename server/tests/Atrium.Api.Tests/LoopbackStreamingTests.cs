using System.Net;
using System.Text;
using Atrium.Api.Endpoints;
using Atrium.Api.Files;
using Atrium.Api.Storage;
using Atrium.Core.Storage;
using Atrium.Files.Storage;
using Atrium.Media;
using Atrium.Media.Playback;
using Atrium.Storage.Tests;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

/// <summary>Which address FFmpeg is given, and how long a token lasts (D-063).</summary>
public sealed class LoopbackSourcesTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5080", "http://127.0.0.1:5080")]
    [InlineData("http://+:8080", "http://127.0.0.1:8080")]
    [InlineData("http://[::]:8080", "http://[::1]:8080")]
    [InlineData("http://localhost:5000", "http://127.0.0.1:5000")]
    [InlineData("http://192.168.1.20:5080", "http://192.168.1.20:5080")]
    public void The_servers_own_address_is_taken_with_wildcards_made_loopback(string listening, string expected)
    {
        Assert.Equal(expected, Sources("https://0.0.0.0:5443", listening).BaseAddress);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData("10.0.0.2", false)]
    [InlineData(null, false)]
    public void Only_a_connection_from_this_machine_is_answered(string? remote, bool answered)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);

        Assert.Equal(answered, LoopbackSources.FromThisMachine(http));
    }

    [Fact]
    public void Without_an_address_there_is_no_URL()
    {
        Assert.Null(Sources().Register(Guid.NewGuid()));
    }

    [Fact]
    public void A_token_lasts_two_minutes_alone_then_as_long_as_its_stream()
    {
        var time = new MutableTime();
        var sources = Sources(time, "http://127.0.0.1:5080");
        var node = Guid.NewGuid();
        var (url, token) = sources.Register(node)!.Value;
        Assert.StartsWith("http://127.0.0.1:5080/api/v1/internal/sources/", url, StringComparison.Ordinal);
        Assert.Equal(node, sources.Find(token));

        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(sources.Find(token));

        var (_, bound) = sources.Register(node)!.Value;
        var alive = true;
        sources.Bind(bound, () => alive);
        time.Advance(TimeSpan.FromHours(5));
        Assert.Equal(node, sources.Find(bound));
        alive = false;
        Assert.Null(sources.Find(bound));
        Assert.Null(sources.Find("not-a-token"));
    }

    private static LoopbackSources Sources(params string[] addresses) => Sources(new MutableTime(), addresses);

    private static LoopbackSources Sources(MutableTime time, params string[] addresses) => new(new FakeServer(addresses), time);

    private sealed class FakeServer : IServer
    {
        public FakeServer(string[] addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
            {
                feature.Addresses.Add(address);
            }

            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>FFmpeg reading through a real port of a real server.</summary>
public sealed class LoopbackStreamingTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public LoopbackStreamingTests()
    {
        _factory.UseKestrel(0);
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
    public async Task FFmpeg_reads_a_file_through_its_loopback_URL_and_nobody_else_can()
    {
        var client = _factory.CreateClient();
        var (_, film) = await StreamingApiTests.FilmAsync(
            _factory, client, _dir, "ana", "film.mkv", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        var sources = _factory.Services.GetRequiredService<LoopbackSources>();
        var (url, _) = sources.Register(film.Id)!.Value;

        var probe = Encoding.UTF8.GetString(await ToolRunner.RunAsync(
            "ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", url], TimeSpan.FromMinutes(1), false, 4096, CancellationToken.None));
        Assert.InRange(double.Parse(probe.Trim(), System.Globalization.CultureInfo.InvariantCulture), 19.8, 20.3);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url + "x")).StatusCode);
    }
}

/// <summary>Streaming a film kept on S3, which FFmpeg reads only through the server (D-063). Needs Docker, so it runs in CI.</summary>
[Trait("Category", "Containers")]
public sealed class S3StreamingTests(S3ServerFixture server) : IClassFixture<S3ServerFixture>, IDisposable
{
    private readonly AtriumFactory _factory = CreateFactory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

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
    public async Task A_film_on_S3_is_remuxed_through_the_server_and_joins_up()
    {
        Directory.CreateDirectory(_dir);
        var client = _factory.CreateClient();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<StorageLocationService>().CreateAsync(
                "s3",
                StorageProviderKind.S3,
                new StorageLocationConfig(null, server.Options("streaming-" + Guid.NewGuid().ToString("N"))),
                createDirectory: false,
                CancellationToken.None);
            Assert.True(outcome.Test.Ok, outcome.Test.ErrorCode);
        }

        var (token, film) = await StreamingApiTests.FilmAsync(
            _factory,
            client,
            _dir,
            "ana",
            "film.mkv",
            ["-c:v", "libx264", "-preset", "ultrafast", "-g", "48", "-keyint_min", "48", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac"],
            addLocalStorage: false);
        var browser = new ClientProfile(["mp4"], [new VideoCodecSupport("h264")], [new AudioCodecSupport("aac")], [], Hls: true);

        var opened = await client.SendAsync(HttpMethod.Post, "/api/v1/playback/sessions", token, new StreamRequest(film.Id, browser));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var stream = await opened.ReadAsync<StreamView>();
        Assert.Equal(PlaybackMethod.Remux, stream.Method);

        var init = await client.GetByteArrayAsync(StreamingApiTests.Relative(stream.MasterUrl, "init.mp4"));
        var parts = new Dictionary<int, byte[]>();
        foreach (var n in new[] { 3, 0, 1, 2 })
        {
            parts[n] = await client.GetByteArrayAsync(StreamingApiTests.Relative(stream.MasterUrl, $"{n}.m4s"));
        }

        var (duration, _, errors) = await StreamingApiTests.ProbeAsync(await StreamingApiTests.JoinAsync(_dir, init, parts));
        Assert.InRange(duration, 19.8, 20.3);
        Assert.Equal(string.Empty, errors);
    }

    private static AtriumFactory CreateFactory()
    {
        var factory = new AtriumFactory();
        factory.UseKestrel(0);
        return factory;
    }
}
