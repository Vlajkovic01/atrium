using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Data;
using Atrium.Files;
using Atrium.Media;
using Atrium.Media.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class TrickplayApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public TrickplayApiTests()
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
    public async Task Seek_pictures_are_listed_with_signed_sheets_once_made_and_only_for_the_owner()
    {
        var (token, film) = await StreamingApiTests.FilmAsync(
            _factory, _client, _dir, "ana", "film.mp4", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac"], seconds: 70);
        var (bob, _) = await StreamingApiTests.FilmAsync(
            _factory, _client, _dir, "bob", "other.mp4", ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);

        var before = await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{film.Id}/trickplay", token);
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(MediaEndpoints.NoTrickplay, await before.ProblemCodeAsync());

        // What the background job does.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
            var node = await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == film.Id);
            var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == node.LibraryId);
            var (source, path) = await scope.ServiceProvider.GetRequiredService<MediaFiles>().LocateAsync(node, library, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<Trickplay>().MakeAsync(source.Key, path!, 70_000, 160, 90, CancellationToken.None);
        }

        var view = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{film.Id}/trickplay", token)).ReadAsync<TrickplayView>();
        Assert.Equal((10_000, 7, 240, 136), (view.IntervalMs, view.Count, view.Width, view.Height));
        var sheet = await _client.GetAsync(Assert.Single(view.Sheets));
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        Assert.Equal("image/jpeg", sheet.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{film.Id}/trickplay", bob)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/api/v1/nodes/{film.Id}/trickplay/0.jpg")).StatusCode);
    }
}
