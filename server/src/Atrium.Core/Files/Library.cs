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

/// <summary>A tree of files and folders on one storage location (DESIGN.md 2.2).</summary>
public sealed class Library : IVersioned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public LibraryKind Kind { get; init; }

    public LibraryMode Mode { get; init; }

    public Guid StorageLocationId { get; init; }

    /// <summary>The user a personal library belongs to; null for every other kind. Unique.</summary>
    public Guid? OwnerUserId { get; init; }

    /// <summary>The library's top folder. Every node of the library descends from it.</summary>
    public Guid RootNodeId { get; init; }

    public bool ReadOnly { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public int Version { get; set; }
}
