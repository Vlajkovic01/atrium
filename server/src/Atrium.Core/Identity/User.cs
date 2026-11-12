namespace Atrium.Core.Identity;

public enum UserRole
{
    Admin,
    User,

    /// <summary>Read-only access to libraries granted to them.</summary>
    Guest,
}

public sealed class User : IVersioned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Sign-in name as the user typed it.</summary>
    public required string Name { get; set; }

    /// <summary>Normalized form of <see cref="Name"/>; unique. See <see cref="UserName"/>.</summary>
    public required string NameKey { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Argon2id hash including its parameters. Never returned by the API or logged.</summary>
    public required string PasswordHash { get; set; }

    public required UserRole Role { get; set; }

    public string Locale { get; set; } = "en";

    /// <summary>Null means unlimited.</summary>
    public long? QuotaBytes { get; set; }

    public bool Disabled { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset PasswordChangedAt { get; set; }

    /// <summary>
    /// Set when an admin reset the password, cleared once the user has been
    /// told at their next sign-in (DESIGN.md 6.3).
    /// </summary>
    public bool PasswordResetByAdmin { get; set; }

    /// <summary>
    /// When an admin scheduled this account for deletion (D-025). The account
    /// is disabled meanwhile, and its files are removed at
    /// <see cref="DeleteAfter"/> unless the deletion is cancelled.
    /// </summary>
    public DateTimeOffset? DeletionScheduledAt { get; set; }

    public DateTimeOffset? DeleteAfter { get; set; }

    public int Version { get; set; }
}
