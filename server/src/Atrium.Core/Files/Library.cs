namespace Atrium.Core.Files;

public enum LibraryKind
{
    /// <summary>One per user, private: "My files".</summary>
    Personal,
    Shared,
    Photos,
    Movies,
    Shows,
}

public enum LibraryMode
{
    /// <summary>Atrium owns the bytes, stored as content-addressed blobs (D-002).</summary>
    Managed,

    /// <summary>An existing folder Atrium indexes, read-only by default (DESIGN.md 4.4).</summary>
    External,
}

public enum LibraryState
{
    Online,

    /// <summary>The folder is missing, empty or unreadable; nothing in the index is removed while it is (DESIGN.md 4.4).</summary>
    Offline,
}

/// <summary>A tree of files and folders on one storage location (DESIGN.md 2.2).</summary>
public sealed class Library : IVersioned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public LibraryKind Kind { get; init; }

    public LibraryMode Mode { get; init; }

    /// <summary>Where a managed library's blobs are stored. Null for an external library.</summary>
    public Guid? StorageLocationId { get; init; }

    /// <summary>
    /// The folder an external library indexes, as an absolute path on the
    /// server (inside the container with Docker). Null for a managed library.
    /// </summary>
    public string? ExternalPath { get; init; }

    /// <summary>The user a personal library belongs to; null for every other kind. Unique.</summary>
    public Guid? OwnerUserId { get; init; }

    /// <summary>The admin who added a non-personal library. Null after that account is deleted.</summary>
    public Guid? CreatedByUserId { get; init; }

    /// <summary>The library's top folder. Every node of the library descends from it.</summary>
    public Guid RootNodeId { get; init; }

    /// <summary>
    /// Nothing can be changed through Atrium. Always true for an external
    /// library until an admin enables writes, after an explicit warning.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>When writes were last enabled on an external library, and by whom; for the admin screen.</summary>
    public DateTimeOffset? WritesEnabledAt { get; set; }

    public Guid? WritesEnabledByUserId { get; set; }

    public LibraryState State { get; set; }

    /// <summary>Error code saying why the library is offline.</summary>
    public string? StateReason { get; set; }

    public DateTimeOffset? LastScanAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public int Version { get; set; }
}

public enum LibraryAccess
{
    Read,

    /// <summary>May change files, where the library allows changes at all.</summary>
    Write,
}

/// <summary>Access of one user to a library that is not their personal one (DESIGN.md 6.3).</summary>
public sealed class LibraryGrant
{
    public Guid LibraryId { get; init; }

    public Guid UserId { get; init; }

    public LibraryAccess Access { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
