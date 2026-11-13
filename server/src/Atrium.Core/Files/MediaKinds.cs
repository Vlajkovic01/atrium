namespace Atrium.Core.Files;

public static class MediaKinds
{
    /// <summary>
    /// Photo or video by media type. Vector images and icons are not
    /// photos: they belong in Files, not on the timeline.
    /// </summary>
    public static MediaKind? FromMediaType(string? mediaType)
    {
        if (mediaType is null)
        {
            return null;
        }

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Video;
        }

        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("image/x-icon", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("image/vnd.microsoft.icon", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Photo;
        }

        return null;
    }
}
