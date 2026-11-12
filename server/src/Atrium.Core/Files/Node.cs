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

    public int Version { get; set; }

    public bool IsFolder => Kind == NodeKind.Folder;
}
