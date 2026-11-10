namespace Atrium.Core.Identity;

public enum DeviceKind
{
    Web,
    Mobile,
    Tv,
    Other,
}

/// <summary>One signed-in device. Revoking it invalidates its tokens and signed URLs.</summary>
public sealed class Session
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    public required string DeviceName { get; set; }

    public required DeviceKind DeviceKind { get; init; }

    public string? Platform { get; init; }

    public string? AppVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }

    public string? LastIp { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Restriction chosen when a paired device was approved: no writes.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Restriction chosen when a paired device was approved: the personal library is hidden.</summary>
    public bool HidePersonalLibrary { get; init; }
}

/// <summary>
/// One link in a session's refresh token chain. Only the hash of the token
/// is stored (DESIGN.md 6.2).
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid SessionId { get; init; }

    public required byte[] TokenHash { get; init; }

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When this token was exchanged for the next one.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public Guid? ReplacedById { get; set; }
}
