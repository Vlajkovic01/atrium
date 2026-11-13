namespace Atrium.Core.Files;

public enum NodeKind
{
    Folder,
    File,
}

/// <summary>
/// A file or folder. In a managed library a file points at a blob; its name
/// and place in the tree exist only here, so renaming and moving never touch
/// storage (D-002).
/// </summary>
public sealed class Node : IVersioned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid LibraryId { get; init; }

    /// <summary>Null only for a library's root folder.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>The name as given, NFC-normalized. See <see cref="NodeName"/>.</summary>
    public required string Name { get; set; }

    /// <summary>Case-folded <see cref="Name"/>, for comparing siblings (D-011).</summary>
    public required string NameKey { get; set; }

    /// <summary>
    /// <see cref="NameKey"/> while the node is not in the trash, null while
    /// it is. Unique together with the parent, so the database itself keeps
    /// two siblings from sharing a name, and a trashed node does not block
    /// its name.
    /// </summary>
    public string? ActiveNameKey { get; set; }

    public NodeKind Kind { get; init; }

    /// <summary>The content of a file in a managed library.</summary>
    public Guid? BlobId { get; set; }

    /// <summary>Size of a file's current content in bytes; 0 for a folder.</summary>
    public long Size { get; set; }

    /// <summary>Content type used when serving the file, derived from the name.</summary>
    public string? MediaType { get; set; }

    /// <summary>Modification time reported by the client, or the upload time.</summary>
    public DateTimeOffset ModifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the name, place or content last changed in Atrium.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who created it. Null after that account is deleted.</summary>
    public Guid? CreatedByUserId { get; init; }

    /// <summary>
    /// Set on the node the user deleted. Its descendants are not marked: they
    /// are in the trash because an ancestor is. The parent is kept, so a
    /// restore knows where the node came from.
    /// </summary>
    public DateTimeOffset? TrashedAt { get; set; }

    /// <summary>
    /// True for a trashed node and everything below it, so lists across a
    /// whole library (timeline, search) can leave the trash out without
    /// walking up the tree. Maintained with <see cref="TrashedAt"/>.
    /// </summary>
    public bool InTrash { get; set; }

    /// <summary>
    /// In an external library, the path relative to the library's folder,
    /// with forward slashes and the names exactly as on disk. Null in a
    /// managed library and for the root.
    /// </summary>
    public string? ExternalPath { get; set; }

    /// <summary>SHA-256 of <see cref="ExternalPath"/>, unique per library; a path can be too long to index itself.</summary>
    public byte[]? ExternalPathHash { get; set; }

    /// <summary>
    /// When a scan last found an external file gone. The node is kept, with
    /// its albums and metadata, until the retention ends: the folder may be
    /// on a share that is down for a day (DESIGN.md 4.4).
    /// </summary>
    public DateTimeOffset? MissingSince { get; set; }

    /// <summary>Photo or video, from the media type; null for anything else. Set when the node is saved.</summary>
    public MediaKind? MediaKind { get; set; }

    /// <summary>
    /// When a photo or video was taken, as the local clock time where it was
    /// taken, stored with a zero offset (D-034). The modification time until
    /// metadata is read. Orders the timeline.
    /// </summary>
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>Case-folded name without diacritics, for search. Set when the node is saved.</summary>
    public string SearchKey { get; set; } = string.Empty;

    public int Version { get; set; }

    public bool IsFolder => Kind == NodeKind.Folder;
}

/// <summary>What the photo timeline shows.</summary>
public enum MediaKind
{
    Photo,
    Video,
}
