using System.Globalization;

namespace Atrium.Api.Hosting;

/// <summary>
/// <c>Atrium.Api --healthcheck</c>: asks the running server whether it is
/// ready and exits with 0 or 1. This is the container's health check. It is
/// built into the server so the image needs no extra tool such as curl.
/// </summary>
public static class HealthCheckCommand
{
    public const string Argument = "--healthcheck";

    private const int DefaultPort = 8080;

    public static async Task<int> RunAsync(IConfiguration configuration)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(ResolveUrl(configuration));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    /// <summary>The server's own readiness address, from the same settings the server listens on.</summary>
    public static Uri ResolveUrl(IConfiguration configuration)
    {
        var port = DefaultPort;
        if (FirstEntry(configuration["ASPNETCORE_HTTP_PORTS"] ?? configuration["HTTP_PORTS"]) is { } ports
            && int.TryParse(ports, NumberStyles.None, CultureInfo.InvariantCulture, out var fromPorts))
        {
            port = fromPorts;
        }
        else if (FirstEntry(configuration["ASPNETCORE_URLS"] ?? configuration["URLS"]) is { } url
            && Uri.TryCreate(url.Replace("+", "localhost", StringComparison.Ordinal).Replace("*", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var parsed))
        {
            port = parsed.Port;
        }

        // Always loopback: the check runs inside the same container.
        return new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/api/v1/health/ready");
    }

    private static string? FirstEntry(string? value) =>
        value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
