using Atrium.Api.Auth;
using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Sessions;
using Atrium.Identity.Totp;
using Atrium.Identity.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="PasswordResetByAdmin">
/// True until the user changes their password after an admin reset it.
/// Clients show a notice, because a reset is the one way an admin could
/// have entered the account.
/// </param>
public sealed record UserProfile(
    Guid Id,
    string Name,
    string DisplayName,
    UserRole Role,
    string Locale,
    long? QuotaBytes,
    bool TotpEnabled,
    bool PasswordResetByAdmin);

public sealed record UpdateProfileRequest(string? DisplayName, string? Locale);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TotpEnrollmentResponse(string Secret, string ProvisioningUri);

public sealed record TotpConfirmRequest(string Code);

/// <param name="Codes">Shown once. The server keeps only their hashes.</param>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);

public sealed record TotpDisableRequest(string Password);

public static class MeEndpoints
{
    private const int FreePasswordChecks = 5;

    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder routes)
    {
        var me = routes.MapGroup("/me").WithTags("Me");

        me.MapGet("/", GetAsync).WithName("GetMe").ProducesProblem(StatusCodes.Status404NotFound);

        // Changing the account is refused for restricted sessions such as a
        // read-only TV.
        var changes = me.MapGroup(string.Empty).RequireAuthorization(AuthSetup.FullSessionPolicy);

        changes.MapPatch("/", UpdateAsync).WithName("UpdateMe").ProducesProblem(StatusCodes.Status400BadRequest);

        changes.MapPost("/password", ChangePasswordAsync)
            .WithName("ChangeMyPassword")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        changes.MapPost("/totp", BeginTotpAsync)
            .WithName("BeginTotpEnrollment")
            .ProducesProblem(StatusCodes.Status409Conflict);

        changes.MapPost("/totp/confirm", ConfirmTotpAsync)
            .WithName("ConfirmTotpEnrollment")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        changes.MapPost("/totp/disable", DisableTotpAsync)
            .WithName("DisableTotp")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return routes;
    }

    private static async Task<Results<Ok<UserProfile>, ProblemHttpResult>> GetAsync(
        HttpContext http, AtriumDbContext db, TotpService totp, CancellationToken ct)
    {
        var userId = http.User.UserId()!.Value;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is null
            ? ApiProblem.Missing()
            : TypedResults.Ok(ToProfile(user, await totp.IsEnabledAsync(userId, ct)));
    }

    private static async Task<Results<Ok<UserProfile>, ProblemHttpResult>> UpdateAsync(
        UpdateProfileRequest request, HttpContext http, UserService users, TotpService totp, CancellationToken ct)
    {
        var userId = http.User.UserId()!.Value;
        var result = await users.UpdateProfileAsync(userId, request.DisplayName, request.Locale, ct);
        return result.User is null
            ? ApiProblem.From(result.Error!.Value)
            : TypedResults.Ok(ToProfile(result.User, await totp.IsEnabledAsync(userId, ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request, HttpContext http, UserService users, AttemptLimiter limiter, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.NewPassword is null)
        {
            return ApiProblem.Invalid();
        }

        var userId = http.User.UserId()!.Value;
        var key = PasswordCheckKey(userId);
        if (limiter.BlockedFor(key) is { } wait)
        {
            return ApiProblem.TooMany(http, wait);
        }

        // The session making the change stays signed in; all others end.
        var result = await users.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, http.User.SessionId(), ct);
        if (result.Error == UserError.WrongPassword)
        {
            limiter.RecordFailure(key, FreePasswordChecks);
        }

        return result.Error is { } error ? ApiProblem.From(error) : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<TotpEnrollmentResponse>, ProblemHttpResult>> BeginTotpAsync(
        HttpContext http, AtriumDbContext db, TotpService totp, CancellationToken ct)
    {
        var userId = http.User.UserId()!.Value;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return ApiProblem.Missing();
        }

        var enrollment = await totp.BeginAsync(userId, ct);
        return enrollment is null
            ? ApiProblem.Create(StatusCodes.Status409Conflict, ApiProblem.TotpAlreadyEnabled)
            : TypedResults.Ok(new TotpEnrollmentResponse(
                Base32.Encode(enrollment.Secret),
                TotpGenerator.ProvisioningUri("Atrium", user.Name, enrollment.Secret)));
    }

    private static async Task<Results<Ok<RecoveryCodesResponse>, ProblemHttpResult>> ConfirmTotpAsync(
        TotpConfirmRequest request, HttpContext http, TotpService totp, CancellationToken ct)
    {
        var codes = await totp.ConfirmAsync(http.User.UserId()!.Value, request.Code, ct);
        return codes is null
            ? ApiProblem.Create(StatusCodes.Status400BadRequest, ApiProblem.InvalidCode)
            : TypedResults.Ok(new RecoveryCodesResponse(codes));
    }

    // Turning two-step verification off asks for the password again, so a
    // stolen unlocked device cannot quietly weaken the account.
    private static async Task<Results<NoContent, ProblemHttpResult>> DisableTotpAsync(
        TotpDisableRequest request,
        HttpContext http,
        UserService users,
        TotpService totp,
        AttemptLimiter limiter,
        CancellationToken ct)
    {
        var userId = http.User.UserId()!.Value;
        var key = PasswordCheckKey(userId);
        if (limiter.BlockedFor(key) is { } wait)
        {
            return ApiProblem.TooMany(http, wait);
        }

        if (!await users.VerifyPasswordAsync(userId, request.Password, ct))
        {
            limiter.RecordFailure(key, FreePasswordChecks);
            return ApiProblem.From(UserError.WrongPassword);
        }

        await totp.DisableAsync(userId, ct);
        return TypedResults.NoContent();
    }

    private static string PasswordCheckKey(Guid userId) => $"password-check:{userId}";

    private static UserProfile ToProfile(User user, bool totpEnabled) => new(
        user.Id, user.Name, user.DisplayName, user.Role, user.Locale, user.QuotaBytes, totpEnabled, user.PasswordResetByAdmin);
}
