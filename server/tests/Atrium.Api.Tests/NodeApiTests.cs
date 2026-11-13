using System.Net;
using System.Text;
using System.Text.Json;
using Atrium.Api.Endpoints;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Files;
using Atrium.Files.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class NodeApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public NodeApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<(string Token, LibraryInfo Library)> SignInWithLibraryAsync(string name)
    {
        if (!await HasStorageAsync())
        {
            await _factory.AddLocalStorageAsync();
        }

        await _factory.CreateUserAsync(name, TestApi.Password);
        var token = (await _client.SignInAsync(name)).AccessToken;
        var libraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token)).ReadAsync<List<LibraryInfo>>();
        return (token, Assert.Single(libraries));
    }

    private async Task<bool> HasStorageAsync()
    {
        var any = false;
        await _factory.WithDbAsync(async db => any = await db.StorageLocations.AnyAsync());
        return any;
    }

    private async Task<NodeInfo> CreateFolderAsync(string token, Guid parentId, string name)
    {
        var response = await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes", token, new { parentId, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<NodeInfo>();
    }

    /// <summary>Adds a file the way a finished upload will, until uploads exist over HTTP.</summary>
    private async Task<Node> AddFileAsync(string userName, Guid parentId, string name, string content, ConflictPolicy policy = ConflictPolicy.Fail)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Atrium.Data.AtriumDbContext>();
        var user = await db.Users.SingleAsync(u => u.Name == userName);
        var library = await db.Libraries.SingleAsync(l => l.OwnerUserId == user.Id);
        var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>()
            .StoreAsync(library.StorageLocationId!.Value, new MemoryStream(Encoding.UTF8.GetBytes(content)), null, CancellationToken.None);
        var result = await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
            new FileActor(user.Id, false, false, false), parentId, name, blob, "text/plain", null, policy, CancellationToken.None);
        return result.Value!;
    }

    private static async Task<Dictionary<string, JsonElement>> ProblemParamsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("params").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public async Task A_folder_is_created_listed_and_found_by_its_path()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var photos = await CreateFolderAsync(token, library.RootNodeId, "Photos");
        var trip = await CreateFolderAsync(token, photos.Id, "Trip");

        var page = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{library.RootNodeId}/children", token)).ReadAsync<NodePage>();
        var path = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{trip.Id}/path", token)).ReadAsync<List<NodeInfo>>();
        var single = await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{trip.Id}", token);

        Assert.Equal("Photos", Assert.Single(page.Items).Name);
        Assert.Equal(1, page.Total);
        Assert.Null(page.NextCursor);
        Assert.Equal([library.RootNodeId, photos.Id], path.Select(p => p.Id));
        Assert.Equal($"\"{trip.Version}\"", single.Headers.ETag?.Tag);
        Assert.True((await single.ReadAsync<NodeInfo>()).CanWrite);
    }

    [Fact]
    public async Task Sort_and_filter_values_are_accepted_as_the_contract_spells_them()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        await CreateFolderAsync(token, library.RootNodeId, "b");
        await CreateFolderAsync(token, library.RootNodeId, "a");

        var response = await _client.SendAsync(
            HttpMethod.Get, $"/api/v1/nodes/{library.RootNodeId}/children?sort=name&descending=true&kind=folder&limit=500", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["b", "a"], (await response.ReadAsync<NodePage>()).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_taken_name_answers_409_with_the_existing_item()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var first = await CreateFolderAsync(token, library.RootNodeId, "Docs");

        var second = await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes", token, new { parentId = library.RootNodeId, name = "docs" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("node.name_conflict", await second.ProblemCodeAsync());
        Assert.Equal(first.Id, (await ProblemParamsAsync(second))["existingId"].GetGuid());
    }

    [Fact]
    public async Task Changing_a_node_requires_the_version_it_was_read_at()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var folder = await CreateFolderAsync(token, library.RootNodeId, "Old");
        var url = $"/api/v1/nodes/{folder.Id}";

        var missing = await _client.SendAsync(HttpMethod.Patch, url, token, new { name = "New" });
        var renamed = await _client.SendAsync(HttpMethod.Patch, url, token, new { name = "New" }, ifMatch: $"\"{folder.Version}\"");
        var stale = await _client.SendAsync(HttpMethod.Patch, url, token, new { name = "Newer" }, ifMatch: $"\"{folder.Version}\"");

        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("New", (await renamed.ReadAsync<NodeInfo>()).Name);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Another_users_files_answer_404_for_every_operation()
    {
        var (anaToken, anaLibrary) = await SignInWithLibraryAsync("ana");
        var (bobToken, bobLibrary) = await SignInWithLibraryAsync("bob");
        var secret = await CreateFolderAsync(anaToken, anaLibrary.RootNodeId, "Secret");

        var responses = new[]
        {
            await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{secret.Id}", bobToken),
            await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{secret.Id}/children", bobToken),
            await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{secret.Id}/path", bobToken),
            await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes", bobToken, new { parentId = secret.Id, name = "x" }),
            await _client.SendAsync(HttpMethod.Patch, $"/api/v1/nodes/{secret.Id}", bobToken, new { name = "x" }, ifMatch: "\"0\""),
            await _client.SendAsync(HttpMethod.Post, $"/api/v1/nodes/{secret.Id}/copy", bobToken, new { parentId = bobLibrary.RootNodeId }),
            await _client.SendAsync(HttpMethod.Delete, $"/api/v1/nodes/{secret.Id}", bobToken),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{secret.Id}", anaToken)).StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_open_a_users_personal_files()
    {
        var (anaToken, anaLibrary) = await SignInWithLibraryAsync("ana");
        await _factory.CreateUserAsync("root", TestApi.Password, Atrium.Core.Identity.UserRole.Admin);
        var adminToken = (await _client.SignInAsync("root")).AccessToken;

        var response = await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{anaLibrary.RootNodeId}/children", adminToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(anaToken);
    }

    [Fact]
    public async Task Trash_restore_and_delete_work_end_to_end()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var folder = await CreateFolderAsync(token, library.RootNodeId, "Old");
        await AddFileAsync("ana", folder.Id, "notes.txt", "hello");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Delete, $"/api/v1/nodes/{folder.Id}", token)).StatusCode);
        var trash = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/trash", token)).ReadAsync<List<TrashItemInfo>>();
        var item = Assert.Single(trash);
        Assert.Equal(5, item.Bytes);
        Assert.Equal(2, item.ItemCount);
        Assert.Equal(library.RootNodeId, Assert.Single(item.Location).Id);

        var restored = await _client.SendAsync(HttpMethod.Post, $"/api/v1/trash/{folder.Id}/restore", token, new { });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Empty(await (await _client.SendAsync(HttpMethod.Get, "/api/v1/trash", token)).ReadAsync<List<TrashItemInfo>>());

        await _client.SendAsync(HttpMethod.Delete, $"/api/v1/nodes/{folder.Id}", token);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Delete, $"/api/v1/trash/{folder.Id}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{folder.Id}", token)).StatusCode);
    }

    [Fact]
    public async Task A_batch_reports_each_item_on_its_own()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var target = await CreateFolderAsync(token, library.RootNodeId, "Target");
        var a = await CreateFolderAsync(token, library.RootNodeId, "a");
        var missing = Guid.NewGuid();

        var response = await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes/batch", token, new
        {
            operation = "move",
            ids = new[] { a.Id, missing, target.Id },
            parentId = target.Id,
        });
        var result = await response.ReadAsync<BatchResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Items[0].Ok);
        Assert.Equal(target.Id, result.Items[0].Node!.ParentId);
        Assert.Equal("resource.not_found", result.Items[1].Code);
        Assert.Equal("node.move_into_itself", result.Items[2].Code);
    }

    [Fact]
    public async Task Versions_are_listed_and_restored_over_http()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var original = await AddFileAsync("ana", library.RootNodeId, "doc.txt", "first");
        await AddFileAsync("ana", library.RootNodeId, "doc.txt", "second", ConflictPolicy.Replace);

        var versions = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{original.Id}/versions", token)).ReadAsync<List<VersionInfo>>();
        var version = Assert.Single(versions);
        var restore = await _client.SendAsync(HttpMethod.Post, $"/api/v1/nodes/{original.Id}/versions/{version.Id}/restore", token);

        Assert.Equal(5, version.Size);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(5, (await restore.ReadAsync<NodeInfo>()).Size);
    }

    [Fact]
    public async Task Usage_reports_what_the_user_is_charged_for()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        await AddFileAsync("ana", library.RootNodeId, "a.txt", "12345");

        var usage = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/me/usage", token)).ReadAsync<UsageSummary>();

        Assert.Equal(5, usage.UsedBytes);
        Assert.Null(usage.QuotaBytes);
    }

    [Fact]
    public async Task A_full_quota_answers_507()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var file = await AddFileAsync("ana", library.RootNodeId, "a.txt", "12345");
        await _factory.WithDbAsync(db => db.Users.Where(u => u.Name == "ana").ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, 6L)));

        var copy = await _client.SendAsync(HttpMethod.Post, $"/api/v1/nodes/{file.Id}/copy", token, new { parentId = library.RootNodeId });

        Assert.Equal(HttpStatusCode.InsufficientStorage, copy.StatusCode);
        Assert.Equal("files.quota_exceeded", await copy.ProblemCodeAsync());
    }
}
