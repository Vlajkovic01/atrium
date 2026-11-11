using Atrium.Api.Auth;
using Atrium.Core.Identity;
using Atrium.Identity.Passwords;
using Atrium.Identity.Sessions;
using Atrium.Identity.SignIn;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

public sealed record DeviceRequest(string Name, DeviceKind Kind, string? Platform, string? AppVersion);

public sealed record LoginRequest(string Name, string Password, DeviceRequest Device);

public sealed record TotpLoginRequest(string Challenge, string Code, DeviceRequest Device);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record UserSummary(Guid Id, string Name, string DisplayName, UserRole Role, string Locale);

/// <param name="RefreshToken">
/// Null for web sessions: the browser receives it in an HttpOnly cookie
/// that scripts cannot read.
/// </param>
public sealed record TokenResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string? RefreshToken,
    Guid SessionId,
    UserSummary User);

public enum LoginStatus
{
    SignedIn,
    TotpRequired,
}

public sealed record LoginResponse(LoginStatus Status, TokenResponse? Tokens, string? TotpChallenge);

public static class AuthEndpoints
{
    public const string RefreshCookie = "atrium_refresh";
    private const string CookiePath = "/api/v1/auth";

    // Free failures before blocking starts. Per address and account together
    // it is strict; per account alone it is loose, so that someone else
    // guessing cannot easily lock the real owner out.
    private const int FreePerAddressAndAccount = 5;
    private const int FreePerAddress = 20;
    private const int FreePerAccount = 50;
    private const int FreeTotp = 5;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/login/totp", TotpLoginAsync)
            .WithName("LoginTotp")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshTokens")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", LogoutAsync).WithName("Logout");

        return routes;
    }

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        HttpContext http,
        SignInService signIn,
        SessionService sessions,
        AccessTokens tokens,
        AttemptLimiter limiter,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrEmpty(request.Password)
            || request.Name.Length > UserName.MaxLength * 2
            || request.Password.Length > PasswordPolicy.MaxLength
            || !TryReadDevice(request.Device, out var device))
        {
            return ApiProblem.Invalid();
        }

        var ip = ClientIp(http);
        var account = UserName.KeyOf(request.Name);
        var perBoth = $"login:{ip}|{account}";
        var perAddress = $"login-ip:{ip}";
        var perAccount = $"login-user:{account}";
        if (Longest(limiter.BlockedFor(perBoth), limiter.BlockedFor(perAddress), limiter.BlockedFor(perAccount)) is { } wait)
        {
            return ApiProblem.TooMany(http, wait);
        }

        var result = await signIn.PasswordSignInAsync(request.Name, request.Password, ct);
        if (result.Status == PasswordSignInStatus.Failed)
        {
            limiter.RecordFailure(perBoth, FreePerAddressAndAccount);
            limiter.RecordFailure(perAddress, FreePerAddress);
            limiter.RecordFailure(perAccount, FreePerAccount);

            // One answer for unknown name, wrong password and disabled account.
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, ApiProblem.InvalidCredentials);
        }

        limiter.RecordSuccess(perBoth);
        var user = result.User!;
        if (result.Status == PasswordSignInStatus.TotpRequired)
        {
            return TypedResults.Ok(new LoginResponse(LoginStatus.TotpRequired, null, tokens.IssueTotpChallenge(user.Id)));
        }

        var issued = await sessions.CreateAsync(user, device, SessionRestrictions.None, ip, ct);
        return TypedResults.Ok(new LoginResponse(LoginStatus.SignedIn, Respond(http, tokens, issued), null));
    }

    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> TotpLoginAsync(
        TotpLoginRequest request,
        HttpContext http,
        SignInService signIn,
        SessionService sessions,
        AccessTokens tokens,
        AttemptLimiter limiter,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Code) || request.Code.Length > 16 || !TryReadDevice(request.Device, out var device))
        {
            return ApiProblem.Invalid();
        }

        if (await tokens.ReadTotpChallengeAsync(request.Challenge) is not { } userId)
        {
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, ApiProblem.InvalidChallenge);
        }

        var key = $"totp:{userId}";
        if (limiter.BlockedFor(key) is { } wait)
        {
            return ApiProblem.TooMany(http, wait);
        }

        var user = await signIn.TotpSignInAsync(userId, request.Code, ct);
        if (user is null)
        {
            limiter.RecordFailure(key, FreeTotp);
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, ApiProblem.InvalidCredentials);
        }

        limiter.RecordSuccess(key);
        var issued = await sessions.CreateAsync(user, device, SessionRestrictions.None, ClientIp(http), ct);
        return TypedResults.Ok(new LoginResponse(LoginStatus.SignedIn, Respond(http, tokens, issued), null));
    }

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> RefreshAsync(
        RefreshRequest? request,
        HttpContext http,
        SessionService sessions,
        AccessTokens tokens,
        CancellationToken ct)
    {
        var presented = string.IsNullOrEmpty(request?.RefreshToken)
            ? http.Request.Cookies[RefreshCookie]
            : request.RefreshToken;
        if (string.IsNullOrEmpty(presented) || presented.Length > 256)
        {
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, ApiProblem.InvalidRefreshToken);
        }

        var result = await sessions.RefreshAsync(presented, ClientIp(http), ct);
        if (result.Issued is null)
        {
            // The reason is not sent: a caller holding a bad token learns
            // nothing about why it is bad.
            http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = CookiePath });
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, ApiProblem.InvalidRefreshToken);
        }

        return TypedResults.Ok(Respond(http, tokens, result.Issued));
    }

    private static async Task<NoContent> LogoutAsync(HttpContext http, SessionService sessions, CancellationToken ct)
    {
        if (http.User.SessionId() is { } sessionId && http.User.UserId() is { } userId)
        {
            await sessions.RevokeAsync(sessionId, userId, ct);
        }

        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = CookiePath });
        return TypedResults.NoContent();
    }

    /// <summary>Builds the token response. Web sessions get the refresh token as a cookie only.</summary>
    internal static TokenResponse Respond(HttpContext http, AccessTokens tokens, IssuedSession issued)
    {
        var isWeb = issued.Session.DeviceKind == DeviceKind.Web;
        if (isWeb)
        {
            http.Response.Cookies.Append(RefreshCookie, issued.RefreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = CookiePath,
                Expires = issued.RefreshExpiresAt,
                IsEssential = true,
            });
        }

        var user = issued.User;
        return new TokenResponse(
            tokens.IssueAccessToken(user, issued.Session),
            (int)TokenLifetimes.AccessToken.TotalSeconds,
            isWeb ? null : issued.RefreshToken,
            issued.Session.Id,
            new UserSummary(user.Id, user.Name, user.DisplayName, user.Role, user.Locale));
    }

    internal static bool TryReadDevice(DeviceRequest? request, out DeviceInfo device)
    {
        device = new DeviceInfo(string.Empty, DeviceKind.Other, null, null);
        var name = request?.Name?.Trim();
        if (request is null
            || string.IsNullOrEmpty(name)
            || name.Length > 128
            || !Enum.IsDefined(request.Kind)
            || request.Platform is { Length: > 64 }
            || request.AppVersion is { Length: > 32 })
        {
            return false;
        }

        device = new DeviceInfo(name, request.Kind, request.Platform, request.AppVersion);
        return true;
    }

    internal static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static TimeSpan? Longest(params TimeSpan?[] waits)
    {
        TimeSpan? longest = null;
        foreach (var wait in waits)
        {
            if (wait is { } w && (longest is null || w > longest))
            {
                longest = w;
            }
        }

        return longest;
    }
}
