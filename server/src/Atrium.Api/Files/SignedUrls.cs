using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Files;
using Atrium.Identity.Crypto;
using Atrium.Identity.Sessions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Files;

/// <summary>
/// URLs that carry their own proof of access, for media elements and
/// download links that cannot send an Authorization header (DESIGN.md 6.6).
/// The signature covers the path, the options, the expiry and the session;
/// revoking the session invalidates every URL it was given.
/// </summary>
public sealed class SignedUrls(ServerKeys keys, SessionRevocationCache revocations, TimeProvider time)
{
    public const string ExpiresParameter = "exp";
    public const string SessionParameter = "sid";
    public const string SignatureParameter = "sig";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    private readonly byte[] _key = keys.Derive("atrium/signed-url/v1");

    /// <summary>A signed version of <paramref name="path"/> with the given extra parameters.</summary>
    public (string Url, DateTimeOffset ExpiresAt) Sign(string path, Guid sessionId, IReadOnlyDictionary<string, string>? options = null)
    {
        var expires = time.GetUtcNow() + DefaultLifetime;
        var exp = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var option in options ?? new Dictionary<string, string>())
        {
            parameters[option.Key] = option.Value;
        }

        parameters[ExpiresParameter] = exp;
        parameters[SessionParameter] = sessionId.ToString("N");
        parameters[SignatureParameter] = Compute(path, options, exp, sessionId);
        return (QueryHelpers.AddQueryString(path, parameters), DateTimeOffset.FromUnixTimeSeconds(long.Parse(exp, CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The actor a signed request stands for, or null when the signature is
    /// wrong, expired, or its session is revoked or its user disabled.
    /// </summary>
    public async Task<FileActor?> VerifyAsync(HttpRequest request, IReadOnlyCollection<string> optionNames, AtriumDbContext db, CancellationToken ct)
    {
        var query = request.Query;
        if (!long.TryParse(query[ExpiresParameter], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)
            || !Guid.TryParseExact(query[SessionParameter], "N", out var sessionId)
            || query[SignatureParameter].ToString() is not { Length: > 0 } signature)
        {
            return null;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(Math.Min(exp, 253402300799)) <= time.GetUtcNow())
        {
            return null;
        }

        var options = optionNames
            .Where(name => query.ContainsKey(name))
            .ToDictionary(name => name, name => query[name].ToString(), StringComparer.Ordinal);
        var expected = Compute(request.Path.Value ?? string.Empty, options, exp.ToString(CultureInfo.InvariantCulture), sessionId);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
        {
            return null;
        }

        if (revocations.IsRevoked(sessionId))
        {
            return null;
        }

        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId && s.RevokedAt == null, ct);
        if (session is null)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId && !u.Disabled, ct);
        return user is null
            ? null
            : new FileActor(user.Id, user.Role == UserRole.Guest, session.ReadOnly, session.HidePersonalLibrary);
    }

    private string Compute(string path, IReadOnlyDictionary<string, string>? options, string exp, Guid sessionId)
    {
        var text = new StringBuilder()
            .Append(path).Append('\n')
            .Append(exp).Append('\n')
            .Append(sessionId.ToString("N"));
        foreach (var option in (options ?? new Dictionary<string, string>()).OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            text.Append('\n').Append(option.Key).Append('=').Append(option.Value);
        }

        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text.ToString())));
    }
}
