using System.Globalization;
using System.Text;
using Atrium.Core.Media;

namespace Atrium.Media.Streaming;

/// <summary>HLS playlists (RFC 8216) of a planned video: the whole list at once, so any segment can be asked for first.</summary>
public static class HlsPlaylist
{
    /// <summary>
    /// The media playlist: every segment with its length, the init segment
    /// first, and the end, so players treat it as a film and can seek
    /// anywhere. Segment times are on the file's clock; the playlist counts
    /// from the first.
    /// </summary>
    public static string Media(IReadOnlyList<Segment> segments, string initUri, Func<int, string> segmentUri)
    {
        var longest = segments.Count == 0 ? 1 : segments.Max(s => s.DurationUs);
        var text = new StringBuilder()
            .Append("#EXTM3U\n")
            .Append("#EXT-X-VERSION:7\n")
            .Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{(long)Math.Ceiling(longest / 1_000_000.0)}\n")
            .Append("#EXT-X-PLAYLIST-TYPE:VOD\n")
            .Append("#EXT-X-MEDIA-SEQUENCE:0\n")
            .Append("#EXT-X-INDEPENDENT-SEGMENTS\n")
            .Append(CultureInfo.InvariantCulture, $"#EXT-X-MAP:URI=\"{initUri}\"\n");
        foreach (var segment in segments)
        {
            text.Append(CultureInfo.InvariantCulture, $"#EXTINF:{segment.DurationUs / 1_000_000.0:0.000000},\n")
                .Append(segmentUri(segment.Index)).Append('\n');
        }

        return text.Append("#EXT-X-ENDLIST\n").ToString();
    }

    /// <summary>
    /// The master playlist with the one stream there is. Its codecs let a
    /// player refuse what it cannot decode before loading anything; Apple's
    /// players need them for HEVC.
    /// </summary>
    public static string Master(string mediaUri, long bandwidth, int? width, int? height, string? codecs)
    {
        var attributes = new StringBuilder().Append(CultureInfo.InvariantCulture, $"BANDWIDTH={Math.Max(1, bandwidth)}");
        if (width is { } w && height is { } h)
        {
            attributes.Append(CultureInfo.InvariantCulture, $",RESOLUTION={w}x{h}");
        }

        if (codecs is not null)
        {
            attributes.Append(CultureInfo.InvariantCulture, $",CODECS=\"{codecs}\"");
        }

        return $"#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-STREAM-INF:{attributes}\n{mediaUri}\n";
    }

    /// <summary>
    /// The codecs attribute (RFC 6381) for the streams sent, or null when a
    /// stream's codec has no known name, in which case players probe the
    /// segments themselves.
    /// </summary>
    public static string? Codecs(MediaStream video, MediaStream? audio)
    {
        var videoCodec = VideoCodec(video);
        var audioCodec = audio is null ? null : AudioCodec(audio.Codec);
        if (videoCodec is null || (audio is not null && audioCodec is null))
        {
            return null;
        }

        return audioCodec is null ? videoCodec : $"{videoCodec},{audioCodec}";
    }

    private static string? VideoCodec(MediaStream video)
    {
        switch (video.Codec)
        {
            case "h264":
                var profile = video.Profile?.ToLowerInvariant() switch
                {
                    "baseline" => "4200",
                    "constrained baseline" => "42E0",
                    "main" => "4D40",
                    "high" => "6400",
                    "high 10" => "6E00",
                    _ => null,
                };
                return profile is null || video.Level is not { } level
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"avc1.{profile}{level:X2}");
            case "hevc":
                var main10 = video.Profile?.Contains("10", StringComparison.Ordinal) == true;
                return video.Level is { } hevcLevel
                    ? string.Create(CultureInfo.InvariantCulture, $"hvc1.{(main10 ? "2.4" : "1.6")}.L{hevcLevel}.B0")
                    : null;
            case "av1":
                return video.Level is { } av1Level
                    ? string.Create(CultureInfo.InvariantCulture, $"av01.0.{av1Level:D2}M.{(video.BitDepth ?? 8):D2}")
                    : null;
            case "vp9":
                return string.Create(CultureInfo.InvariantCulture, $"vp09.00.40.{(video.BitDepth ?? 8):D2}");
            default:
                return null;
        }
    }

    private static string? AudioCodec(string? codec) => codec switch
    {
        "aac" => "mp4a.40.2",
        "mp3" => "mp4a.40.34",
        "ac3" => "ac-3",
        "eac3" => "ec-3",
        "opus" => "Opus",
        "flac" => "fLaC",
        "alac" => "alac",
        _ => null,
    };
}
