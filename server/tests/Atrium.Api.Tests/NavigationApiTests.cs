using System.Net;
using System.Text;
using Atrium.Api.Endpoints;
using Atrium.Api.Preferences;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Files;
using Atrium.Files.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class NavigationApiTests : IDisposable
{
    private const string Nav = "/api/v1/me/navigation";
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public NavigationApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string> PersonAsync(string name, UserRole role = UserRole.User)
    {
        if (!await _factory.WithDbResultAsync(db => db.StorageLocations.AnyAsync()))
        {
            await _factory.AddLocalStorageAsync();
        }

        await _factory.CreateUserAsync(name, TestApi.Password, role);
        var token = (await _client.SignInAsync(name)).AccessToken;
        await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token);
        return token;
    }

    private async Task AddFileAsync(string user, string name, string mediaType, int bytes)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Atrium.Data.AtriumDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Name == user);
        var library = await db.Libraries.SingleAsync(l => l.OwnerUserId == owner.Id);
        var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>()
            .StoreAsync(library.StorageLocationId!.Value, new MemoryStream(Encoding.UTF8.GetBytes(name + new string('x', bytes))), null, CancellationToken.None);
        var added = await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
            new FileActor(owner.Id, false, false, false), library.RootNodeId, name, blob, mediaType, null, ConflictPolicy.Fail, CancellationToken.None);
        Assert.True(added.Ok, added.Error);
    }

    private async Task<NavigationView> NavigationAsync(string token) =>
        await (await _client.SendAsync(HttpMethod.Get, Nav, token)).ReadAsync<NavigationView>();

    [Fact]
    public async Task The_default_menu_lists_the_usual_entries_and_a_new_view_after_photos()
    {
        var token = await PersonAsync("ana");

        Assert.Equal(["files", "photos", "trash", "devices", "account"], (await NavigationAsync(token)).Items.Select(i => i.Key));

        var view = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/me/views", token, new
        {
            name = "Invoices",
            rules = new { words = "invoice" },
        })).ReadAsync<SavedView>();

        var keys = (await NavigationAsync(token)).Items.Select(i => i.Key).ToList();
        Assert.Equal(["files", "photos", $"view:{view.Id:N}", "trash", "devices", "account"], keys);
    }

    [Fact]
    public async Task A_person_arranges_hides_renames_and_groups_entries_and_can_go_back()
    {
        var token = await PersonAsync("ana");

        var saved = await _client.SendAsync(HttpMethod.Put, Nav, token, new
        {
            entries = new object[]
            {
                new { key = "trash", hidden = false, label = (string?)null, group = (string?)null },
                new { key = "files", hidden = false, label = "Documents", group = "work" },
                new { key = "photos", hidden = true, label = (string?)null, group = (string?)null },
            },
            groups = new[] { new { id = "work", name = "Work" } },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var menu = await saved.ReadAsync<NavigationView>();

        Assert.True(menu.Custom);
        // Entries not in the layout keep their usual neighbour: devices and account follow the trash.
        Assert.Equal(["trash", "devices", "account", "files", "photos"], menu.Items.Select(i => i.Key));
        Assert.Equal("Documents", menu.Items[3].Label);
        Assert.Equal("work", menu.Items[3].Group);
        Assert.True(menu.Items[4].Hidden);

        var bad = await _client.SendAsync(HttpMethod.Put, Nav, token, new
        {
            entries = new[] { new { key = "../admin", hidden = false, label = (string?)null, group = (string?)null } },
            groups = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var reset = await (await _client.SendAsync(HttpMethod.Delete, Nav, token)).ReadAsync<NavigationView>();
        Assert.False(reset.Custom);
        Assert.Equal("files", reset.Items[0].Key);
    }

    [Fact]
    public async Task The_admins_default_applies_to_everyone_who_has_not_arranged_their_own()
    {
        var admin = await PersonAsync("root", UserRole.Admin);
        var ana = await PersonAsync("ana");

        var put = await _client.SendAsync(HttpMethod.Put, "/api/v1/admin/navigation-default", admin, new
        {
            entries = new[] { new { key = "photos", hidden = false, label = (string?)null, group = (string?)null } },
            groups = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Get, "/api/v1/admin/navigation-default", ana)).StatusCode);

        Assert.Equal("photos", (await NavigationAsync(ana)).Items[0].Key);
    }

    [Fact]
    public async Task Folders_and_albums_are_pinned_with_icons_separators_and_menu_look_and_only_while_reachable()
    {
        var ana = await PersonAsync("ana");
        var bob = await PersonAsync("bob");
        var anaRoot = await _factory.WithDbResultAsync(db => db.Libraries.Where(l => db.Users.Any(u => u.Id == l.OwnerUserId && u.Name == "ana")).Select(l => l.RootNodeId).SingleAsync());
        var album = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/albums", ana, new { name = "Summer" })).ReadAsync<AlbumView>();
        var separator = $"separator:{Guid.NewGuid():N}";

        var saved = await _client.SendAsync(HttpMethod.Put, Nav, ana, new
        {
            entries = new object[]
            {
                new { key = $"folder:{anaRoot:N}", hidden = false, label = (string?)null, group = "g", icon = "star" },
                new { key = separator, hidden = false, label = (string?)null, group = (string?)null, icon = (string?)null },
                new { key = $"album:{album.Id:N}", hidden = false, label = "Holidays", group = (string?)null, icon = (string?)null },
            },
            groups = new[] { new { id = "g", name = "Pinned", collapsed = true } },
            density = "compact",
            adminPosition = "top",
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var menu = await saved.ReadAsync<NavigationView>();

        Assert.Equal("compact", menu.Density);
        Assert.Equal("top", menu.AdminPosition);
        Assert.True(Assert.Single(menu.Groups).Collapsed);
        var folder = menu.Items[0];
        Assert.Equal(("folder", anaRoot, "star", "g"), (folder.Kind, folder.TargetId!.Value, folder.Icon, folder.Group));
        Assert.Equal("separator", menu.Items[1].Kind);
        Assert.Equal(("album", album.Id, "Holidays"), (menu.Items[2].Kind, menu.Items[2].TargetId!.Value, menu.Items[2].Label));

        // Pins to what someone cannot open are left out, never shown broken.
        var foreign = await (await _client.SendAsync(HttpMethod.Put, Nav, bob, new
        {
            entries = new object[]
            {
                new { key = $"folder:{anaRoot:N}", hidden = false, label = (string?)null, group = (string?)null },
                new { key = $"album:{album.Id:N}", hidden = false, label = (string?)null, group = (string?)null },
            },
            groups = Array.Empty<object>(),
        })).ReadAsync<NavigationView>();
        Assert.DoesNotContain(foreign.Items, i => i.Kind is "folder" or "album");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Delete, $"/api/v1/albums/{album.Id}", ana)).StatusCode);
        Assert.DoesNotContain((await NavigationAsync(ana)).Items, i => i.Kind == "album");

        foreach (var invalid in new object[]
        {
            new { entries = new[] { new { key = "files", hidden = false, label = (string?)null, group = (string?)null, icon = "<svg>" } }, groups = Array.Empty<object>() },
            new { entries = Array.Empty<object>(), groups = Array.Empty<object>(), density = "huge" },
            new { entries = Array.Empty<object>(), groups = Array.Empty<object>(), adminPosition = "left" },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(HttpMethod.Put, Nav, ana, invalid)).StatusCode);
        }
    }

    [Fact]
    public async Task A_view_shows_files_by_its_rules_and_only_to_its_owner()
    {
        var ana = await PersonAsync("ana");
        var bob = await PersonAsync("bob");
        await AddFileAsync("ana", "invoice-1.pdf", "application/pdf", 10);
        await AddFileAsync("ana", "invoice-2.pdf", "application/pdf", 5000);
        await AddFileAsync("ana", "invoice.xlsx", "application/octet-stream", 10);
        await AddFileAsync("ana", "beach.jpg", "image/jpeg", 10);

        var preview = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/me/views/preview", ana, new
        {
            rules = new { extensions = new[] { "PDF" }, minSize = 1000 },
        })).ReadAsync<ViewPageView>();
        Assert.Equal(1, preview.Total);
        Assert.Equal("invoice-2.pdf", Assert.Single(preview.Items).Node.Name);

        var view = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/me/views", ana, new
        {
            name = "Invoices",
            rules = new { words = "invoice", types = new[] { "document" }, sort = "name" },
        })).ReadAsync<SavedView>();
        var items = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/me/views/{view.Id}/items", ana)).ReadAsync<ViewPageView>();
        Assert.Equal(["invoice-1.pdf", "invoice-2.pdf", "invoice.xlsx"], items.Items.Select(i => i.Node.Name));

        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(HttpMethod.Get, $"/api/v1/me/views/{view.Id}/items", bob)).StatusCode);
        var invalid = await _client.SendAsync(HttpMethod.Post, "/api/v1/me/views", ana, new { name = "x", rules = new { extensions = new[] { "../*" } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(HttpMethod.Delete, $"/api/v1/me/views/{view.Id}", ana)).StatusCode);
    }
}
