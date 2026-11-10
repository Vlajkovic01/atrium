namespace Atrium.Core.Identity;

public enum PairingState
{
    Pending,
    Approved,
    Denied,

    /// <summary>The device collected its tokens. A request is single-use.</summary>
    Redeemed,
}

/// <summary>
/// A device without a keyboard asking to be signed in (DESIGN.md 6.4).
/// The device code is the device's secret and is stored hashed; the user
/// code is short, shown on screen and typed or scanned by the approver.
/// </summary>
public sealed class PairingRequest
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required byte[] DeviceCodeHash { get; init; }

    public required string UserCode { get; init; }

    public required string DeviceName { get; init; }

    public required DeviceKind DeviceKind { get; init; }

    public string? Platform { get; init; }

    public string? AppVersion { get; init; }

    public string? RequestIp { get; init; }

    public PairingState State { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public Guid? ApprovedByUserId { get; set; }

    public bool ReadOnly { get; set; }

    public bool HidePersonalLibrary { get; set; }

    /// <summary>The session created when the device redeemed the approval.</summary>
    public Guid? SessionId { get; set; }
}

/// <summary>
/// Single-use code a signed-in session shows as a QR so a phone can sign in
/// without typing the password (DESIGN.md 6.5). Stored hashed.
/// </summary>
public sealed class LinkCode
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required byte[] CodeHash { get; init; }

    public required Guid UserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }
}
