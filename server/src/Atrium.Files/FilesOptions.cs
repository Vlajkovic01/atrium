namespace Atrium.Files;

/// <summary>Retention and safety periods for files. Editable in the admin UI later (Phase 11).</summary>
public sealed class FilesOptions
{
    /// <summary>
    /// How long an unreferenced blob is kept before its content is deleted.
    /// Also covers restoring a metadata backup a few days old: its nodes
    /// still find their content (DESIGN.md 4.3).
    /// </summary>
    public TimeSpan BlobGracePeriod { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long a deleted item stays in the trash.</summary>
    public TimeSpan TrashRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Earlier versions are removed this long after they were replaced.</summary>
    public TimeSpan VersionRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>At most this many earlier versions are kept per file.</summary>
    public int MaxVersions { get; init; } = 10;

    /// <summary>How long after an admin deletes an account its files are removed; until then the deletion can be cancelled (D-025).</summary>
    public TimeSpan UserDeletionDelay { get; init; } = TimeSpan.FromDays(30);
}
