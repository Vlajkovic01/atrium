using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Files;
using Atrium.Files.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class MediaApiTests : IDisposable
{
    // A 2 by 1 PNG: one red pixel, one blue.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAEElEQVR4nGP4z8DAwMDAAAAN/wH/1GN7AAAAAElFTkSuQmCC");

    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public MediaApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<(string Token, Node Photo)> PhotoAsync(string user)
    {
        if (!await _factory.WithDbResultAsync(db => db.StorageLocations.AnyAsync()))
        {
            await _factory.AddLocalStorageAsync();
        }

        await _factory.CreateUserAsync(user, TestApi.Password);
        var token = (await _client.SignInAsync(user)).AccessToken;
        await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Atrium.Data.AtriumDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Name == user);
        var library = await db.Libraries.SingleAsync(l => l.OwnerUserId == owner.Id);
        var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>()
            .StoreAsync(library.StorageLocationId!.Value, new MemoryStream(Png), null, CancellationToken.None);
        var added = await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
            new FileActor(owner.Id, false, false, false), library.RootNodeId, "dot.png", blob, "image/png", null, ConflictPolicy.Fail, CancellationToken.None);
        return (token, added.Value!);
    }

    [Fact]
    public async Task A_thumbnail_is_made_on_first_request_and_only_for_its_owner()
    {
        var (token, photo) = await PhotoAsync("ana");
        var (bob, _) = await PhotoAsync("bob");

        var response = await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{photo.Id}/thumbnail?size=small", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{photo.Id}/thumbnail", bob)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{photo.Id}/thumbnail?size=huge", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/api/v1/nodes/{photo.Id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task The_timeline_gives_signed_thumbnails_that_an_image_element_can_load()
    {
        var (token, photo) = await PhotoAsync("ana");

        var buckets = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/timeline/buckets", token)).ReadAsync<List<TimelineBucketView>>();
        var page = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/timeline?limit=50", token)).ReadAsync<PhotoPageView>();

        Assert.Equal(1, Assert.Single(buckets).Count);
        var item = Assert.Single(page.Items);
        Assert.Equal(photo.Id, item.Id);

        // No token: the signature is the access.
        var image = await _client.GetAsync(item.ThumbnailUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(item.ThumbnailUrl.Replace("size=small", "size=large", StringComparison.Ordinal))).StatusCode);
    }

    [Fact]
    public async Task Albums_and_search_work_over_http()
    {
        var (token, photo) = await PhotoAsync("ana");

        var created = await _client.SendAsync(HttpMethod.Post, "/api/v1/albums", token, new { name = "Dots", nodeIds = new[] { photo.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var album = await created.ReadAsync<AlbumView>();
        Assert.Equal(1, album.ItemCount);
        Assert.NotNull(album.CoverUrl);

        var items = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/albums/{album.Id}/items", token)).ReadAsync<PhotoPageView>();
        Assert.Single(items.Items);

        var found = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/search?q=DOT&type=photo", token)).ReadAsync<SearchPageView>();
        Assert.Equal(photo.Id, Assert.Single(found.Items).Node.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(HttpMethod.Get, "/api/v1/search?type=spreadsheet", token)).StatusCode);

        var renamed = await _client.SendAsync(HttpMethod.Patch, $"/api/v1/albums/{album.Id}", token, new { name = "Two dots" }, ifMatch: $"\"{album.Version}\"");
        Assert.Equal("Two dots", (await renamed.ReadAsync<AlbumView>()).Name);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Delete, $"/api/v1/albums/{album.Id}", token)).StatusCode);
    }

    [Fact]
    public async Task Media_info_reports_what_is_known_so_far()
    {
        var (token, photo) = await PhotoAsync("ana");

        var info = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{photo.Id}/media-info", token)).ReadAsync<MediaInfoView>();

        Assert.Equal(MediaKind.Photo, info.Kind);
        Assert.NotNull(info.CapturedAt);
    }
}
