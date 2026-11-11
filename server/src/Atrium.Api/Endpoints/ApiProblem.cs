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

    public static ProblemHttpResult From(UserError error) => error switch
    {
        UserError.NotFound => Missing(),
        UserError.InvalidName => Create(StatusCodes.Status400BadRequest, "user.invalid_name", new()
        {
            ["minLength"] = UserName.MinLength,
            ["maxLength"] = UserName.MaxLength,
        }),
        UserError.NameTaken => Create(StatusCodes.Status409Conflict, "user.name_taken"),
        UserError.InvalidDisplayName => Create(StatusCodes.Status400BadRequest, "user.invalid_display_name"),
        UserError.WeakPassword => Create(StatusCodes.Status400BadRequest, "user.weak_password", new()
        {
            ["minLength"] = PasswordPolicy.MinLength,
            ["maxLength"] = PasswordPolicy.MaxLength,
        }),
        // 403, not 401: the session is fine, and 401 would make clients try to refresh it.
        UserError.WrongPassword => Create(StatusCodes.Status403Forbidden, WrongPassword),
        UserError.InvalidLocale => Create(StatusCodes.Status400BadRequest, "user.invalid_locale"),
        UserError.InvalidQuota => Create(StatusCodes.Status400BadRequest, "user.invalid_quota"),
        UserError.LastAdmin => Create(StatusCodes.Status409Conflict, "user.last_admin"),
        UserError.VersionConflict => Create(StatusCodes.Status412PreconditionFailed, VersionConflict),
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
