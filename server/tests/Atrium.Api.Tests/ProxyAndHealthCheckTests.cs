using System.Net;
using System.Net.Http.Json;
using Atrium.Api.Endpoints;
using Atrium.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class ProxyAndHealthCheckTests : IDisposable
{
    private const string ProxyAddress = "172.30.0.2";

    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>
    /// The server as it runs behind a proxy: every request arrives from
    /// <paramref name="connectingFrom"/>, and <paramref name="trusted"/> is
    /// what the installer listed as trusted proxies.
    /// </summary>
    private HttpClient Behind(string connectingFrom, string? trusted) =>
        _factory.WithWebHostBuilder(builder =>
        {
            if (trusted is not null)
            {
                builder.UseSetting(BootstrapOptions.TrustedProxiesKey, trusted);
            }

            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse(connectingFrom))));
        }).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<HttpResponseMessage> StartPairingAsync(HttpClient client, string? forwardedProto, string? forwardedFor, string? forwardedHost)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/pairing")
        {
            Content = JsonContent.Create(new { device = new { name = "TV", kind = "tv" } }),
        };
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (forwardedHost is not null)
        {
            request.Headers.Add("X-Forwarded-Host", forwardedHost);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_server_sees_the_real_scheme_host_and_client()
    {
        using var client = Behind(ProxyAddress, trusted: "172.30.0.0/24");

        var response = await StartPairingAsync(client, "https", "203.0.113.9", "cloud.example.com");

        var started = await response.ReadAsync<StartPairingResponse>();
        Assert.StartsWith("https://cloud.example.com/pair?code=", started.VerificationUrl, StringComparison.Ordinal);
        await _factory.WithDbAsync(async db =>
        {
            var request = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.PairingRequests);
            Assert.Equal("203.0.113.9", request.RequestIp);
        });
    }

    [Fact]
    public async Task Behind_a_trusted_https_proxy_the_refresh_cookie_is_marked_secure()
    {
        await _factory.CreateUserAsync("ana", TestApi.Password);
        using var client = Behind(ProxyAddress, trusted: ProxyAddress);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { name = "ana", password = TestApi.Password, device = new { name = "Browser", kind = "web" } }),
        };
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("10.9.9.0/24")]
    public async Task Forwarding_headers_from_anyone_else_are_ignored(string? trusted)
    {
        // The headers arrive from an address that is not a listed proxy:
        // a client trying to pass as someone else, over "HTTPS".
        using var client = Behind("198.51.100.7", trusted);

        var response = await StartPairingAsync(client, "https", "203.0.113.9", "evil.example.com");

        var started = await response.ReadAsync<StartPairingResponse>();
        Assert.StartsWith("http://localhost/pair?code=", started.VerificationUrl, StringComparison.Ordinal);
        await _factory.WithDbAsync(async db =>
        {
            var request = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.PairingRequests);
            Assert.Equal("198.51.100.7", request.RequestIp);
        });
    }

    [Fact]
    public async Task Only_the_nearest_hop_is_believed()
    {
        using var client = Behind(ProxyAddress, trusted: ProxyAddress);

        // A client can put anything in front of what the proxy appends.
        await StartPairingAsync(client, "https", "1.2.3.4, 203.0.113.9", null);

        await _factory.WithDbAsync(async db =>
        {
            var request = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.PairingRequests);
            Assert.Equal("203.0.113.9", request.RequestIp);
        });
    }

    [Theory]
    [InlineData("172.30.0.2", 1)]
    [InlineData("172.30.0.0/24, 10.0.0.1", 2)]
    [InlineData("fd00::/8,::1", 2)]
    [InlineData(" ", 0)]
    public void Trusted_proxies_are_read_as_addresses_or_networks(string value, int expected)
    {
        var options = BootstrapOptions.From(Config((BootstrapOptions.TrustedProxiesKey, value)));

        Assert.Equal(expected, options.TrustedProxies.Count);
    }

    [Fact]
    public void No_proxy_is_trusted_unless_listed()
    {
        Assert.Empty(BootstrapOptions.From(Config()).TrustedProxies);
    }

    [Theory]
    [InlineData("caddy")]
    [InlineData("172.30.0.0/99")]
    [InlineData("172.30.0.2, not-an-address")]
    public void A_mistyped_trusted_proxy_stops_the_server_with_an_explanation(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => BootstrapOptions.From(Config((BootstrapOptions.TrustedProxiesKey, value))));

        Assert.Contains(BootstrapOptions.TrustedProxiesKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, "http://127.0.0.1:8080/api/v1/health/ready")]
    [InlineData("9000", null, "http://127.0.0.1:9000/api/v1/health/ready")]
    [InlineData("9000;9001", null, "http://127.0.0.1:9000/api/v1/health/ready")]
    [InlineData(null, "http://+:5005", "http://127.0.0.1:5005/api/v1/health/ready")]
    [InlineData(null, "http://0.0.0.0:7007;http://[::]:7008", "http://127.0.0.1:7007/api/v1/health/ready")]
    [InlineData("8081", "http://+:5005", "http://127.0.0.1:8081/api/v1/health/ready")]
    public void The_health_check_asks_the_port_the_server_listens_on(string? ports, string? urls, string expected)
    {
        var url = HealthCheckCommand.ResolveUrl(Config(("ASPNETCORE_HTTP_PORTS", ports), ("ASPNETCORE_URLS", urls)));

        Assert.Equal(expected, url.ToString());
    }

    [Fact]
    public async Task The_health_check_fails_when_no_server_answers()
    {
        // Port 1 is never listening.
        Assert.Equal(1, await HealthCheckCommand.RunAsync(Config(("ASPNETCORE_HTTP_PORTS", "1"))));
    }

    /// <summary>Makes every request appear to come from one address, as a real connection would.</summary>
    private sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                context.Connection.RemoteIpAddress = address;
                await following();
            });
            next(app);
        };
    }
}
