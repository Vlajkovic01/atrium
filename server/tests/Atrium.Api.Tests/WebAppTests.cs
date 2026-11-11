using System.Net;
using System.Net.Http.Json;
using Atrium.Api.Setup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class WebAppTests : IDisposable
{
    private const string StartPage = "<!doctype html><title>Atrium test page</title>";

    private readonly AtriumFactory _factory = new();
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_webRoot))
        {
            Directory.Delete(_webRoot, recursive: true);
        }
    }

    /// <summary>The server with a stand-in for the built web app.</summary>
    private HttpClient ClientWithWebApp(Action<IWebHostBuilder>? configure = null)
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), StartPage);
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js"), "console.log('atrium');");
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseWebRoot(_webRoot);
            configure?.Invoke(builder);
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/devices")]
    [InlineData("/setup")]
    [InlineData("/pair?code=BCDF-GHJK")]
    [InlineData("/some/deep/link")]
    [InlineData("/apiary")]
    public async Task App_links_open_the_web_app_without_signing_in(string path)
    {
        using var client = ClientWithWebApp();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StartPage, await response.Content.ReadAsStringAsync());
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/api/v2/system/info")]
    [InlineData("/openapi/missing.json")]
    public async Task Unknown_api_paths_are_not_answered_with_a_web_page(string path)
    {
        using var client = ClientWithWebApp();

        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Atrium test page", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_apps_own_files_are_served()
    {
        using var client = ClientWithWebApp();

        var response = await client.GetAsync("/assets/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("atrium", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_outside_the_web_root_cannot_be_fetched()
    {
        using var client = ClientWithWebApp();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_webRoot)!, "secret.txt"), "secret");

        foreach (var path in new[] { "/../secret.txt", "/assets/../../secret.txt", "/%2e%2e/secret.txt", "/assets/..%2f..%2fsecret.txt" })
        {
            var response = await client.GetAsync(path);
            Assert.DoesNotContain("secret", (await response.Content.ReadAsStringAsync()).Replace("Atrium test page", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        File.Delete(Path.Combine(Path.GetDirectoryName(_webRoot)!, "secret.txt"));
    }

    [Fact]
    public async Task Without_a_built_web_app_the_api_still_works()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/devices")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/assets/app.js")]
    [InlineData("/api/v1/system/info")]
    [InlineData("/api/v1/sessions")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        using var client = ClientWithWebApp();

        var response = await client.GetAsync(path);

        var csp = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", string.Join(",", response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", string.Join(",", response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task A_setup_token_chosen_by_the_installer_is_used()
    {
        const string chosen = "a-token-i-picked-myself";
        using var client = ClientWithWebApp(builder => builder.UseSetting(SetupService.TokenVariable, chosen));

        var wrong = await client.PostAsJsonAsync("/api/v1/setup/admin", new { name = "owner", password = TestApi.Password, setupToken = "something-else-entirely" });
        var right = await client.PostAsJsonAsync("/api/v1/setup/admin", new { name = "owner", password = TestApi.Password, setupToken = chosen });

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Created, right.StatusCode);
    }

    [Fact]
    public void A_chosen_setup_token_that_is_too_short_is_ignored()
    {
        using var factory = new AtriumFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting(SetupService.TokenVariable, "short"));

        var token = configured.Services.GetRequiredService<SetupService>().Token;

        Assert.NotEqual("short", token);
        Assert.True(token.Length >= SetupService.MinChosenTokenLength);
    }
}
