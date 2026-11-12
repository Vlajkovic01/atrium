using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Core.Identity;

namespace Atrium.Api.Tests;

public sealed class LibraryApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<List<LibraryInfo>> ListAsync(string user)
    {
        var client = _factory.CreateClient();
        var token = (await client.SignInAsync(user)).AccessToken;
        var response = await client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<List<LibraryInfo>>();
    }

    [Fact]
    public async Task A_user_gets_a_personal_library_on_first_request_and_the_same_one_after()
    {
        await _factory.AddLocalStorageAsync();
        await _factory.CreateUserAsync("ana", TestApi.Password);

        var first = Assert.Single(await ListAsync("ana"));
        var second = Assert.Single(await ListAsync("ana"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(LibraryKind.Personal, first.Kind);
        Assert.Equal(LibraryMode.Managed, first.Mode);
        Assert.False(first.ReadOnly);
        Assert.NotEqual(Guid.Empty, first.RootNodeId);
    }

    [Fact]
    public async Task Each_user_has_their_own_library()
    {
        await _factory.AddLocalStorageAsync();
        await _factory.CreateUserAsync("ana", TestApi.Password);
        await _factory.CreateUserAsync("bob", TestApi.Password);

        var ana = Assert.Single(await ListAsync("ana"));
        var bob = Assert.Single(await ListAsync("bob"));

        Assert.NotEqual(ana.Id, bob.Id);
    }

    [Fact]
    public async Task Without_storage_or_as_a_guest_there_is_no_personal_library()
    {
        await _factory.CreateUserAsync("ana", TestApi.Password);
        await _factory.CreateUserAsync("guest", TestApi.Password, UserRole.Guest);

        Assert.Empty(await ListAsync("ana"));
        await _factory.AddLocalStorageAsync();
        Assert.Empty(await ListAsync("guest"));
        Assert.Single(await ListAsync("ana"));
    }
}
