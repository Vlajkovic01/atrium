using System.Globalization;
using Atrium.Core.Identity;
using Atrium.Identity.Crypto;
using Atrium.Identity.Sessions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Atrium.Api.Auth;

/// <summary>
/// Issues the short-lived signed tokens (DESIGN.md 6.2). Access tokens and
/// two-step challenges are signed with different derived keys, so one can
/// never be accepted in place of the other.
/// </summary>
public sealed class AccessTokens(ServerKeys keys, TimeProvider time)
{
    public const string Issuer = "atrium";
    public const string ApiAudience = "atrium-api";
    public const string ChallengeAudience = "atrium-totp-challenge";

    public const string SessionClaim = "sid";
    public const string RoleClaim = "role";
    public const string ReadOnlyClaim = "ro";
    public const string HidePersonalClaim = "hp";

    private readonly SymmetricSecurityKey _accessKey = new(keys.Derive("atrium/access-token-signing/v1"));
    private readonly SymmetricSecurityKey _challengeKey = new(keys.Derive("atrium/totp-challenge-signing/v1"));
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public string IssueAccessToken(User user, Session session)
    {
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
            [SessionClaim] = session.Id.ToString(),
            [RoleClaim] = user.Role.ToString(),
        };
        if (session.ReadOnly)
        {
            claims[ReadOnlyClaim] = "1";
        }

        if (session.HidePersonalLibrary)
        {
            claims[HidePersonalClaim] = "1";
        }

        return Issue(_accessKey, ApiAudience, TokenLifetimes.AccessToken, claims);
    }

    /// <summary>Proof that the password step succeeded for this user, valid for a few minutes.</summary>
    public string IssueTotpChallenge(Guid userId) =>
        Issue(_challengeKey, ChallengeAudience, TokenLifetimes.TotpChallenge, new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
        });

    public async Task<Guid?> ReadTotpChallengeAsync(string? challenge)
    {
        if (string.IsNullOrEmpty(challenge))
        {
            return null;
        }

        var result = await _handler.ValidateTokenAsync(challenge, ValidationParameters(_challengeKey, ChallengeAudience));
        return result.IsValid
            && result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub)
            && Guid.TryParse(Convert.ToString(sub, CultureInfo.InvariantCulture), out var userId)
            ? userId
            : null;
    }

    public TokenValidationParameters AccessTokenValidation() => ValidationParameters(_accessKey, ApiAudience);

    private string Issue(SecurityKey key, string audience, TimeSpan lifetime, Dictionary<string, object> claims)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    private TokenValidationParameters ValidationParameters(SecurityKey key, string audience) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = audience,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = RoleClaim,
        // The server issues and checks its own tokens on one clock, so no
        // skew allowance is needed. Checked against the injected clock.
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            return expires is { } e && e > now && (notBefore is null || notBefore <= now);
        },
    };
}
