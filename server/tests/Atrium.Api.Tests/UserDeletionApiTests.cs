using System.Net;
using System.Text;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

/// <summary>Deleting a user is scheduled, reversible for 30 days, and shows the admin sizes, never names (D-025).</summary>
public sealed class UserDeletionApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public UserDeletionApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<(string AdminToken, User Ana, string AnaToken)> SetUpAsync()
    {
        await _factory.AddLocalStorageAsync();
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        var ana = await _factory.CreateUserAsync("ana", TestApi.Password);
        var adminToken = (await _client.SignInAsync("root")).AccessToken;
        var anaToken = (await _client.SignInAsync("ana")).AccessToken;
        var library = Assert.Single(await (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", anaToken)).ReadAsync<List<LibraryInfo>>());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var locationId = (await db.Libraries.SingleAsync(l => l.Id == library.Id)).StorageLocationId;
        var blob = await scope.ServiceProvider.GetRequiredService<BlobStore>()
            .StoreAsync(locationId, new MemoryStream(Encoding.UTF8.GetBytes("ana's diary")), null, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<NodeService>().AddFileAsync(
            new FileActor(ana.Id, false, false, false), library.RootNodeId, "diary.txt", blob, "text/plain", null, ConflictPolicy.Fail, CancellationToken.None);
        return (adminToken, ana, anaToken);
    }

    [Fact]
    public async Task The_admin_sees_how_much_would_be_removed_but_no_names()
    {
        var (adminToken, ana, _) = await SetUpAsync();

        var response = await _client.SendAsync(HttpMethod.Get, $"/api/v1/users/{ana.Id}/deletion", adminToken);
        var body = await response.Content.ReadAsStringAsync();
        var info = await response.ReadAsync<DeletionInfo>();

        Assert.Equal(1, info.Files);
        Assert.Equal(11, info.Bytes);
        Assert.Null(info.DeletionScheduledAt);
        Assert.DoesNotContain("diary", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deletion_needs_the_name_typed_and_cannot_target_yourself_or_the_last_admin()
    {
        var (adminToken, ana, _) = await SetUpAsync();
        var admin = await _factory.WithDbResultAsync(db => db.Users.SingleAsync(u => u.Name == "root"));

        var wrongName = await _client.SendAsync(HttpMethod.Post, $"/api/v1/users/{ana.Id}/deletion", adminToken, new { confirmName = "bob" });
        var self = await _client.SendAsync(HttpMethod.Post, $"/api/v1/users/{admin.Id}/deletion", adminToken, new { confirmName = "root" });

        Assert.Equal("user.confirmation_mismatch", await wrongName.ProblemCodeAsync());
        Assert.Equal("user.cannot_delete_self", await self.ProblemCodeAsync());
    }

    [Fact]
    public async Task Scheduling_signs_the_user_out_and_keeps_them_from_being_enabled()
    {
        var (adminToken, ana, anaToken) = await SetUpAsync();

        var scheduled = await _client.SendAsync(HttpMethod.Post, $"/api/v1/users/{ana.Id}/deletion", adminToken, new { confirmName = "ANA" });
        var view = await scheduled.ReadAsync<AdminUserView>();

        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);
        Assert.True(view.Disabled);
        Assert.Equal(view.DeletionScheduledAt!.Value.AddDays(30), view.DeleteAfter);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(HttpMethod.Get, "/api/v1/libraries", anaToken)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _client.LoginAsync("ana")).StatusCode);

        var enable = await _client.SendAsync(HttpMethod.Patch, $"/api/v1/users/{ana.Id}", adminToken, new { disabled = false }, ifMatch: $"\"{view.Version}\"");
        Assert.Equal("user.deletion_scheduled", await enable.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_cancelled_deletion_removes_nothing_even_after_the_delay()
    {
        var (adminToken, ana, _) = await SetUpAsync();
        await _client.SendAsync(HttpMethod.Post, $"/api/v1/users/{ana.Id}/deletion", adminToken, new { confirmName = "ana" });

        var cancelled = await _client.SendAsync(HttpMethod.Delete, $"/api/v1/users/{ana.Id}/deletion", adminToken);
        var view = await cancelled.ReadAsync<AdminUserView>();

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Null(view.DeletionScheduledAt);
        Assert.True(view.Disabled);
        _factory.Time.Advance(TimeSpan.FromDays(31));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(await _factory.WithDbResultAsync(db => db.Nodes.AnyAsync(n => n.Name == "diary.txt")));

        // A month later the admin's earlier token has long expired.
        var freshToken = (await _client.SignInAsync("root")).AccessToken;
        var enabled = await _client.SendAsync(HttpMethod.Patch, $"/api/v1/users/{ana.Id}", freshToken, new { disabled = false }, ifMatch: $"\"{view.Version}\"");
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
    }

    [Fact]
    public async Task After_the_delay_the_account_and_its_files_are_removed_and_content_waits_for_the_collector()
    {
        var (adminToken, ana, _) = await SetUpAsync();
        await _client.SendAsync(HttpMethod.Post, $"/api/v1/users/{ana.Id}/deletion", adminToken, new { confirmName = "ana" });

        _factory.Time.Advance(TimeSpan.FromDays(29));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.True(await _factory.WithDbResultAsync(db => db.Users.AnyAsync(u => u.Id == ana.Id)));

        _factory.Time.Advance(TimeSpan.FromDays(2));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await _factory.WithDbResultAsync(db => db.Users.AnyAsync(u => u.Id == ana.Id)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.False(await _factory.WithDbResultAsync(db => db.Users.AnyAsync(u => u.Id == ana.Id)));
        Assert.False(await _factory.WithDbResultAsync(db => db.Nodes.AnyAsync(n => n.Name == "diary.txt")));
        Assert.False(await _factory.WithDbResultAsync(db => db.Libraries.AnyAsync(l => l.OwnerUserId == ana.Id)));

        // The content itself is not deleted here: the collector's grace period still applies.
        Assert.True(await _factory.WithDbResultAsync(db => db.Blobs.AnyAsync()));
    }

    [Fact]
    public async Task An_integrity_check_can_be_started_and_reports_healthy_storage()
    {
        var (adminToken, _, anaToken) = await SetUpAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Post, "/api/v1/admin/integrity/check", anaToken)).StatusCode);

        // The weekly check also runs once when the server starts; let that one finish first.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await _factory.WithDbResultAsync(db => db.Jobs.AnyAsync(j => j.Type == ScrubHandler.Type && j.State == JobState.Running))
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var started = await _client.SendAsync(HttpMethod.Post, "/api/v1/admin/integrity/check", adminToken);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var jobId = (await started.ReadAsync<JobStarted>()).JobId;
        while (await _factory.WithDbResultAsync(db => db.Jobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync()) != JobState.Succeeded
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var status = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/admin/integrity", adminToken)).ReadAsync<IntegrityStatus>();
        Assert.NotNull(status.Report);
        Assert.Equal(1, status.Report.BlobsChecked);
        Assert.Empty(status.Report.Issues);
    }
}
