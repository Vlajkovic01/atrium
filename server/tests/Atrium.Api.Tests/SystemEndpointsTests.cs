using System.Net;
using System.Net.Http.Json;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Atrium.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class SystemEndpointsTests : IDisposable
{
    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task System_info_is_public_and_reports_version_and_setup_state()
    {
        using var client = _factory.CreateClient();

        var info = await client.GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.NotNull(info);
        Assert.Equal("Atrium", info.Name);
        Assert.True(Version.TryParse(info.Version, out _), $"'{info.Version}' is not a version");
        Assert.Equal(SystemEndpoints.MinClientVersion, info.MinClientVersion);
        Assert.False(info.SetupComplete);
    }

    [Fact]
    public async Task Setup_is_complete_once_an_admin_exists()
    {
        using var client = _factory.CreateClient();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
            db.Users.Add(new User
            {
                Name = "Ana",
                NameKey = "ana",
                DisplayName = "Ana",
                PasswordHash = "not-a-real-hash",
                Role = UserRole.Admin,
                CreatedAt = DateTimeOffset.UtcNow,
                PasswordChangedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var info = await client.GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.True(info?.SetupComplete);
    }

    [Fact]
    public async Task A_non_admin_user_does_not_complete_setup()
    {
        using var client = _factory.CreateClient();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
            db.Users.Add(new User
            {
                Name = "Guest",
                NameKey = "guest",
                DisplayName = "Guest",
                PasswordHash = "not-a-real-hash",
                Role = UserRole.Guest,
                CreatedAt = DateTimeOffset.UtcNow,
                PasswordChangedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var info = await client.GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.False(info?.SetupComplete);
    }

    [Fact]
    public async Task Liveness_answers_without_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthStatus>();
        Assert.Equal("ok", body?.Status);
    }

    [Fact]
    public async Task Readiness_is_ok_when_the_database_answers()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Startup_creates_the_database_inside_the_config_directory()
    {
        using var client = _factory.CreateClient();
        await client.GetAsync("/api/v1/health/live");

        Assert.True(File.Exists(Path.Combine(_factory.ConfigDir, "atrium.db")));
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_describes_v1()
    {
        using var client = _factory.CreateClient();

        var document = await client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("/api/v1/system/info", document, StringComparison.Ordinal);
    }
}
