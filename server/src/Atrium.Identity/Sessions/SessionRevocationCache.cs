using System.Collections.Concurrent;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Identity.Sessions;

public static class TokenLifetimes
{
    public static readonly TimeSpan AccessToken = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshToken = TimeSpan.FromDays(60);

    /// <summary>
    /// How long the previous refresh token is still accepted after it was
    /// rotated. Covers a response lost on a bad network (DESIGN.md 6.2).
    /// </summary>
    public static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan TotpChallenge = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Sessions revoked recently enough that one of their access tokens could
/// still be unexpired. Checked on every request, so revocation takes effect
/// at once instead of when the token runs out. In memory because there is
/// exactly one server process (D-015); reloaded from the database at start.
/// </summary>
public sealed class SessionRevocationCache(TimeProvider time)
{
    // Twice the token lifetime, as margin for clock adjustments.
    private static readonly TimeSpan Retention = TokenLifetimes.AccessToken * 2;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _revoked = new();

    public bool IsRevoked(Guid sessionId) => _revoked.ContainsKey(sessionId);

    public void Add(Guid sessionId)
    {
        var now = time.GetUtcNow();
        _revoked[sessionId] = now;

        foreach (var (id, at) in _revoked)
        {
            if (now - at > Retention)
            {
                _revoked.TryRemove(id, out _);
            }
        }
    }

    public async Task LoadAsync(AtriumDbContext db, CancellationToken ct)
    {
        var since = time.GetUtcNow() - Retention;
        var recent = await db.Sessions
            .Where(s => s.RevokedAt != null && s.RevokedAt >= since)
            .Select(s => new { s.Id, s.RevokedAt })
            .ToListAsync(ct);

        foreach (var session in recent)
        {
            _revoked[session.Id] = session.RevokedAt!.Value;
        }
    }
}
