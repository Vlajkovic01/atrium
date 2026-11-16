using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Atrium.Data.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class NotificationApiTests : IDisposable
{
    private const string Url = "/api/v1/me/notifications";
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public NotificationApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<NotificationList> ListAsync(string token) =>
        await (await _client.SendAsync(HttpMethod.Get, Url, token)).ReadAsync<NotificationList>();

    [Fact]
    public async Task A_client_reports_its_uploads_and_the_list_is_read_marked_and_cleared_per_person()
    {
        await _factory.CreateUserAsync("ana", TestApi.Password);
        await _factory.CreateUserAsync("bob", TestApi.Password);
        var ana = (await _client.SignInAsync("ana")).AccessToken;
        var bob = (await _client.SignInAsync("bob")).AccessToken;

        var added = await _client.SendAsync(HttpMethod.Post, Url, ana, new
        {
            kind = "uploads.finished",
            @params = new Dictionary<string, string> { ["count"] = "3" },
            link = "/files/0123456789abcdef0123456789abcdef",
        });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        await _client.SendAsync(HttpMethod.Post, Url, ana, new { kind = "uploads.failed", @params = new Dictionary<string, string> { ["count"] = "1" } });

        var list = await ListAsync(ana);
        Assert.Equal(2, list.Unread);
        Assert.Equal(["uploads.failed", "uploads.finished"], list.Items.Select(n => n.Kind));
        Assert.Equal("3", list.Items[1].Params["count"]);
        Assert.Empty((await ListAsync(bob)).Items);

        // A client cannot pretend to be the server, nor link to another site.
        foreach (var bad in new object[]
        {
            new { kind = "job.failed" },
            new { kind = "uploads.finished", link = "https://elsewhere.example/" },
            new { kind = "uploads.finished", link = "//elsewhere.example" },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(HttpMethod.Post, Url, ana, bad)).StatusCode);
        }

        var first = list.Items[0].Id;
        var read = await (await _client.SendAsync(HttpMethod.Post, $"{Url}/read", ana, new { ids = new[] { first } })).ReadAsync<NotificationList>();
        Assert.Equal(1, read.Unread);
        Assert.NotNull(read.Items.Single(n => n.Id == first).ReadAt);

        var all = await (await _client.SendAsync(HttpMethod.Post, $"{Url}/read", ana, new { ids = (Guid[]?)null })).ReadAsync<NotificationList>();
        Assert.Equal(0, all.Unread);

        var removed = await (await _client.SendAsync(HttpMethod.Delete, $"{Url}/{first}", ana)).ReadAsync<NotificationList>();
        Assert.Single(removed.Items);
        Assert.Empty((await (await _client.SendAsync(HttpMethod.Delete, Url, ana)).ReadAsync<NotificationList>()).Items);
    }

    [Fact]
    public async Task Server_notices_for_admins_reach_every_active_admin_only_and_the_list_keeps_the_newest()
    {
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        await _factory.CreateUserAsync("ana", TestApi.Password);
        var root = (await _client.SignInAsync("root")).AccessToken;
        var ana = (await _client.SignInAsync("ana")).AccessToken;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<NotificationStore>();
            await store.AddForAdminsAsync("library.offline", new Dictionary<string, string> { ["name"] = "Photos" }, "/admin/libraries", CancellationToken.None);
            for (var i = 0; i < NotificationStore.Max + 5; i++)
            {
                await store.AddForAdminsAsync("integrity.problems", new Dictionary<string, string> { ["count"] = "1" }, null, CancellationToken.None);
            }
        }

        var mine = await ListAsync(root);
        Assert.Equal(NotificationStore.Max, mine.Items.Count);
        Assert.Equal("integrity.problems", mine.Items[0].Kind);
        Assert.Empty((await ListAsync(ana)).Items);
    }
}
