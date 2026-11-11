using Atrium.Api.Auth;
using Atrium.Core.Identity;
using Atrium.Identity.Sessions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

public sealed record SessionInfo(
    Guid Id,
    string DeviceName,
    DeviceKind DeviceKind,
    string? Platform,
    string? AppVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string? LastIp,
    bool ReadOnly,
    bool Current);

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/sessions").WithTags("Sessions");

        group.MapGet("/", async (HttpContext http, SessionService sessions, CancellationToken ct) =>
            {
                var current = http.User.SessionId();
                var list = await sessions.ListAsync(http.User.UserId()!.Value, ct);
                return list.Select(s => new SessionInfo(
                    s.Id, s.DeviceName, s.DeviceKind, s.Platform, s.AppVersion,
                    s.CreatedAt, s.LastSeenAt, s.LastIp, s.ReadOnly, s.Id == current));
            })
            .WithName("ListSessions");

        // Only the caller's own sessions can be revoked here. Someone else's
        // session answers "not found", the same as one that does not exist.
        group.MapDelete("/{id:guid}", async Task<Results<NoContent, ProblemHttpResult>> (
                Guid id, HttpContext http, SessionService sessions, CancellationToken ct) =>
                await sessions.RevokeAsync(id, http.User.UserId()!.Value, ct)
                    ? TypedResults.NoContent()
                    : ApiProblem.Missing())
            .WithName("RevokeSession")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }
}
