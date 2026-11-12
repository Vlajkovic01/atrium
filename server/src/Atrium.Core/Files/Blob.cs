using System.Globalization;

namespace Atrium.Core.Files;

public enum BlobState
{
    Stored,

    /// <summary>No node or version referenced it at the last collection; deleted after a grace period (DESIGN.md 4.3).</summary>
    PendingDelete,
}

/// <summary>One stored object in a managed library, named by the SHA-256 of its content.</summary>
public sealed class Blob
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid StorageLocationId { get; init; }

    /// <summary>SHA-256 of the content (D-014). Unique per storage location.</summary>
    public required byte[] Sha256 { get; init; }

    public long Size { get; init; }

    public BlobState State { get; set; }

    public DateTimeOffset? PendingDeleteSince { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The storage key: <c>atrium/blobs/ab/cd/abcd…</c>, so no directory grows too large.</summary>
    public string Key => KeyFor(Sha256);

    public static string KeyFor(byte[] sha256)
    {
        var hex = Convert.ToHexStringLower(sha256);
        return string.Create(CultureInfo.InvariantCulture, $"atrium/blobs/{hex[..2]}/{hex[2..4]}/{hex}");
    }
}

/// <summary>Earlier content of a file, kept when it was replaced (D-019).</summary>
public sealed class NodeVersion
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid NodeId { get; init; }

    public Guid BlobId { get; init; }

    public long Size { get; init; }

    /// <summary>The modification time the content had while it was current.</summary>
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>When it stopped being the current content.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Who replaced it. Null after that account is deleted.</summary>
    public Guid? CreatedByUserId { get; init; }
}
