namespace Atrium.Core.Identity;

/// <summary>A user's TOTP secret, encrypted with the server key. At most one per user.</summary>
public sealed class TotpCredential
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    public required byte[] EncryptedSecret { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null until the user proves the authenticator works; unconfirmed credentials are not enforced.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>Single-use fallback for a lost authenticator. Stored hashed.</summary>
public sealed class RecoveryCode
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    public required byte[] CodeHash { get; init; }

    public DateTimeOffset? UsedAt { get; set; }
}
