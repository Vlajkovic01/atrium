using System.Net;
using System.Net.Http.Headers;
using Atrium.Api.Endpoints;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Files.External;

namespace Atrium.Api.Tests;

public sealed class ExternalLibraryApiTests : IDisposable
{
    private const string Url = "/api/v1/admin/libraries";
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N") + "-photos");

    public ExternalLibraryApiTests()
    {
        _client = _factory.CreateClient();
        Directory.CreateDirectory(Path.Combine(_folder, "2024"));
        File.WriteAllText(Path.Combine(_folder, "2024", "beach.jpg"), "0123456789");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(string Admin, string Ana, User AnaUser)> PeopleAsync()
    {
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        var ana = await _factory.CreateUserAsync("ana", TestApi.Password);
        await _factory.CreateUserAsync("bob", TestApi.Password);
        return ((await _client.SignInAsync("root")).AccessToken, (await _client.SignInAsync("ana")).AccessToken, ana);
    }

    private async Task<AdminLibraryView> WaitForScanAsync(string adminToken, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var all = await (await _client.SendAsync(HttpMethod.Get, Url, adminToken)).ReadAsync<List<AdminLibraryView>>();
            var library = all.Single(l => l.Id == id);
            if (library.LastScanAt is not null && library.Scan is null)
            {
                return library;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The scan did not finish.");
    }

    private async Task<(AdminLibraryView Library, string Admin, string Ana)> AddAsync()
    {
        var (admin, ana, anaUser) = await PeopleAsync();
        var response = await _client.SendAsync(HttpMethod.Post, Url, admin, new { name = "Photos", path = _folder, userIds = new[] { anaUser.Id } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadAsync<AdminLibraryView>();
        return (await WaitForScanAsync(admin, created.Id), admin, ana);
    }

    [Fact]
    public async Task Only_an_admin_manages_libraries()
    {
        var (_, ana, _) = await PeopleAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Get, Url, ana)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Post, Url, ana, new { name = "x", path = _folder })).StatusCode);
    }

    [Fact]
    public async Task A_folder_is_tried_before_it_is_added()
    {
        var (admin, _, _) = await PeopleAsync();

        var bad = await (await _client.SendAsync(HttpMethod.Post, $"{Url}/check-path", admin, new { path = _factory.ConfigDir })).ReadAsync<CheckPathResponse>();
        var good = await (await _client.SendAsync(HttpMethod.Post, $"{Url}/check-path", admin, new { path = _folder })).ReadAsync<CheckPathResponse>();
        var refused = await _client.SendAsync(HttpMethod.Post, Url, admin, new { name = "Config", path = _factory.ConfigDir });

        Assert.Equal(LibraryErrors.PathProtected, bad.ErrorCode);
        Assert.True(good.Ok);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(LibraryErrors.PathProtected, await refused.ProblemCodeAsync());
    }

    [Fact]
    public async Task An_added_folder_is_scanned_and_read_only_for_those_with_access()
    {
        var (library, admin, ana) = await AddAsync();
        Assert.Equal(1, library.FileCount);
        Assert.True(library.ReadOnly);
        Assert.Equal(LibraryState.Online, library.State);

        var anaLibraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", ana)).ReadAsync<List<LibraryInfo>>();
        var photos = Assert.Single(anaLibraries, l => l.Id == library.Id);
        Assert.True(photos.ReadOnly);
        Assert.Equal(LibraryMode.External, photos.Mode);

        // The admin did not give themselves access, so they do not see it as a library.
        var adminLibraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", admin)).ReadAsync<List<LibraryInfo>>();
        Assert.DoesNotContain(adminLibraries, l => l.Id == library.Id);

        var bob = (await _client.SignInAsync("bob")).AccessToken;
        var bobLibraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", bob)).ReadAsync<List<LibraryInfo>>();
        Assert.DoesNotContain(bobLibraries, l => l.Id == library.Id);
    }

    [Fact]
    public async Task A_file_in_the_folder_is_served_whole_and_in_ranges()
    {
        var (library, _, ana) = await AddAsync();
        var root = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{(await RootAsync(ana, library.Id))}/children", ana)).ReadAsync<NodePageProbe>();
        var year = Assert.Single(root.Items);
        var inside = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{year.Id}/children", ana)).ReadAsync<NodePageProbe>();
        var beach = Assert.Single(inside.Items);
        Assert.False(beach.CanWrite);

        var whole = await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{beach.Id}/content", ana);
        Assert.Equal("0123456789", await whole.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/nodes/{beach.Id}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ana);
        request.Headers.Range = new RangeHeaderValue(2, 4);
        var part = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("234", await part.Content.ReadAsStringAsync());

        // Removed outside Atrium after the scan: gone, not an error.
        File.Delete(Path.Combine(_folder, "2024", "beach.jpg"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{beach.Id}/content", ana)).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_change_the_folder_through_atrium()
    {
        var (library, _, ana) = await AddAsync();
        var rootId = await RootAsync(ana, library.Id);

        var folder = await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes", ana, new { parentId = rootId, name = "New" });

        Assert.Equal(HttpStatusCode.Forbidden, folder.StatusCode);
        Assert.Equal(["2024"], Directory.GetFileSystemEntries(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Access_is_changed_and_deleting_needs_the_name_and_keeps_the_files()
    {
        var (library, admin, ana) = await AddAsync();

        var patch = await _client.SendAsync(
            HttpMethod.Patch, $"{Url}/{library.Id}", admin, new { userIds = Array.Empty<Guid>() }, ifMatch: $"\"{library.Version}\"");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var anaLibraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", ana)).ReadAsync<List<LibraryInfo>>();
        Assert.DoesNotContain(anaLibraries, l => l.Id == library.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(HttpMethod.Delete, $"{Url}/{library.Id}?confirmName=photos", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Delete, $"{Url}/{library.Id}?confirmName=Photos", admin)).StatusCode);
        Assert.True(File.Exists(Path.Combine(_folder, "2024", "beach.jpg")));
    }

    private async Task<Guid> RootAsync(string token, Guid libraryId)
    {
        var libraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token)).ReadAsync<List<LibraryInfo>>();
        return libraries.Single(l => l.Id == libraryId).RootNodeId;
    }

    private sealed record NodePageProbe(List<NodeInfo> Items);
}
