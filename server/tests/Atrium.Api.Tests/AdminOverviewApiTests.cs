using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Atrium.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Tests;

public sealed class AdminOverviewApiTests : IDisposable
{
    private const string Url = "/api/v1/admin/overview";
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public AdminOverviewApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_overview_counts_people_and_storage_and_says_what_needs_attention()
    {
        await _factory.AddLocalStorageAsync();
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        await _factory.CreateUserAsync("ana", TestApi.Password, UserRole.User);
        await _factory.CreateUserAsync("bob", TestApi.Password, UserRole.User);
        await _factory.WithDbAsync(async db =>
        {
            var bob = await db.Users.SingleAsync(u => u.Name == "bob");
            bob.Disabled = true;
            bob.DeletionScheduledAt = DateTimeOffset.UtcNow;
            bob.DeleteAfter = DateTimeOffset.UtcNow.AddDays(30);
            var location = await db.StorageLocations.SingleAsync();
            location.State = StorageLocationState.Offline;
            await db.SaveChangesAsync();
        });
        var admin = (await _client.SignInAsync("root")).AccessToken;
        var ana = (await _client.SignInAsync("ana")).AccessToken;

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Get, Url, ana)).StatusCode);

        var overview = await (await _client.SendAsync(HttpMethod.Get, Url, admin)).ReadAsync<AdminOverview>();
        Assert.Equal(new OverviewPeople(3, 1, 1, 1), overview.People);
        Assert.Equal(StorageLocationState.Offline, Assert.Single(overview.Storage).State);

        // Warnings come first; each names the section that deals with it.
        var codes = overview.Attention.Select(a => a.Code).ToList();
        Assert.Contains("storage.offline", codes);
        Assert.Contains("people.deletion", codes);
        Assert.Equal("storage", overview.Attention.Single(a => a.Code == "storage.offline").Section);
        Assert.True(codes.IndexOf("storage.offline") < codes.IndexOf("people.deletion"));
    }
}
