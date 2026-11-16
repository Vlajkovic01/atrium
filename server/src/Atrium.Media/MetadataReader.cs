using System.Globalization;
using System.Text.Json;
using Atrium.Core.Files;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Atrium.Media;

/// <summary>What was read from a photo or video. No location is ever read (D-031).</summary>
public sealed record MediaFacts(
    MediaKind Kind,
    int? Width,
    int? Height,
    long? DurationMs,
    string? Format,
    string? VideoCodec,
    string? AudioCodec,
    DateTime? CapturedLocal,
    int? OffsetMinutes,
    DateTimeOffset? CapturedInstant,
    string? CameraMake,
    string? CameraModel,
    int? Orientation);

/// <summary>Reads technical facts: EXIF and XMP for photos, ffprobe for videos.</summary>
public sealed class MetadataReader(MediaOptions options)
{
    /// <summary>Largest ffprobe answer accepted; a normal one is a few kilobytes.</summary>
    private const long MaxProbeOutput = 4 * 1024 * 1024;

    /// <summary>EXIF OffsetTimeOriginal (0x9011): the camera's offset from UTC when the photo was taken.</summary>
    private const int OffsetTimeOriginalTag = 0x9011;

    public async Task<MediaFacts> ReadAsync(string path, MediaKind kind, bool background, CancellationToken ct)
    {
        return kind == MediaKind.Video
            ? await ReadVideoAsync(path, background, ct)
            : ReadPhoto(path);
    }

    /// <summary>
    /// EXIF from the file. Dimensions come from the image header when EXIF
    /// lacks them; they are as stored, before orientation is applied.
    /// </summary>
    public static MediaFacts ReadPhoto(string path)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            // Shared with other programs, which may be writing the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
            directories = ImageMetadataReader.ReadMetadata(stream);
        }
        catch (Exception e) when (e is ImageProcessingException or IOException)
        {
            directories = [];
        }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

        DateTime? captured = null;
        if (sub is not null && (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original)
            || sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out original)))
        {
            captured = DateTime.SpecifyKind(original, DateTimeKind.Unspecified);
        }

        int? offset = ParseOffset(sub?.GetString(OffsetTimeOriginalTag));
        int? orientation = ifd0 is not null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 1 and <= 8 ? o : null;

        var (width, height) = Dimensions(directories);
        var format = directories.OfType<MetadataExtractor.Formats.FileType.FileTypeDirectory>().FirstOrDefault()
            ?.GetString(MetadataExtractor.Formats.FileType.FileTypeDirectory.TagDetectedFileTypeName)?.ToLowerInvariant();

        return new MediaFacts(
            MediaKind.Photo,
            width,
            height,
            null,
            Trim(format, 64),
            null,
            null,
            captured,
            offset,
            null,
            Trim(ifd0?.GetDescription(ExifDirectoryBase.TagMake), 128),
            Trim(ifd0?.GetDescription(ExifDirectoryBase.TagModel), 128),
            orientation);
    }

    private async Task<MediaFacts> ReadVideoAsync(string path, bool background, CancellationToken ct)
    {
        var json = await ToolRunner.RunAsync(
            options.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "--", path],
            options.ToolTimeout,
            background,
            MaxProbeOutput,
            ct);
        return ParseProbe(json);
    }

    /// <summary>Reads ffprobe's JSON. Location tags are present in many phone videos and are ignored on purpose.</summary>
    public static MediaFacts ParseProbe(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = root.TryGetProperty("streams", out var s) && s.ValueKind == JsonValueKind.Array ? s.EnumerateArray().ToList() : [];
        var video = streams.FirstOrDefault(x => Str(x, "codec_type") == "video" && !IsAttachedPicture(x));
        var audio = streams.FirstOrDefault(x => Str(x, "codec_type") == "audio");

        int? width = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("width", out var w) ? w.GetInt32() : null;
        int? height = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("height", out var h) ? h.GetInt32() : null;

        // A phone holding the camera upright records a rotation; the picture is shown turned.
        if (Rotation(video) is 90 or 270 or -90 or -270 && width is not null && height is not null)
        {
            (width, height) = (height, width);
        }

        long? duration = format.ValueKind == JsonValueKind.Object
            && double.TryParse(Str(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? (long)(seconds * 1000)
            : null;

        // Apple writes the local time with its offset; everyone else an instant in UTC.
        DateTime? local = null;
        int? offset = null;
        DateTimeOffset? instant = null;
        var tags = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("tags", out var t) ? t : default;
        if (Tag(tags, "com.apple.quicktime.creationdate") is { } apple
            && DateTimeOffset.TryParse(apple, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            local = withOffset.DateTime;
            offset = (int)withOffset.Offset.TotalMinutes;
        }
        else if (Tag(tags, "creation_time") is { } created
            && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            && utc.Year > 1970)
        {
            instant = utc;
        }

        return new MediaFacts(
            MediaKind.Video,
            width,
            height,
            duration,
            Trim(format.ValueKind == JsonValueKind.Object ? Str(format, "format_name") : null, 64),
            Trim(video.ValueKind == JsonValueKind.Object ? Str(video, "codec_name") : null, 64),
            Trim(audio.ValueKind == JsonValueKind.Object ? Str(audio, "codec_name") : null, 64),
            local,
            offset,
            instant,
            Trim(Tag(tags, "com.apple.quicktime.make") ?? Tag(tags, "make"), 128),
            Trim(Tag(tags, "com.apple.quicktime.model") ?? Tag(tags, "model"), 128),
            null);
    }

    /// <summary>
    /// The local clock time a photo was taken (D-034): from the file when it
    /// says, else from a recorded instant or the modification time, read in
    /// the server's zone.
    /// </summary>
    public DateTimeOffset CapturedAt(MediaFacts facts, DateTimeOffset modifiedAt)
    {
        if (facts.CapturedLocal is { } local && local.Year >= 1900)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        var instant = facts.CapturedInstant ?? modifiedAt;
        var inZone = TimeZoneInfo.ConvertTime(instant, options.TimeZone);
        return new DateTimeOffset(inZone.DateTime, TimeSpan.Zero);
    }

    private static (int? Width, int? Height) Dimensions(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        foreach (var directory in directories)
        {
            foreach (var (wTag, hTag) in new[]
            {
                (ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight),
                (ExifDirectoryBase.TagImageWidth, ExifDirectoryBase.TagImageHeight),
            })
            {
                if (directory is ExifDirectoryBase && directory.TryGetInt32(wTag, out var w) && directory.TryGetInt32(hTag, out var h) && w > 0 && h > 0)
                {
                    return (w, h);
                }
            }
        }

        // PNG, WebP, GIF, BMP, HEIF and JPEG headers each have their own directory.
        foreach (var directory in directories)
        {
            int? width = null;
            int? height = null;
            foreach (var tag in directory.Tags)
            {
                if (tag.Name is "Image Width" or "Width" && directory.TryGetInt32(tag.Type, out var w) && w > 0)
                {
                    width ??= w;
                }
                else if (tag.Name is "Image Height" or "Height" && directory.TryGetInt32(tag.Type, out var h) && h > 0)
                {
                    height ??= h;
                }
            }

            if (width is not null && height is not null)
            {
                return (width, height);
            }
        }

        return (null, null);
    }

    private static int? ParseOffset(string? value)
    {
        // "+02:00" or "-05:30"
        if (value is { Length: 6 } && (value[0] is '+' or '-')
            && int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(value.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            var total = (hours * 60) + minutes;
            return value[0] == '-' ? -total : total;
        }

        return null;
    }

    private static int? Rotation(JsonElement stream)
    {
        if (stream.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (stream.TryGetProperty("tags", out var tags) && Tag(tags, "rotate") is { } rotate
            && int.TryParse(rotate, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r))
        {
            return r;
        }

        if (stream.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in side.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) && rotation.TryGetInt32(out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Tag(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in tags.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim().Replace("\0", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
