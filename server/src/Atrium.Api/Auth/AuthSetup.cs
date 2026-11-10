using System.Security.Claims;
using Atrium.Api.Hosting;
using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Crypto;
using Atrium.Identity.Pairing;
using Atrium.Identity.Passwords;
using Atrium.Identity.Sessions;
using Atrium.Identity.SignIn;
using Atrium.Identity.Totp;
using Atrium.Identity.Users;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Atrium.Api.Auth;

public static class AuthSetup
{
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// A session without restrictions. Required wherever a session can create
    /// another session, so a read-only living-room TV cannot be used to sign
    /// in an unrestricted device.
    /// </summary>
    public const string FullSessionPolicy = "FullSession";

    private const long LowMemoryThresholdBytes = 4L * 1024 * 1024 * 1024;

    public static IServiceCollection AddAtriumAuth(this IServiceCollection services, BootstrapOptions bootstrap)
    {
        services.AddSingleton(_ => ServerKeys.LoadOrCreate(Path.Combine(bootstrap.ConfigDir, "keys")));
        services.AddSingleton<SecretProtector>();

        // Small devices get the lighter Argon2id profile (DESIGN.md 6.1).
        services.TryAddSingleton(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < LowMemoryThresholdBytes
            ? Argon2Settings.LowMemory
            : Argon2Settings.Default);
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<PasswordService>();

        services.AddSingleton<AttemptLimiter>();
        services.AddSingleton<SessionRevocationCache>();
        services.AddSingleton<AccessTokens>();
        services.AddScoped<SessionService>();
        services.AddScoped<SignInService>();
        services.AddScoped<PairingService>();
        services.AddScoped<LinkCodeService>();
        services.AddScoped<UserService>();
        services.AddScoped<TotpService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<AccessTokens, SessionRevocationCache>((options, tokens, revocations) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.AccessTokenValidation();
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        // A valid signature is not enough: the session may
                        // have been revoked since the token was issued.
                        if (context.Principal?.SessionId() is not { } sessionId || revocations.IsRevoked(sessionId))
                        {
                            context.Fail("Session is revoked.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        // Secure by default: an endpoint needs a signed-in user unless it
        // explicitly opts out with AllowAnonymous.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(nameof(UserRole.Admin)))
            .AddPolicy(FullSessionPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !context.User.HasClaim(
                    claim => claim.Type is AccessTokens.ReadOnlyClaim or AccessTokens.HidePersonalClaim)));

        return services;
    }

    /// <summary>Restores recently revoked sessions after a restart, before any request is served.</summary>
    public static async Task LoadRevokedSessionsAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        await app.Services.GetRequiredService<SessionRevocationCache>().LoadAsync(db, app.Lifetime.ApplicationStopping);
    }

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;

    public static Guid? SessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(AccessTokens.SessionClaim), out var id) ? id : null;
}
