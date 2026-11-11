using System.Net;
using System.Net.Http.Json;
using Atrium.Api.Endpoints;
using Atrium.Api.Setup;
using Atrium.Core.Identity;
using Atrium.Core.Storage;
using Atrium.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class SetupTests : IDisposable
{
    private const string Password = TestApi.Password;
    private const string LocationsUrl = "/api/v1/storage-locations";

    private readonly AtriumFactory _factory = new();
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private string Token => _factory.Services.GetRequiredService<SetupService>().Token;

    private static async Task<HttpResponseMessage> CreateAdminAsync(HttpClient client, string? token, string name = "owner", string password = Password) =>
        await client.PostAsJsonAsync("/api/v1/setup/admin", new { name, password, setupToken = token });

    private async Task<TokenResponse> SetUpAdminAsync(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.Created, (await CreateAdminAsync(client, Token)).StatusCode);
        return await client.SignInAsync("owner");
    }

    private object LocalLocation(string name, string? path = null, bool createDirectory = true) =>
        new { name, kind = "localFileSystem", local = new { path = path ?? Path.Combine(_storageRoot, name), createDirectory } };

    [Fact]
    public async Task A_fresh_server_reports_that_nothing_is_set_up_and_a_token_is_needed()
    {
        using var client = NewClient();

        var state = await client.GetFromJsonAsync<SetupState>("/api/v1/setup/state", AtriumFactory.Json);

        Assert.False(state!.AdminExists);
        Assert.False(state.StorageConfigured);
        Assert.False(state.Completed);
        Assert.True(state.SetupTokenRequired);
    }

    [Fact]
    public async Task The_first_admin_cannot_be_created_without_the_setup_token()
    {
        using var client = NewClient();

        var missing = await CreateAdminAsync(client, token: null);
        var wrong = await CreateAdminAsync(client, token: "not-the-token");

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal("setup.token_required", await missing.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("setup.token_invalid", await wrong.ProblemCodeAsync());
        var state = await client.GetFromJsonAsync<SetupState>("/api/v1/setup/state", AtriumFactory.Json);
        Assert.False(state!.AdminExists);
    }

    [Fact]
    public async Task With_the_token_the_first_admin_is_created_and_can_sign_in()
    {
        using var client = NewClient();

        var created = await CreateAdminAsync(client, Token);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var summary = await created.ReadAsync<UserSummary>();
        Assert.Equal("owner", summary.Name);
        Assert.Equal(UserRole.Admin, summary.Role);
        var tokens = await client.SignInAsync("owner");
        Assert.Equal(UserRole.Admin, tokens.User.Role);
        var state = await client.GetFromJsonAsync<SetupState>("/api/v1/setup/state", AtriumFactory.Json);
        Assert.True(state!.AdminExists);
        Assert.False(state.SetupTokenRequired);
        Assert.False(state.Completed);
    }

    [Fact]
    public async Task Once_an_admin_exists_nobody_can_create_another_through_setup()
    {
        using var client = NewClient();
        await SetUpAdminAsync(client);

        var second = await CreateAdminAsync(client, Token, name: "intruder");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("setup.already_done", await second.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("intruder")).StatusCode);
    }

    [Fact]
    public async Task Two_setup_requests_at_the_same_moment_create_exactly_one_admin()
    {
        using var client = NewClient();

        var responses = await Task.WhenAll(
            CreateAdminAsync(client, Token, name: "first"),
            CreateAdminAsync(client, Token, name: "second"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        await _factory.WithDbAsync(async db => Assert.Equal(1, await db.Users.CountAsync()));
    }

    [Fact]
    public async Task The_first_admin_must_have_a_valid_name_and_password()
    {
        using var client = NewClient();

        var weak = await CreateAdminAsync(client, Token, password: "short");
        var badName = await CreateAdminAsync(client, Token, name: "../admin");

        Assert.Equal("user.weak_password", await weak.ProblemCodeAsync());
        Assert.Equal("user.invalid_name", await badName.ProblemCodeAsync());
        // A failed attempt does not use up the setup.
        Assert.Equal(HttpStatusCode.Created, (await CreateAdminAsync(client, Token)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", null, false)]
    [InlineData("::1", null, false)]
    [InlineData("127.0.0.1", "203.0.113.9", true)]
    [InlineData("192.168.1.20", null, true)]
    [InlineData("172.17.0.1", null, true)]
    [InlineData("203.0.113.9", null, true)]
    public void Only_a_direct_request_from_the_server_itself_skips_the_token(string remote, string? forwardedFor, bool required)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (forwardedFor is not null)
        {
            http.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        Assert.Equal(required, SetupService.TokenRequired(http));
    }

    [Fact]
    public void A_request_with_no_known_address_needs_the_token()
    {
        Assert.True(SetupService.TokenRequired(new DefaultHttpContext()));
    }

    [Fact]
    public async Task Storage_administration_needs_an_admin()
    {
        using var client = NewClient();
        await SetUpAdminAsync(client);
        await _factory.CreateUserAsync("ana", Password);
        var ana = await client.SignInAsync("ana");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Get, LocationsUrl, accessToken: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Get, LocationsUrl, ana.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(HttpMethod.Post, LocationsUrl, ana.AccessToken, LocalLocation("files"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/test", ana.AccessToken, LocalLocation("files"))).StatusCode);
        Assert.False(Directory.Exists(_storageRoot));
    }

    [Fact]
    public async Task Testing_a_location_never_creates_the_directory()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);

        var response = await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/test", admin.AccessToken, LocalLocation("files"));

        var result = await response.ReadAsync<StorageTestResult>();
        Assert.False(result.Ok);
        Assert.Equal("storage.root_missing", result.ErrorCode);
        Assert.False(Directory.Exists(_storageRoot));
    }

    [Fact]
    public async Task Testing_an_existing_directory_reports_free_space_and_leaves_it_empty()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);
        var path = Path.Combine(_storageRoot, "files");
        Directory.CreateDirectory(path);

        var response = await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/test", admin.AccessToken, LocalLocation("files"));

        var result = await response.ReadAsync<StorageTestResult>();
        Assert.True(result.Ok, $"{result.ErrorCode}: {result.Detail}");
        Assert.True(result.FreeBytes > 0);
        Assert.False(result.HoldsAtriumData);
        Assert.Empty(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Saving_a_location_marks_it_with_its_identity_and_encrypts_its_settings()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);
        var path = Path.Combine(_storageRoot, "files");

        var created = await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("files"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.ReadAsync<StorageLocationView>();
        Assert.Equal("files", view.Name);
        Assert.Equal(StorageProviderKind.LocalFileSystem, view.Kind);
        Assert.Equal(StorageLocationState.Online, view.State);
        Assert.Equal(path, view.Target);

        // The directory now says which location it is.
        var identity = await File.ReadAllTextAsync(Path.Combine(path, "atrium", "location.json"));
        Assert.Contains(view.Id.ToString(), identity, StringComparison.Ordinal);

        // The stored settings cannot be read without the server key.
        await _factory.WithDbAsync(async db =>
        {
            var row = await db.StorageLocations.SingleAsync();
            Assert.DoesNotContain("files", System.Text.Encoding.UTF8.GetString(row.EncryptedConfig), StringComparison.Ordinal);
        });

        var list = await (await client.SendAsync(HttpMethod.Get, LocationsUrl, admin.AccessToken)).ReadAsync<List<StorageLocationView>>();
        Assert.Equal(view.Id, Assert.Single(list).Id);
        var state = await client.GetFromJsonAsync<SetupState>("/api/v1/setup/state", AtriumFactory.Json);
        Assert.True(state!.StorageConfigured);
    }

    [Fact]
    public async Task A_missing_directory_is_created_only_when_asked()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);

        var refused = await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("files", createDirectory: false));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("storage.root_missing", await refused.ProblemCodeAsync());
        Assert.False(Directory.Exists(_storageRoot));
    }

    [Fact]
    public async Task A_location_already_used_by_an_atrium_server_is_not_taken_over()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);
        var path = Path.Combine(_storageRoot, "shared");
        await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("first", path));
        var identityBefore = await File.ReadAllTextAsync(Path.Combine(path, "atrium", "location.json"));

        var test = await (await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/test", admin.AccessToken, LocalLocation("second", path)))
            .ReadAsync<StorageTestResult>();
        var second = await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("second", path));

        Assert.True(test.HoldsAtriumData);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("storage.location_in_use", await second.ProblemCodeAsync());
        Assert.Equal(identityBefore, await File.ReadAllTextAsync(Path.Combine(path, "atrium", "location.json")));
    }

    [Fact]
    public async Task Two_locations_cannot_share_a_name()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);
        await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("files"));

        var second = await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken,
            LocalLocation("files", Path.Combine(_storageRoot, "elsewhere")));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("storage.name_taken", await second.ProblemCodeAsync());
        Assert.False(Directory.Exists(Path.Combine(_storageRoot, "elsewhere")));
    }

    [Fact]
    public async Task User_files_cannot_be_stored_in_the_servers_own_config_directory()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);

        foreach (var path in new[] { _factory.ConfigDir, Path.Combine(_factory.ConfigDir, "files"), Path.GetDirectoryName(_factory.ConfigDir)! })
        {
            var response = await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("bad", path));
            Assert.Equal("storage.path_not_allowed", await response.ProblemCodeAsync());
        }

        Assert.False(Directory.Exists(Path.Combine(_factory.ConfigDir, "files")));
    }

    [Theory]
    [InlineData("""{"name":"x","kind":"localFileSystem","local":{"path":"relative/path","createDirectory":true}}""")]
    [InlineData("""{"name":"x","kind":"localFileSystem"}""")]
    [InlineData("""{"name":"x","kind":"s3","s3":{"bucket":"","accessKey":"a","secretKey":"b"}}""")]
    [InlineData("""{"name":"x","kind":"s3","s3":{"bucket":"b","accessKey":"a","secretKey":"s","serviceUrl":"ftp://example"}}""")]
    [InlineData("""{"name":"x","kind":"s3","s3":{"bucket":"b","accessKey":"a","secretKey":"s","keyPrefix":"../up"}}""")]
    [InlineData("""{"name":"","kind":"localFileSystem","local":{"path":"/tmp/atrium-x","createDirectory":false}}""")]
    public async Task Malformed_location_settings_are_rejected(string json)
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, LocationsUrl)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.AccessToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("storage.invalid_config", await response.ProblemCodeAsync());
    }

    private async Task<(TokenResponse Admin, StorageLocationView Location, string Path)> WithLocationAsync(HttpClient client)
    {
        var admin = await SetUpAdminAsync(client);
        var created = await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("files"));
        return (admin, await created.ReadAsync<StorageLocationView>(), Path.Combine(_storageRoot, "files"));
    }

    [Fact]
    public async Task A_healthy_location_checks_out_as_online()
    {
        using var client = NewClient();
        var (admin, location, _) = await WithLocationAsync(client);

        var result = await (await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/{location.Id}/check", admin.AccessToken))
            .ReadAsync<StorageTestResult>();

        Assert.True(result.Ok, $"{result.ErrorCode}: {result.Detail}");
        Assert.True(result.FreeBytes > 0);
    }

    [Fact]
    public async Task An_empty_directory_where_the_location_should_be_is_not_the_location()
    {
        using var client = NewClient();
        var (admin, location, path) = await WithLocationAsync(client);

        // What an unmounted share looks like: the directory exists and is
        // writable, but nothing of the location is in it.
        Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);

        var result = await (await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/{location.Id}/check", admin.AccessToken))
            .ReadAsync<StorageTestResult>();

        Assert.False(result.Ok);
        Assert.Equal("storage.identity_missing", result.ErrorCode);
        // Nothing was written into the wrong place while finding that out.
        Assert.Empty(Directory.GetFileSystemEntries(path));
        var list = await (await client.SendAsync(HttpMethod.Get, LocationsUrl, admin.AccessToken)).ReadAsync<List<StorageLocationView>>();
        Assert.Equal(StorageLocationState.Offline, Assert.Single(list).State);
    }

    [Fact]
    public async Task Another_locations_directory_in_its_place_is_detected()
    {
        using var client = NewClient();
        var (admin, location, path) = await WithLocationAsync(client);
        var foreign = new LocationIdentity(Guid.NewGuid(), DateTimeOffset.UtcNow, LocationIdentity.CurrentFormatVersion);
        await File.WriteAllTextAsync(
            Path.Combine(path, "atrium", "location.json"),
            System.Text.Json.JsonSerializer.Serialize(foreign, AtriumFactory.Json));

        var result = await (await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/{location.Id}/check", admin.AccessToken))
            .ReadAsync<StorageTestResult>();

        Assert.False(result.Ok);
        Assert.Equal("storage.identity_mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task A_location_whose_directory_is_gone_goes_offline_and_comes_back()
    {
        using var client = NewClient();
        var (admin, location, path) = await WithLocationAsync(client);
        var parked = path + "-unmounted";
        Directory.Move(path, parked);

        var gone = await (await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/{location.Id}/check", admin.AccessToken))
            .ReadAsync<StorageTestResult>();

        Assert.False(gone.Ok);
        Assert.Equal("storage.root_missing", gone.ErrorCode);
        Assert.False(Directory.Exists(path));

        Directory.Move(parked, path);
        var back = await (await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/{location.Id}/check", admin.AccessToken))
            .ReadAsync<StorageTestResult>();
        Assert.True(back.Ok);
        var list = await (await client.SendAsync(HttpMethod.Get, LocationsUrl, admin.AccessToken)).ReadAsync<List<StorageLocationView>>();
        Assert.Equal(StorageLocationState.Online, Assert.Single(list).State);
    }

    [Fact]
    public async Task Checking_an_unknown_location_is_not_found()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);

        var response = await client.SendAsync(HttpMethod.Post, $"{LocationsUrl}/{Guid.NewGuid()}/check", admin.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Setup_completes_only_with_storage_and_then_the_server_says_so()
    {
        using var client = NewClient();
        var admin = await SetUpAdminAsync(client);

        var early = await client.SendAsync(HttpMethod.Post, "/api/v1/setup/complete", admin.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("setup.storage_missing", await early.ProblemCodeAsync());

        await client.SendAsync(HttpMethod.Post, LocationsUrl, admin.AccessToken, LocalLocation("files"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Post, "/api/v1/setup/complete", accessToken: null)).StatusCode);
        var done = await client.SendAsync(HttpMethod.Post, "/api/v1/setup/complete", admin.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        var info = await client.GetFromJsonAsync<SystemInfo>("/api/v1/system/info", AtriumFactory.Json);
        Assert.True(info!.SetupComplete);
        var state = await client.GetFromJsonAsync<SetupState>("/api/v1/setup/state", AtriumFactory.Json);
        Assert.True(state!.Completed);
        // Completing twice is harmless.
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/v1/setup/complete", admin.AccessToken)).StatusCode);
    }
}
