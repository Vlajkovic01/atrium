using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Api.Files;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Tests;

/// <summary>The change feed over HTTP, the way the mobile app follows it.</summary>
public sealed class ChangeApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public ChangeApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<(string Token, LibraryInfo Library)> SignInWithLibraryAsync(string name)
    {
        var hasStorage = false;
        await _factory.WithDbAsync(async db => hasStorage = await db.StorageLocations.AnyAsync());
        if (!hasStorage)
        {
            await _factory.AddLocalStorageAsync();
        }

        await _factory.CreateUserAsync(name, TestApi.Password);
        var token = (await _client.SignInAsync(name)).AccessToken;
        var libraries = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token)).ReadAsync<List<LibraryInfo>>();
        return (token, Assert.Single(libraries));
    }

    private async Task<ChangePage> ChangesAsync(string token, string? cursor)
    {
        var url = cursor is null ? "/api/v1/changes" : $"/api/v1/changes?cursor={Uri.EscapeDataString(cursor)}";
        var response = await _client.SendAsync(HttpMethod.Get, url, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<ChangePage>();
    }

    [Fact]
    public async Task A_client_starts_from_now_and_then_sees_what_changed()
    {
        var (token, library) = await SignInWithLibraryAsync("ana");
        var start = await ChangesAsync(token, null);
        Assert.Empty(start.Items);
        Assert.False(start.HasMore);

        var created = await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes", token, new { parentId = library.RootNodeId, name = "Docs" });
        var folder = await created.ReadAsync<NodeInfo>();
        var afterCreate = await ChangesAsync(token, start.Cursor);

        var item = Assert.Single(afterCreate.Items);
        Assert.Equal(folder.Id, item.NodeId);
        Assert.Equal(library.Id, item.LibraryId);
        Assert.False(item.Removed);
        Assert.Equal("Docs", item.Node!.Name);
        Assert.True(item.Node.CanWrite);

        await _client.SendAsync(HttpMethod.Delete, $"/api/v1/nodes/{folder.Id}", token);
        await _client.SendAsync(HttpMethod.Delete, $"/api/v1/trash/{folder.Id}", token);
        var afterRemove = await ChangesAsync(token, afterCreate.Cursor);

        var removed = Assert.Single(afterRemove.Items);
        Assert.True(removed.Removed);
        Assert.Null(removed.Node);
        Assert.Empty((await ChangesAsync(token, afterRemove.Cursor)).Items);
    }

    [Fact]
    public async Task One_persons_changes_are_not_in_another_persons_feed()
    {
        var (ana, _) = await SignInWithLibraryAsync("ana");
        var (marko, markoLibrary) = await SignInWithLibraryAsync("marko");
        var start = await ChangesAsync(ana, null);

        await _client.SendAsync(HttpMethod.Post, "/api/v1/nodes", marko, new { parentId = markoLibrary.RootNodeId, name = "Private" });

        Assert.Empty((await ChangesAsync(ana, start.Cursor)).Items);
    }

    [Fact]
    public async Task A_cursor_the_server_cannot_continue_from_answers_410_and_a_bad_limit_400()
    {
        var (token, _) = await SignInWithLibraryAsync("ana");

        var expired = await _client.SendAsync(HttpMethod.Get, "/api/v1/changes?cursor=made-up", token);
        var tooMany = await _client.SendAsync(HttpMethod.Get, "/api/v1/changes?limit=5000", token);
        var anonymous = await _client.SendAsync(HttpMethod.Get, "/api/v1/changes", null);

        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal("changes.cursor_expired", await expired.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
