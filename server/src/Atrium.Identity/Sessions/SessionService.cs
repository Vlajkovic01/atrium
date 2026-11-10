using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Codes;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Identity.Sessions;

public sealed record DeviceInfo(string Name, DeviceKind Kind, string? Platform, string? AppVersion);

public sealed record SessionRestrictions(bool ReadOnly, bool HidePersonalLibrary)
{
    public static SessionRestrictions None { get; } = new(false, false);
}

/// <summary>A session together with the refresh token, which exists in plain form only here.</summary>
public sealed record IssuedSession(Session Session, User User, string RefreshToken, DateTimeOffset RefreshExpiresAt);

public enum RefreshFailure
{
    UnknownToken,
    Expired,
    SessionRevoked,
    UserUnavailable,

    /// <summary>An already rotated token was presented outside the grace window. The session has been revoked.</summary>
    ReuseDetected,
}

public sealed record RefreshResult(IssuedSession? Issued, RefreshFailure? Failure)
{
    public static RefreshResult Failed(RefreshFailure failure) => new(null, failure);
}

/// <summary>Per-device sessions and their rotating refresh tokens (DESIGN.md 6.2).</summary>
public sealed class SessionService(AtriumDbContext db, SessionRevocationCache revocations, TimeProvider time)
{
    // A chain longer than this within the grace window is not a lost
    // response, it is something replaying tokens.
    private const int MaxGraceChain = 5;

    public async Task<IssuedSession> CreateAsync(
        User user, DeviceInfo device, SessionRestrictions restrictions, string? ip, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var session = new Session
        {
            UserId = user.Id,
            DeviceName = device.Name,
            DeviceKind = device.Kind,
            Platform = device.Platform,
            AppVersion = device.AppVersion,
            CreatedAt = now,
            LastSeenAt = now,
            LastIp = ip,
            ReadOnly = restrictions.ReadOnly,
            HidePersonalLibrary = restrictions.HidePersonalLibrary,
        };
        db.Sessions.Add(session);
        var (plain, token) = NewRefreshToken(session.Id, now);
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return new IssuedSession(session, user, plain, token.ExpiresAt);
    }

    public async Task<RefreshResult> RefreshAsync(string presentedToken, string? ip, CancellationToken ct)
    {
        try
        {
            return await RefreshOnceAsync(presentedToken, ip, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request rotated the same token a moment ago. Start
            // over: this request is now a reuse inside the grace window.
            db.ChangeTracker.Clear();
            return await RefreshOnceAsync(presentedToken, ip, ct);
        }
    }

    private async Task<RefreshResult> RefreshOnceAsync(string presentedToken, string? ip, CancellationToken ct)
    {
        var hash = SecretCode.Hash(presentedToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            return RefreshResult.Failed(RefreshFailure.UnknownToken);
        }

        var session = await db.Sessions.SingleAsync(s => s.Id == token.SessionId, ct);
        if (session.RevokedAt is not null)
        {
            return RefreshResult.Failed(RefreshFailure.SessionRevoked);
        }

        var user = await db.Users.SingleAsync(u => u.Id == session.UserId, ct);
        if (user.Disabled)
        {
            return RefreshResult.Failed(RefreshFailure.UserUnavailable);
        }

        var now = time.GetUtcNow();
        if (token.ExpiresAt <= now)
        {
            return RefreshResult.Failed(RefreshFailure.Expired);
        }

        var current = token;
        if (token.UsedAt is { } usedAt)
        {
            // Already rotated. Inside the grace window this is a client whose
            // response was lost: give it a fresh token and retire the one it
            // never received. Outside the window it is a stolen or replayed
            // token, and the only safe answer is to end the session.
            var latest = now - usedAt <= TokenLifetimes.RefreshReuseGrace
                ? await FindUnusedSuccessorAsync(token, ct)
                : null;
            if (latest is null)
            {
                session.RevokedAt = now;
                await db.SaveChangesAsync(ct);
                revocations.Add(session.Id);
                return RefreshResult.Failed(RefreshFailure.ReuseDetected);
            }

            current = latest;
        }

        var (plain, next) = NewRefreshToken(session.Id, now);
        db.RefreshTokens.Add(next);
        current.UsedAt = now;
        current.ReplacedById = next.Id;
        session.LastSeenAt = now;
        session.LastIp = ip;
        await db.SaveChangesAsync(ct);
        return new RefreshResult(new IssuedSession(session, user, plain, next.ExpiresAt), null);
    }

    private async Task<RefreshToken?> FindUnusedSuccessorAsync(RefreshToken token, CancellationToken ct)
    {
        var current = token;
        for (var hop = 0; hop < MaxGraceChain; hop++)
        {
            if (current.ReplacedById is not { } nextId)
            {
                return null;
            }

            var next = await db.RefreshTokens.SingleOrDefaultAsync(t => t.Id == nextId, ct);
            if (next is null)
            {
                return null;
            }

            if (next.UsedAt is null)
            {
                return next;
            }

            current = next;
        }

        return null;
    }

    public Task<List<Session>> ListAsync(Guid userId, CancellationToken ct) =>
        db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct);

    /// <summary>Revokes one of the user's own sessions. False if it is not theirs or does not exist.</summary>
    public async Task<bool> RevokeAsync(Guid sessionId, Guid ownerUserId, CancellationToken ct)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == ownerUserId, ct);
        if (session is null)
        {
            return false;
        }

        if (session.RevokedAt is null)
        {
            session.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        revocations.Add(session.Id);
        return true;
    }

    /// <summary>Signs the user out everywhere, optionally keeping one session (the one changing the password).</summary>
    public async Task RevokeAllAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != exceptSessionId)
            .ToListAsync(ct);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
        foreach (var session in sessions)
        {
            revocations.Add(session.Id);
        }
    }

    private static (string Plain, RefreshToken Token) NewRefreshToken(Guid sessionId, DateTimeOffset now)
    {
        var plain = SecretCode.NewToken();
        return (plain, new RefreshToken
        {
            SessionId = sessionId,
            TokenHash = SecretCode.Hash(plain),
            IssuedAt = now,
            ExpiresAt = now + TokenLifetimes.RefreshToken,
        });
    }
}
