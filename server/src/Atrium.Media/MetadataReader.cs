using System.Globalization;
using System.Text.Json;
using Atrium.Core.Files;
using Atrium.Core.Media;
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
    int? Orientation,
    long? BitRate = null,
    IReadOnlyList<MediaStream>? Streams = null);

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
            null,
            Long(format, "bit_rate"),
            streams.Select((stream, position) => ReadStream(stream, position)).OfType<MediaStream>().ToList());
    }

    /// <summary>
    /// One stream as a playback decision needs it (DESIGN.md 7.3). Cover
    /// pictures, data and attachment streams are left out.
    /// </summary>
    private static MediaStream? ReadStream(JsonElement stream, int position)
    {
        MediaStreamType? type = Str(stream, "codec_type") switch
        {
            "video" when !IsAttachedPicture(stream) => MediaStreamType.Video,
            "audio" => MediaStreamType.Audio,
            "subtitle" => MediaStreamType.Subtitle,
            _ => null,
        };
        if (type is null)
        {
            return null;
        }

        var tags = stream.TryGetProperty("tags", out var t) ? t : default;
        var disposition = stream.TryGetProperty("disposition", out var d) ? d : default;
        var (hdr, dvProfile, dvBase) = type == MediaStreamType.Video ? DynamicRange(stream) : (HdrFormat.None, null, null);
        return new MediaStream(
            Int(stream, "index") ?? position,
            type.Value,
            Trim(Str(stream, "codec_name"), 32),
            Trim(Str(stream, "profile"), 64),
            Int(stream, "level") is { } level && level >= 0 ? level : null,
            type == MediaStreamType.Video ? BitDepth(stream) : null,
            type == MediaStreamType.Video ? Int(stream, "width") : null,
            type == MediaStreamType.Video ? Int(stream, "height") : null,
            type == MediaStreamType.Video ? FrameRate(Str(stream, "avg_frame_rate")) ?? FrameRate(Str(stream, "r_frame_rate")) : null,
            Long(stream, "bit_rate") ?? Long(tags, "BPS"),
            hdr,
            dvProfile,
            dvBase,
            type == MediaStreamType.Audio ? Int(stream, "channels") : null,
            Trim(Tag(tags, "language"), 16) is { } language && language != "und" ? language : null,
            Trim(Tag(tags, "title"), 256),
            Flag(disposition, "default"),
            Flag(disposition, "forced"));
    }

    /// <summary>
    /// HDR10 and HLG are read from the transfer function. Dolby Vision from
    /// its configuration record, with the base layer a player without Dolby
    /// Vision shows: compatibility 1 and 6 are HDR10, 2 is SDR, 4 is HLG,
    /// 0 none (profile 5).
    /// </summary>
    private static (HdrFormat Hdr, int? Profile, HdrFormat? BaseLayer) DynamicRange(JsonElement stream)
    {
        var transfer = Str(stream, "color_transfer") switch
        {
            "smpte2084" => HdrFormat.Hdr10,
            "arib-std-b67" => HdrFormat.Hlg,
            _ => HdrFormat.None,
        };

        if (stream.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Str(item, "side_data_type") is { } kind && kind.StartsWith("DOVI configuration", StringComparison.OrdinalIgnoreCase))
                {
                    HdrFormat? baseLayer = Int(item, "dv_bl_signal_compatibility_id") switch
                    {
                        1 or 6 => HdrFormat.Hdr10,
                        2 => HdrFormat.None,
                        4 => HdrFormat.Hlg,
                        _ => null,
                    };
                    return (HdrFormat.DolbyVision, Int(item, "dv_profile"), baseLayer);
                }
            }
        }

        return (transfer, null, null);
    }

    /// <summary>Bits per sample from the stream, else from its pixel format (yuv420p10le, p010le...).</summary>
    private static int? BitDepth(JsonElement stream)
    {
        if (Int(stream, "bits_per_raw_sample") is { } bits and > 0)
        {
            return bits;
        }

        var pixels = Str(stream, "pix_fmt");
        if (pixels is null)
        {
            return null;
        }

        return pixels.Contains("12", StringComparison.Ordinal) ? 12
            : pixels.Contains("10", StringComparison.Ordinal) || pixels.StartsWith("p01", StringComparison.Ordinal) ? 10
            : 8;
    }

    private static double? FrameRate(string? fraction)
    {
        var parts = fraction?.Split('/');
        if (parts is not [var n, var d]
            || !double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || numerator <= 0 || denominator <= 0)
        {
            return null;
        }

        return Math.Round(numerator / denominator, 3);
    }

    /// <summary>A whole number ffprobe writes as a number or as text.</summary>
    private static long? Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static int? Int(JsonElement element, string name) =>
        Long(element, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static bool Flag(JsonElement disposition, string name) => Int(disposition, name) == 1;

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
