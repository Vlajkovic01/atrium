using System.Globalization;
using Atrium.Core.Identity;
using Atrium.Identity.Passwords;
using Atrium.Identity.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

/// <summary>
/// Error responses. The server sends a stable code and parameters, never
/// display text; each client translates the code (DESIGN.md 9).
/// </summary>
public static class ApiProblem
{
    public const string InvalidRequest = "request.invalid";
    public const string NotFound = "resource.not_found";
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string InvalidChallenge = "auth.invalid_challenge";
    public const string InvalidRefreshToken = "auth.invalid_refresh_token";
    public const string TooManyAttempts = "auth.too_many_attempts";
    public const string InvalidLinkCode = "auth.invalid_link_code";

    public static ProblemHttpResult Create(int status, string code, Dictionary<string, object?>? parameters = null) =>
        TypedResults.Problem(
            statusCode: status,
            title: code,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["params"] = parameters ?? [],
            });

    public const string WrongPassword = "auth.wrong_password";
    public const string InvalidCode = "auth.invalid_code";
    public const string TotpAlreadyEnabled = "auth.totp_already_enabled";
    public const string PreconditionRequired = "request.precondition_required";
    public const string VersionConflict = "request.version_conflict";
    public const string UserInvalidName = "user.invalid_name";
    public const string UserNameTaken = "user.name_taken";
    public const string UserInvalidDisplayName = "user.invalid_display_name";
    public const string UserWeakPassword = "user.weak_password";
    public const string UserInvalidLocale = "user.invalid_locale";
    public const string UserInvalidQuota = "user.invalid_quota";
    public const string UserLastAdmin = "user.last_admin";
    public const string UserDeletionScheduled = "user.deletion_scheduled";
    public const string UserNotScheduled = "user.deletion_not_scheduled";
    public const string UserConfirmationMismatch = "user.confirmation_mismatch";
    public const string UserCannotDeleteSelf = "user.cannot_delete_self";

    public static ProblemHttpResult From(UserError error) => error switch
    {
        UserError.NotFound => Missing(),
        UserError.InvalidName => Create(StatusCodes.Status400BadRequest, UserInvalidName, new()
        {
            ["minLength"] = UserName.MinLength,
            ["maxLength"] = UserName.MaxLength,
        }),
        UserError.NameTaken => Create(StatusCodes.Status409Conflict, UserNameTaken),
        UserError.InvalidDisplayName => Create(StatusCodes.Status400BadRequest, UserInvalidDisplayName),
        UserError.WeakPassword => Create(StatusCodes.Status400BadRequest, UserWeakPassword, new()
        {
            ["minLength"] = PasswordPolicy.MinLength,
            ["maxLength"] = PasswordPolicy.MaxLength,
        }),
        // 403, not 401: the session is fine, and 401 would make clients try to refresh it.
        UserError.WrongPassword => Create(StatusCodes.Status403Forbidden, WrongPassword),
        UserError.InvalidLocale => Create(StatusCodes.Status400BadRequest, UserInvalidLocale),
        UserError.InvalidQuota => Create(StatusCodes.Status400BadRequest, UserInvalidQuota),
        UserError.LastAdmin => Create(StatusCodes.Status409Conflict, UserLastAdmin),
        UserError.VersionConflict => Create(StatusCodes.Status412PreconditionFailed, VersionConflict),
        UserError.DeletionScheduled => Create(StatusCodes.Status409Conflict, UserDeletionScheduled),
        _ => Invalid(),
    };

    public static ProblemHttpResult Invalid() => Create(StatusCodes.Status400BadRequest, InvalidRequest);

    public static ProblemHttpResult Missing() => Create(StatusCodes.Status404NotFound, NotFound);

    public static ProblemHttpResult TooMany(HttpContext http, TimeSpan retryAfter)
    {
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Create(
            StatusCodes.Status429TooManyRequests,
            TooManyAttempts,
            new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds });
    }
}
