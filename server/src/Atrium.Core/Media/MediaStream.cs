namespace Atrium.Core.Media;

public enum MediaStreamType
{
    Video,
    Audio,
    Subtitle,
}

/// <summary>The dynamic range a video stream is mastered in (DESIGN.md 7.2).</summary>
public enum HdrFormat
{
    None,

    /// <summary>PQ transfer (SMPTE ST 2084) with static metadata. HDR10+ is read as HDR10, its base layer.</summary>
    Hdr10,

    /// <summary>Hybrid log-gamma (ARIB STD-B67).</summary>
    Hlg,

    /// <summary>Dolby Vision; <see cref="MediaStream.DolbyVisionProfile"/> and its compatible base layer say more.</summary>
    DolbyVision,
}

/// <summary>
/// One stream of a video file as ffprobe reported it, kept so a playback
/// decision needs no new probe (DESIGN.md 7.3). Kept with the file's facts
/// as JSON: it is read whole and never queried by its parts.
/// </summary>
/// <param name="Index">The stream's index in the file, as FFmpeg numbers it.</param>
/// <param name="Codec">FFmpeg's codec name: h264, hevc, av1, aac, eac3, subrip...</param>
/// <param name="Profile">FFmpeg's profile name: High, Main 10...</param>
/// <param name="Level">The codec's level as FFmpeg reports it: 41 for H.264 4.1, 120 for HEVC 4.</param>
/// <param name="DolbyVisionBaseLayer">What a player without Dolby Vision sees: HDR10, HLG, SDR, or none when the stream has no compatible base layer.</param>
public sealed record MediaStream(
    int Index,
    MediaStreamType Type,
    string? Codec,
    string? Profile = null,
    int? Level = null,
    int? BitDepth = null,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null,
    long? BitRate = null,
    HdrFormat Hdr = HdrFormat.None,
    int? DolbyVisionProfile = null,
    HdrFormat? DolbyVisionBaseLayer = null,
    int? Channels = null,
    string? Language = null,
    string? Title = null,
    bool IsDefault = false,
    bool IsForced = false);
