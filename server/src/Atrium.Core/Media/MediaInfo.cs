using Atrium.Core.Files;

namespace Atrium.Core.Media;

public enum ThumbnailState
{
    /// <summary>Not made yet.</summary>
    Pending,
    Ready,

    /// <summary>Making it failed; retried only when the source changes or an admin asks.</summary>
    Failed,

    /// <summary>The format cannot be decoded on this server.</summary>
    Unsupported,
}

/// <summary>
/// Technical facts read from a photo or video (EXIF, ffprobe). Belongs to
/// the content: a blob in a managed library, so duplicates share it, or a
/// node in an external library, together with the size and modification
/// time it was read from. No location is read (D-031).
/// </summary>
public sealed class MediaInfo
{
    /// <summary>Bump when extraction learns something new, so older rows are read again.</summary>
    public const int CurrentExtractorVersion = 1;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The content in a managed library. Exactly one of this and <see cref="NodeId"/> is set.</summary>
    public Guid? BlobId { get; init; }

    /// <summary>The file in an external library.</summary>
    public Guid? NodeId { get; init; }

    /// <summary>Size of the content this was read from; for an external file a change means it must be read again.</summary>
    public long SourceSize { get; set; }

    public DateTimeOffset SourceModifiedAt { get; set; }

    public MediaKind Kind { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public long? DurationMs { get; set; }

    /// <summary>Container or format, for example <c>jpeg</c>, <c>heif</c>, <c>mov,mp4</c>.</summary>
    public string? Format { get; set; }

    public string? VideoCodec { get; set; }

    public string? AudioCodec { get; set; }

    /// <summary>Local clock time the photo or video was taken, with a zero offset (D-034).</summary>
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>The offset from UTC the camera recorded, when it did.</summary>
    public int? CapturedOffsetMinutes { get; set; }

    public string? CameraMake { get; set; }

    public string? CameraModel { get; set; }

    /// <summary>EXIF orientation 1 to 8; thumbnails are already turned upright.</summary>
    public int? Orientation { get; set; }

    public ThumbnailState ThumbnailState { get; set; }

    /// <summary>Error code of the last failed extraction or thumbnail.</summary>
    public string? ErrorCode { get; set; }

    public int ExtractorVersion { get; set; }

    public DateTimeOffset ExtractedAt { get; set; }
}

/// <summary>A user's own collection of photos and videos. Items are references; nothing is copied.</summary>
public sealed class Album : IVersioned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid OwnerUserId { get; init; }

    public required string Name { get; set; }

    /// <summary>The item shown on the album; the newest item when null or no longer readable.</summary>
    public Guid? CoverNodeId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }
}

public sealed class AlbumItem
{
    public Guid AlbumId { get; init; }

    public Guid NodeId { get; init; }

    public DateTimeOffset AddedAt { get; init; }
}
