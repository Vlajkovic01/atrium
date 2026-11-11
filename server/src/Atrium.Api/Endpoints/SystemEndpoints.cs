using System.Reflection;

namespace Atrium.Api.Endpoints;

/// <summary>
/// What a client needs to know before signing in: which server this is and
/// whether the client is new enough to talk to it (DESIGN.md 8.3).
/// </summary>
public sealed record SystemInfo(
    string Name,
    string Version,
    string MinClientVersion,
    bool SetupComplete);

public sealed record HealthStatus(string Status);

public static class SystemEndpoints
{
    /// <summary>Oldest client version this server still supports.</summary>
    public const string MinClientVersion = "0.1.0";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/system/info", () => new SystemInfo(
                Name: "Atrium",
                Version: ServerVersion,
                MinClientVersion: MinClientVersion,
                // Setup state arrives with the setup wizard; until then no admin can exist.
                SetupComplete: false))
            .WithName("GetSystemInfo")
            .AllowAnonymous();

        // Liveness only says the process answers. It must not touch the
        // database or storage, so a slow disk never gets the container killed.
        routes.MapGet("/health/live", () => new HealthStatus("ok"))
            .WithName("GetHealthLive")
            .AllowAnonymous();

        return routes;
    }

    private static string ServerVersion { get; } =
        typeof(SystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";
}
