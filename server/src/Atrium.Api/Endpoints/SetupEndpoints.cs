using Atrium.Api.Auth;
using Atrium.Api.Setup;
using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="SetupTokenRequired">Whether this client must send the setup token to create the first admin.</param>
public sealed record SetupState(bool AdminExists, bool StorageConfigured, bool Completed, bool SetupTokenRequired);

public sealed record CreateFirstAdminRequest(string Name, string? DisplayName, string Password, string? Locale, string? SetupToken);

public static class SetupEndpoints
{
    public const string AlreadyDone = "setup.already_done";
    public const string TokenRequired = "setup.token_required";
    public const string TokenInvalid = "setup.token_invalid";
    public const string StorageMissing = "setup.storage_missing";

    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder routes)
    {
        var setup = routes.MapGroup("/setup").WithTags("Setup");

        // Public, and tells only what the wizard needs to pick its step.
        setup.MapGet("/state", async (HttpContext http, AtriumDbContext db, CancellationToken ct) =>
            {
                var adminExists = await SetupService.AdminExistsAsync(db, ct);
                return new SetupState(
                    adminExists,
                    await db.StorageLocations.AnyAsync(ct),
                    await SetupService.IsCompletedAsync(db, ct),
                    !adminExists && SetupService.TokenRequired(http));
            })
            .WithName("GetSetupState")
            .AllowAnonymous();

        setup.MapPost("/admin", CreateAdminAsync)
            .WithName("CreateFirstAdmin")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // From here on the wizard is signed in as the admin it just created,
        // and uses the same endpoints the admin interface uses later.
        setup.MapPost("/complete", CompleteAsync)
            .WithName("CompleteSetup")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<Results<Created<UserSummary>, ProblemHttpResult>> CreateAdminAsync(
        CreateFirstAdminRequest request,
        HttpContext http,
        AtriumDbContext db,
        UserService users,
        SetupService setup,
        CancellationToken ct)
    {
        if (request.Name is null || request.Password is null)
        {
            return ApiProblem.Invalid();
        }

        var result = await setup.CreateFirstAdminAsync(
            db,
            users,
            http,
            new NewUser(request.Name, request.DisplayName, request.Password, UserRole.Admin, QuotaBytes: null, request.Locale),
            request.SetupToken,
            ct);

        if (result.SetupError is { } setupError)
        {
            return setupError switch
            {
                SetupAdminError.AlreadyDone => ApiProblem.Create(StatusCodes.Status409Conflict, AlreadyDone),
                SetupAdminError.TokenRequired => ApiProblem.Create(StatusCodes.Status403Forbidden, TokenRequired),
                _ => ApiProblem.Create(StatusCodes.Status403Forbidden, TokenInvalid),
            };
        }

        if (result.UserError is { } userError)
        {
            return ApiProblem.From(userError);
        }

        var user = result.User!;
        return TypedResults.Created(
            "/api/v1/me", new UserSummary(user.Id, user.Name, user.DisplayName, user.Role, user.Locale));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> CompleteAsync(
        AtriumDbContext db, SetupService setup, CancellationToken ct)
    {
        // A server with nowhere to put files is not set up.
        if (!await db.StorageLocations.AnyAsync(ct))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, StorageMissing);
        }

        await setup.MarkCompletedAsync(db, ct);
        return TypedResults.NoContent();
    }
}
