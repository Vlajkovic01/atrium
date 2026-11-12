using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class DownloadApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public DownloadApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<(string Token, LibraryInfo Library)> SignInAsync(string name)
    {
        var any = false;
        await _factory.WithDbAsync(async db => any = await db.StorageLocations.AnyAsync());
        if (!any)
        {
            await _factory.AddLocalStorageAsync();
        }

        await _factory.CreateUserAsync(name, TestApi.Password);
        var token = (await _client.SignInAsync(name)).AccessToken;
        var libraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token)).ReadAsync<List<LibraryInfo>>();
        return (token, Assert.Single(libraries));
    }

    private async Task<Node> AddFileAsync(string userName, Guid parentId, string name, string content, string mediaType = "text/plain", ConflictPolicy policy = ConflictPolicy.Fail)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var user = await db.Users.SingleAsync(u => u.Name == userName);
        var library = await db.Libraries.SingleAsync(l => l.OwnerUserId == user.Id);
        var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>()
            .StoreAsync(library.StorageLocationId, new MemoryStream(Encoding.UTF8.GetBytes(content)), null, CancellationToken.None);
        var result = await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
            new FileActor(user.Id, false, false, false), parentId, name, blob, mediaType, null, policy, CancellationToken.None);
        return result.Value!;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? token, Action<HttpRequestMessage>? configure = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        configure?.Invoke(request);
        return await _client.SendAsync(request);
    }

    private async Task<string> SignedUrlAsync(string token, Guid nodeId, object? body = null) =>
        (await (await _client.SendAsync(HttpMethod.Post, $"/api/v1/nodes/{nodeId}/content-url", token, body ?? new { })).ReadAsync<SignedUrlResponse>()).Url;

    [Fact]
    public async Task A_file_downloads_as_an_attachment_inside_a_sandbox()
    {
        var (token, library) = await SignInAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "Račun 2024.txt", "hello, world");

        var response = await GetAsync($"/api/v1/nodes/{file.Id}/content", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello, world", await response.Content.ReadAsStringAsync());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("Račun 2024.txt", response.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("sandbox", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hello, world")))}\"", response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Ranges_are_served_for_players_and_resumed_downloads()
    {
        var (token, library) = await SignInAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "digits.txt", "0123456789");
        var url = $"/api/v1/nodes/{file.Id}/content";

        var middle = await GetAsync(url, token, r => r.Headers.Range = new RangeHeaderValue(2, 5));
        var suffix = await GetAsync(url, token, r => r.Headers.Range = new RangeHeaderValue(null, 3));
        var open = await GetAsync(url, token, r => r.Headers.Range = new RangeHeaderValue(7, null));
        var beyond = await GetAsync(url, token, r => r.Headers.Range = new RangeHeaderValue(20, 30));
        var staleIfRange = await GetAsync(url, token, r =>
        {
            r.Headers.Range = new RangeHeaderValue(2, 5);
            r.Headers.IfRange = new RangeConditionHeaderValue("\"not-this-content\"");
        });

        Assert.Equal(HttpStatusCode.PartialContent, middle.StatusCode);
        Assert.Equal("2345", await middle.Content.ReadAsStringAsync());
        Assert.Equal("bytes 2-5/10", middle.Content.Headers.ContentRange!.ToString());
        Assert.Equal("789", await suffix.Content.ReadAsStringAsync());
        Assert.Equal("789", await open.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staleIfRange.StatusCode);
        Assert.Equal("0123456789", await staleIfRange.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unchanged_content_answers_304_and_head_sends_no_body()
    {
        var (token, library) = await SignInAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "a.txt", "content");
        var url = $"/api/v1/nodes/{file.Id}/content";
        var etag = (await GetAsync(url, token)).Headers.ETag!;

        var notModified = await GetAsync(url, token, r => r.Headers.IfNoneMatch.Add(etag));
        var head = await GetAsync(url, token, method: HttpMethod.Head);

        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(7, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Content_needs_a_sign_in_or_a_valid_signature()
    {
        var (token, library) = await SignInAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "a.txt", "secret");
        var signed = await SignedUrlAsync(token, file.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync($"/api/v1/nodes/{file.Id}/content", null)).StatusCode);
        Assert.Equal("secret", await (await GetAsync(signed, null)).Content.ReadAsStringAsync());

        var tampered = signed.Replace($"/nodes/{file.Id}/", $"/nodes/{library.RootNodeId}/", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(tampered, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(signed + "&inline=1", null)).StatusCode);

        _factory.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(signed, null)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_invalidates_the_signed_urls_of_that_device()
    {
        var (token, library) = await SignInAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "a.txt", "secret");
        var signed = await SignedUrlAsync(token, file.Id);

        await _client.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(signed, null)).StatusCode);
    }

    [Fact]
    public async Task Only_safe_types_are_ever_shown_inline()
    {
        var (token, library) = await SignInAsync("ana");
        var page = await AddFileAsync("ana", library.RootNodeId, "page.html", "<script>alert(1)</script>", "text/html");
        var picture = await AddFileAsync("ana", library.RootNodeId, "photo.jpg", "not really a jpeg", "image/jpeg");
        var vector = await AddFileAsync("ana", library.RootNodeId, "logo.svg", "<svg onload='alert(1)'/>", "image/svg+xml");

        var html = await GetAsync(await SignedUrlAsync(token, page.Id, new { inline = true }), null);
        var jpeg = await GetAsync(await SignedUrlAsync(token, picture.Id, new { inline = true }), null);
        var svg = await GetAsync(await SignedUrlAsync(token, vector.Id, new { inline = true }), null);

        Assert.Equal("attachment", html.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", html.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", svg.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("inline", jpeg.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("image/jpeg", jpeg.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Another_users_content_is_not_found_and_cannot_be_signed()
    {
        var (anaToken, library) = await SignInAsync("ana");
        var (bobToken, _) = await SignInAsync("bob");
        var file = await AddFileAsync("ana", library.RootNodeId, "a.txt", "ana's");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/v1/nodes/{file.Id}/content", bobToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Post, $"/api/v1/nodes/{file.Id}/content-url", bobToken, new { })).StatusCode);
        Assert.NotNull(anaToken);
    }

    [Fact]
    public async Task An_earlier_version_can_be_downloaded()
    {
        var (token, library) = await SignInAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "doc.txt", "first");
        await AddFileAsync("ana", library.RootNodeId, "doc.txt", "second", policy: ConflictPolicy.Replace);
        var version = Assert.Single(await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{file.Id}/versions", token)).ReadAsync<List<VersionInfo>>());

        var current = await GetAsync($"/api/v1/nodes/{file.Id}/content", token);
        var earlier = await GetAsync($"/api/v1/nodes/{file.Id}/versions/{version.Id}/content", token);
        var signed = await GetAsync(await SignedUrlAsync(token, file.Id, new { versionId = version.Id }), null);

        Assert.Equal("second", await current.Content.ReadAsStringAsync());
        Assert.Equal("first", await earlier.Content.ReadAsStringAsync());
        Assert.Equal("first", await signed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_folder_downloads_as_a_zip_without_what_is_in_the_trash()
    {
        var (token, library) = await SignInAsync("ana");
        var trip = await CreateFolderAsync(token, library.RootNodeId, "Trip");
        var day = await CreateFolderAsync(token, trip, "Day 1");
        await AddFileAsync("ana", trip, "notes.txt", "notes");
        await AddFileAsync("ana", day, "photo.jpg", "pixels", "image/jpeg");
        var deleted = await AddFileAsync("ana", trip, "deleted.txt", "gone");
        await _client.SendAsync(HttpMethod.Delete, $"/api/v1/nodes/{deleted.Id}", token);

        var prepared = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/downloads/archive", token, new { ids = new[] { trip } })).ReadAsync<SignedUrlResponse>();
        var response = await GetAsync(prepared.Url, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Trip.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["Trip/", "Trip/Day 1/", "Trip/Day 1/photo.jpg", "Trip/notes.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var reader = new StreamReader(zip.GetEntry("Trip/Day 1/photo.jpg")!.Open());
        Assert.Equal("pixels", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task An_archive_link_works_only_for_the_user_who_asked_for_it()
    {
        var (token, library) = await SignInAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "a.txt", "a");
        var prepared = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/downloads/archive", token, new { ids = new[] { file.Id } })).ReadAsync<SignedUrlResponse>();

        var forged = prepared.Url.Replace("/archive/", "/archive/x", StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(forged, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(prepared.Url, null)).StatusCode);
    }

    private async Task<Guid> CreateFolderAsync(string token, Guid parentId, string name) =>
        (await (await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes", token, new { parentId, name })).ReadAsync<Atrium.Api.Files.NodeInfo>()).Id;
}
