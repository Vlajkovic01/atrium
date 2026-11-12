using Atrium.Api.Auth;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Files;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Name">Clients show a translated name for a personal library, chosen by <paramref name="Kind"/>.</param>
public sealed record LibraryInfo(
    Guid Id,
    string Name,
    LibraryKind Kind,
    LibraryMode Mode,
    Guid RootNodeId,
    bool ReadOnly);

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder routes)
    {
        var libraries = routes.MapGroup("/libraries").WithTags("Libraries");

        libraries.MapGet("/", ListAsync).WithName("ListLibraries");

        return routes;
    }

    /// <summary>
    /// The libraries the caller can open. The personal library is created on
    /// first use. A device approved with "hide my personal files" does not
    /// see it, and a read-only device sees every library as read-only.
    /// </summary>
    private static async Task<Results<Ok<List<LibraryInfo>>, ProblemHttpResult>> ListAsync(
        HttpContext http, AtriumDbContext db, LibraryService libraries, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == http.User.UserId(), ct);
        if (user is null)
        {
            return ApiProblem.Missing();
        }

        var readOnlySession = http.User.HasClaim(c => c.Type == AccessTokens.ReadOnlyClaim);
        var hidePersonal = http.User.HasClaim(c => c.Type == AccessTokens.HidePersonalClaim);
        var result = new List<LibraryInfo>();
        if (!hidePersonal && await libraries.EnsurePersonalAsync(user, ct) is { } personal)
        {
            result.Add(new LibraryInfo(
                personal.Id, personal.Name, personal.Kind, personal.Mode, personal.RootNodeId, personal.ReadOnly || readOnlySession));
        }

        return TypedResults.Ok(result);
    }
}
