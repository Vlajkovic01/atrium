using Atrium.Api.Auth;
using Atrium.Core.Identity;
using Atrium.Identity.Codes;
using Atrium.Identity.Pairing;
using Atrium.Identity.Sessions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

public sealed record StartPairingRequest(DeviceRequest Device);

/// <param name="DeviceCode">The device's secret. It is never shown on screen.</param>
/// <param name="UserCode">Shown on screen as text and inside the QR code.</param>
/// <param name="VerificationUrl">What the QR code contains: opens the approval page with the code filled in.</param>
public sealed record StartPairingResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUrl,
    int ExpiresInSeconds,
    int IntervalSeconds);

public sealed record PairingTokenRequest(string DeviceCode);

public enum PairingStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
}

public sealed record PairingTokenResponse(PairingStatus Status, TokenResponse? Tokens);

public sealed record PairingDetails(
    string UserCode,
    string DeviceName,
    DeviceKind DeviceKind,
    string? Platform,
    string? RequestIp,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record ApprovePairingRequest(bool ReadOnly, bool HidePersonalLibrary);

public sealed record LinkCodeResponse(string Code, int ExpiresInSeconds);

public sealed record RedeemLinkCodeRequest(string Code, DeviceRequest Device);

public static class PairingEndpoints
{
    private const int FreeUnknownCodes = 10;

    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder routes)
    {
        var pairing = routes.MapGroup("/auth/pairing").WithTags("Pairing");

        pairing.MapPost("/", StartAsync)
            .WithName("StartPairing")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        pairing.MapPost("/token", TokenAsync)
            .WithName("PollPairing")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Approving a device hands out a session. A session that is itself
        // restricted must not be able to create an unrestricted one.
        pairing.MapGet("/{userCode}", DetailsAsync)
            .WithName("GetPairing")
            .RequireAuthorization(AuthSetup.FullSessionPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        pairing.MapPost("/{userCode}/approve", ApproveAsync)
            .WithName("ApprovePairing")
            .RequireAuthorization(AuthSetup.FullSessionPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound);

        pairing.MapPost("/{userCode}/deny", DenyAsync)
            .WithName("DenyPairing")
            .RequireAuthorization(AuthSetup.FullSessionPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var links = routes.MapGroup("/auth/link-codes").WithTags("Pairing");

        links.MapPost("/", async (HttpContext http, LinkCodeService service, CancellationToken ct) =>
                new LinkCodeResponse(
                    await service.CreateAsync(http.User.UserId()!.Value, ct),
                    (int)LinkCodeService.Lifetime.TotalSeconds))
            .WithName("CreateLinkCode")
            .RequireAuthorization(AuthSetup.FullSessionPolicy);

        links.MapPost("/redeem", RedeemLinkAsync)
            .WithName("RedeemLinkCode")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return routes;
    }

    private static async Task<Results<Ok<StartPairingResponse>, ProblemHttpResult>> StartAsync(
        StartPairingRequest request, HttpContext http, PairingService pairing, CancellationToken ct)
    {
        if (!AuthEndpoints.TryReadDevice(request.Device, out var device))
        {
            return ApiProblem.Invalid();
        }

        var started = await pairing.StartAsync(device, AuthEndpoints.ClientIp(http), ct);
        if (started is null)
        {
            return ApiProblem.TooMany(http, TimeSpan.FromMinutes(1));
        }

        var url = $"{http.Request.Scheme}://{http.Request.Host}/pair?code={started.UserCode}";
        return TypedResults.Ok(new StartPairingResponse(
            started.DeviceCode,
            started.UserCode,
            url,
            (int)PairingService.Lifetime.TotalSeconds,
            (int)PairingService.PollInterval.TotalSeconds));
    }

    private static async Task<Results<Ok<PairingTokenResponse>, ProblemHttpResult>> TokenAsync(
        PairingTokenRequest request,
        HttpContext http,
        PairingService pairing,
        AccessTokens tokens,
        AttemptLimiter limiter,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.DeviceCode) || request.DeviceCode.Length > 256)
        {
            return ApiProblem.Invalid();
        }

        var ip = AuthEndpoints.ClientIp(http);
        var key = $"pairing-token:{ip}";
        if (limiter.BlockedFor(key) is { } wait)
        {
            return ApiProblem.TooMany(http, wait);
        }

        var result = await pairing.RedeemAsync(request.DeviceCode, ip, ct);
        switch (result.Status)
        {
            case PairingRedeemStatus.Approved:
                return TypedResults.Ok(new PairingTokenResponse(
                    PairingStatus.Approved, AuthEndpoints.Respond(http, tokens, result.Issued!)));
            case PairingRedeemStatus.Pending:
                return TypedResults.Ok(new PairingTokenResponse(PairingStatus.Pending, null));
            case PairingRedeemStatus.Denied:
                return TypedResults.Ok(new PairingTokenResponse(PairingStatus.Denied, null));
            default:
                // Counted, so device codes cannot be guessed at speed.
                limiter.RecordFailure(key, FreeUnknownCodes);
                return TypedResults.Ok(new PairingTokenResponse(PairingStatus.Expired, null));
        }
    }

    private static async Task<Results<Ok<PairingDetails>, ProblemHttpResult>> DetailsAsync(
        string userCode, HttpContext http, PairingService pairing, AttemptLimiter limiter, CancellationToken ct)
    {
        var key = $"pairing-lookup:{http.User.UserId()}";
        if (limiter.BlockedFor(key) is { } wait)
        {
            return ApiProblem.TooMany(http, wait);
        }

        var request = UserCode.Normalize(userCode) is { } code ? await pairing.FindOpenAsync(code, ct) : null;
        if (request is null)
        {
            // Counted, so a signed-in user cannot search for other people's codes.
            limiter.RecordFailure(key, FreeUnknownCodes);
            return ApiProblem.Missing();
        }

        return TypedResults.Ok(new PairingDetails(
            request.UserCode,
            request.DeviceName,
            request.DeviceKind,
            request.Platform,
            request.RequestIp,
            request.CreatedAt,
            request.ExpiresAt));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ApproveAsync(
        string userCode, ApprovePairingRequest? request, HttpContext http, PairingService pairing, CancellationToken ct)
    {
        var restrictions = new SessionRestrictions(request?.ReadOnly ?? false, request?.HidePersonalLibrary ?? false);
        return UserCode.Normalize(userCode) is { } code
            && await pairing.ApproveAsync(code, http.User.UserId()!.Value, restrictions, ct)
            ? TypedResults.NoContent()
            : ApiProblem.Missing();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DenyAsync(
        string userCode, PairingService pairing, CancellationToken ct) =>
        UserCode.Normalize(userCode) is { } code && await pairing.DenyAsync(code, ct)
            ? TypedResults.NoContent()
            : ApiProblem.Missing();

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> RedeemLinkAsync(
        RedeemLinkCodeRequest request,
        HttpContext http,
        LinkCodeService service,
        AccessTokens tokens,
        AttemptLimiter limiter,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Code)
            || request.Code.Length > 256
            || !AuthEndpoints.TryReadDevice(request.Device, out var device))
        {
            return ApiProblem.Invalid();
        }

        var ip = AuthEndpoints.ClientIp(http);
        var key = $"link-redeem:{ip}";
        if (limiter.BlockedFor(key) is { } wait)
        {
            return ApiProblem.TooMany(http, wait);
        }

        var issued = await service.RedeemAsync(request.Code, device, ip, ct);
        if (issued is null)
        {
            limiter.RecordFailure(key, FreeUnknownCodes);
            return ApiProblem.Create(StatusCodes.Status401Unauthorized, ApiProblem.InvalidLinkCode);
        }

        return TypedResults.Ok(AuthEndpoints.Respond(http, tokens, issued));
    }
}
