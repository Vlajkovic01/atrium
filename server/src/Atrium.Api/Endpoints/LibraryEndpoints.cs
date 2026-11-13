using Atrium.Api.Auth;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Data;
using Atrium.Files;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="Name">Clients show a translated name for a personal library, chosen by <paramref name="Kind"/>.</param>
/// <param name="ReadOnly">Whether the caller cannot change anything here.</param>
/// <param name="ChangesAllowed">Whether the library allows changes at all; when it does but <paramref name="ReadOnly"/> is set, the caller is not among those who may change it.</param>
public sealed record LibraryInfo(
    Guid Id,
    string Name,
    LibraryKind Kind,
    LibraryMode Mode,
    Guid RootNodeId,
    bool ReadOnly,
    LibraryState State,
    bool ChangesAllowed);

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
                personal.Id, personal.Name, personal.Kind, personal.Mode, personal.RootNodeId, personal.ReadOnly || readOnlySession, personal.State, !personal.ReadOnly));
        }

        // Libraries shared with the user through a grant, such as existing folders on the server.
        var actor = http.User.Actor();
        var granted = await db.LibraryGrants.AsNoTracking()
            .Where(g => g.UserId == user.Id)
            .Join(db.Libraries.AsNoTracking(), g => g.LibraryId, l => l.Id, (g, l) => new { Grant = g, Library = l })
            .OrderBy(x => x.Library.Name)
            .ToListAsync(ct);
        foreach (var item in granted)
        {
            var library = item.Library;
            var writable = NodeService.CanWrite(actor, library, item.Grant);
            result.Add(new LibraryInfo(
                library.Id, library.Name, library.Kind, library.Mode, library.RootNodeId, !writable, library.State, !library.ReadOnly));
        }

        return TypedResults.Ok(result);
    }
}
