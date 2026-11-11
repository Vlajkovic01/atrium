using System.Reflection;
using Atrium.Core.Identity;
using Atrium.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
        routes.MapGet("/system/info", async (AtriumDbContext db, CancellationToken ct) => new SystemInfo(
                Name: "Atrium",
                Version: ServerVersion,
                MinClientVersion: MinClientVersion,
                // Setup is complete once an admin exists; the wizard creates the first one.
                SetupComplete: await db.Users.AnyAsync(u => u.Role == UserRole.Admin, ct)))
            .WithName("GetSystemInfo")
            .AllowAnonymous();

        // Liveness only says the process answers. It must not touch the
        // database or storage, so a slow disk never gets the container killed.
        routes.MapGet("/health/live", () => new HealthStatus("ok"))
            .WithName("GetHealthLive")
            .AllowAnonymous();

        // Readiness says the server can do useful work. It is public, so it
        // reports no detail about what is wrong.
        routes.MapGet("/health/ready", async Task<Results<Ok<HealthStatus>, JsonHttpResult<HealthStatus>>> (
                AtriumDbContext db, CancellationToken ct) =>
                await db.Database.CanConnectAsync(ct)
                    ? TypedResults.Ok(new HealthStatus("ok"))
                    : TypedResults.Json(new HealthStatus("unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable))
            .WithName("GetHealthReady")
            .AllowAnonymous();

        return routes;
    }

    private static string ServerVersion { get; } =
        typeof(SystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";
}
