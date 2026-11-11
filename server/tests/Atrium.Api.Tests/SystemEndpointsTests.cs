using System.Net;
using System.Net.Http.Json;
using Atrium.Api.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Atrium.Api.Tests;

public sealed class SystemEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task System_info_is_public_and_reports_version_and_setup_state()
    {
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.NotNull(info);
        Assert.Equal("Atrium", info.Name);
        Assert.True(Version.TryParse(info.Version, out _), $"'{info.Version}' is not a version");
        Assert.Equal(SystemEndpoints.MinClientVersion, info.MinClientVersion);
        Assert.False(info.SetupComplete);
    }

    [Fact]
    public async Task Liveness_answers_without_authentication()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthStatus>();
        Assert.Equal("ok", body?.Status);
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_describes_v1()
    {
        using var client = factory.CreateClient();

        var document = await client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("/api/v1/system/info", document, StringComparison.Ordinal);
    }
}
