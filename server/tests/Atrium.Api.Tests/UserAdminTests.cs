using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Files;
using Atrium.Files.Blobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class UserAdminTests : IDisposable
{
    private const string Password = TestApi.Password;
    private const string UsersUrl = "/api/v1/users";

    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private async Task<TokenResponse> AdminAsync(HttpClient client)
    {
        await _factory.CreateUserAsync("admin", Password, UserRole.Admin);
        return await client.SignInAsync("admin");
    }

    private static object NewUserBody(string name, string role = "user") =>
        new { name, password = Password, role };

    [Fact]
    public async Task Only_admins_can_reach_user_administration()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var ana = await client.SignInAsync("ana");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Get, UsersUrl, accessToken: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Get, UsersUrl, ana.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(HttpMethod.Post, UsersUrl, ana.AccessToken, NewUserBody("eve", "admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(HttpMethod.Post, $"{UsersUrl}/{ana.User.Id}/reset-password", ana.AccessToken,
                new { newPassword = "whatever else" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_creates_a_user_who_can_then_sign_in()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);

        var created = await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken,
            new { name = "Marko", displayName = "Marko M.", password = Password, role = "guest", quotaBytes = 1000 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.ReadAsync<AdminUserView>();
        Assert.Equal("Marko", view.Name);
        Assert.Equal("Marko M.", view.DisplayName);
        Assert.Equal(UserRole.Guest, view.Role);
        Assert.Equal(1000, view.QuotaBytes);
        Assert.DoesNotContain("argon2", await created.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var marko = await client.SignInAsync("marko");
        Assert.Equal(UserRole.Guest, marko.User.Role);
    }

    [Theory]
    [InlineData("ADMIN", "correct horse battery", HttpStatusCode.Conflict, "user.name_taken")]
    [InlineData("has space", "correct horse battery", HttpStatusCode.BadRequest, "user.invalid_name")]
    [InlineData("../etc", "correct horse battery", HttpStatusCode.BadRequest, "user.invalid_name")]
    [InlineData("marko", "short", HttpStatusCode.BadRequest, "user.weak_password")]
    public async Task Invalid_new_users_are_rejected_with_a_reason(string name, string password, HttpStatusCode status, string code)
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);

        var response = await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, new { name, password, role = "user" });

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task The_list_pages_through_all_users_in_name_order()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);
        foreach (var name in new[] { "dana", "boris", "Ceca", "eva" })
        {
            await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, NewUserBody(name));
        }

        var names = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"{UsersUrl}?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await (await client.SendAsync(HttpMethod.Get, url, admin.AccessToken)).ReadAsync<UserPage>();
            Assert.InRange(page.Items.Count, 1, 2);
            names.AddRange(page.Items.Select(u => u.Name));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(["admin", "boris", "Ceca", "dana", "eva"], names);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Editing_a_user_requires_the_version_that_was_read()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);
        var created = await (await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, NewUserBody("marko"))).ReadAsync<AdminUserView>();
        var url = $"{UsersUrl}/{created.Id}";

        var noVersion = await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { displayName = "M" });
        var first = await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { displayName = "First" }, ifMatch: "\"0\"");
        var stale = await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { displayName = "Second" }, ifMatch: "\"0\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, noVersion.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"1\"", first.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("request.version_conflict", await stale.ProblemCodeAsync());
        var current = await client.SendAsync(HttpMethod.Get, url, admin.AccessToken);
        Assert.Equal("First", (await current.ReadAsync<AdminUserView>()).DisplayName);
        Assert.Equal("\"1\"", current.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Disabling_a_user_cuts_them_off_at_once()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);
        var created = await (await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, NewUserBody("marko"))).ReadAsync<AdminUserView>();
        var marko = await client.SignInAsync("marko");

        var disabled = await client.SendAsync(HttpMethod.Patch, $"{UsersUrl}/{created.Id}", admin.AccessToken, new { disabled = true }, ifMatch: "0");

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Get, "/api/v1/me", marko.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("marko")).StatusCode);

        // Enabling again lets them sign in; their old sessions stay dead.
        await client.SendAsync(HttpMethod.Patch, $"{UsersUrl}/{created.Id}", admin.AccessToken, new { disabled = false }, ifMatch: "1");
        await client.SignInAsync("marko");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Get, "/api/v1/me", marko.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Changing_a_role_takes_effect_at_once()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);
        var created = await (await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, NewUserBody("marko", "admin"))).ReadAsync<AdminUserView>();
        var marko = await client.SignInAsync("marko");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Get, UsersUrl, marko.AccessToken)).StatusCode);

        await client.SendAsync(HttpMethod.Patch, $"{UsersUrl}/{created.Id}", admin.AccessToken, new { role = "user" }, ifMatch: "0");

        // The old token still said "admin"; it must not keep working.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Get, UsersUrl, marko.AccessToken)).StatusCode);
        var again = await client.SignInAsync("marko");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Get, UsersUrl, again.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_or_disabled()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);
        var url = $"{UsersUrl}/{admin.User.Id}";

        var demote = await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { role = "user" }, ifMatch: "0");
        var disable = await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { disabled = true }, ifMatch: "0");

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("user.last_admin", await demote.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);

        // A disabled second admin does not count as a way back in.
        var second = await (await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, NewUserBody("second", "admin"))).ReadAsync<AdminUserView>();
        await client.SendAsync(HttpMethod.Patch, $"{UsersUrl}/{second.Id}", admin.AccessToken, new { disabled = true }, ifMatch: "0");
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { role = "user" }, ifMatch: "0")).StatusCode);

        // With another active admin the change is allowed.
        await client.SendAsync(HttpMethod.Patch, $"{UsersUrl}/{second.Id}", admin.AccessToken, new { disabled = false }, ifMatch: "1");
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { role = "user" }, ifMatch: "0")).StatusCode);
    }

    [Fact]
    public async Task A_quota_can_be_set_and_removed()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);
        var created = await (await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, NewUserBody("marko"))).ReadAsync<AdminUserView>();
        var url = $"{UsersUrl}/{created.Id}";

        var set = await (await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { quotaBytes = 5_000_000_000 }, ifMatch: "0")).ReadAsync<AdminUserView>();
        var untouched = await (await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { displayName = "M" }, ifMatch: "1")).ReadAsync<AdminUserView>();
        var negative = await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { quotaBytes = -1 }, ifMatch: "2");
        var cleared = await (await client.SendAsync(HttpMethod.Patch, url, admin.AccessToken, new { clearQuota = true }, ifMatch: "2")).ReadAsync<AdminUserView>();

        Assert.Equal(5_000_000_000, set.QuotaBytes);
        Assert.Equal(5_000_000_000, untouched.QuotaBytes);
        Assert.Equal("user.invalid_quota", await negative.ProblemCodeAsync());
        Assert.Null(cleared.QuotaBytes);
    }

    [Fact]
    public async Task A_password_reset_signs_the_user_out_and_tells_them_afterwards()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);
        var created = await (await client.SendAsync(HttpMethod.Post, UsersUrl, admin.AccessToken, NewUserBody("marko"))).ReadAsync<AdminUserView>();
        var marko = await client.SignInAsync("marko");

        var reset = await client.SendAsync(HttpMethod.Post, $"{UsersUrl}/{created.Id}/reset-password", admin.AccessToken,
            new { newPassword = "temporary passphrase" });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Get, "/api/v1/me", marko.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("marko", Password)).StatusCode);

        var after = await client.SignInAsync("marko", "temporary passphrase");
        var profile = await (await client.SendAsync(HttpMethod.Get, "/api/v1/me", after.AccessToken)).ReadAsync<UserProfile>();
        Assert.True(profile.PasswordResetByAdmin);

        // Choosing their own password clears the notice.
        await client.SendAsync(HttpMethod.Post, "/api/v1/me/password", after.AccessToken,
            new { currentPassword = "temporary passphrase", newPassword = "my own new passphrase" });
        profile = await (await client.SendAsync(HttpMethod.Get, "/api/v1/me", after.AccessToken)).ReadAsync<UserProfile>();
        Assert.False(profile.PasswordResetByAdmin);
    }

    [Fact]
    public async Task Admins_cannot_reset_their_own_password_without_knowing_it()
    {
        using var client = NewClient();
        var admin = await AdminAsync(client);

        var reset = await client.SendAsync(HttpMethod.Post, $"{UsersUrl}/{admin.User.Id}/reset-password", admin.AccessToken,
            new { newPassword = "something else entirely" });
        var missing = await client.SendAsync(HttpMethod.Post, $"{UsersUrl}/{Guid.NewGuid()}/reset-password", admin.AccessToken,
            new { newPassword = "something else entirely" });

        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await client.SignInAsync("admin");
    }

    [Fact]
    public async Task The_list_shows_the_space_each_person_uses_and_when_they_were_last_active()
    {
        await _factory.AddLocalStorageAsync();
        using var client = NewClient();
        var admin = await AdminAsync(client);
        await _factory.CreateUserAsync("ana", Password);
        var ana = await client.SignInAsync("ana");
        await client.SendAsync(HttpMethod.Get, "/api/v1/libraries", ana.AccessToken);
        await _factory.CreateUserAsync("bob", Password);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Atrium.Data.AtriumDbContext>();
            var library = await db.Libraries.SingleAsync(l => l.OwnerUserId == ana.User.Id);
            var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>()
                .StoreAsync(library.StorageLocationId!.Value, new MemoryStream(new byte[1234]), null, CancellationToken.None);
            var added = await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
                new FileActor(ana.User.Id, false, false, false), library.RootNodeId, "a.bin", blob, "application/octet-stream", null,
                ConflictPolicy.Fail, CancellationToken.None);
            Assert.True(added.Ok, added.Error);
        }

        var page = await (await client.SendAsync(HttpMethod.Get, UsersUrl, admin.AccessToken)).ReadAsync<UserPage>();
        var anaView = page.Items.Single(u => u.Name == "ana");
        var bobView = page.Items.Single(u => u.Name == "bob");
        Assert.Equal(1234, anaView.UsedBytes);
        Assert.NotNull(anaView.LastActiveAt);
        Assert.Equal(0, bobView.UsedBytes);
        Assert.Null(bobView.LastActiveAt);
    }
}
