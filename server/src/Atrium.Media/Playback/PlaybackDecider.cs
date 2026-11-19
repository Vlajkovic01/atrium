using Atrium.Core.Media;

namespace Atrium.Media.Playback;

/// <summary>What a client can play itself (DESIGN.md 7.2). Codec names are FFmpeg's: h264, hevc, av1, vp9, aac, eac3, opus...</summary>
/// <param name="Containers">mp4, mov, mkv, webm, mpegts, avi...</param>
/// <param name="Hdr">The dynamic ranges the client shows as they are; SDR is always assumed.</param>
/// <param name="DolbyVisionProfiles">Dolby Vision profiles the client decodes; empty with <see cref="HdrFormat.DolbyVision"/> in <paramref name="Hdr"/> means any.</param>
/// <param name="Hls">Plays HLS with fMP4 segments, which every way of streaming but the original needs.</param>
/// <param name="MaxBitRate">The most the connection or the person allows, in bits per second.</param>
public sealed record ClientProfile(
    IReadOnlyList<string> Containers,
    IReadOnlyList<VideoCodecSupport> VideoCodecs,
    IReadOnlyList<AudioCodecSupport> AudioCodecs,
    IReadOnlyList<HdrFormat> Hdr,
    bool Hls,
    long? MaxBitRate = null,
    IReadOnlyList<int>? DolbyVisionProfiles = null);

/// <param name="Profiles">Profile names as FFmpeg writes them (High, Main 10); none means every profile.</param>
/// <param name="MaxLevel">The highest level as written in the codec's own terms: 5.1 for H.264 5.1 or HEVC 5.1.</param>
public sealed record VideoCodecSupport(
    string Codec,
    IReadOnlyList<string>? Profiles = null,
    double? MaxLevel = null,
    int? MaxBitDepth = null,
    int? MaxWidth = null,
    int? MaxHeight = null);

public sealed record AudioCodecSupport(string Codec, int? MaxChannels = null);

public enum PlaybackMethod
{
    /// <summary>The original file, with range requests.</summary>
    DirectPlay,

    /// <summary>Video and audio copied into HLS.</summary>
    Remux,

    /// <summary>Video copied, audio converted, in HLS.</summary>
    AudioTranscode,

    /// <summary>Video encoded again, in HLS.</summary>
    Transcode,
}

/// <summary>
/// The cheapest way to play a file on a client, and why anything more was
/// needed. Reasons are stable codes the clients translate
/// (<see cref="PlaybackReasons"/>).
/// </summary>
public sealed record PlaybackDecision(PlaybackMethod Method, IReadOnlyList<string> Reasons, int VideoStream, int? AudioStream);

/// <summary>Why a file is not played as it is. Clients show these; the codes never change meaning.</summary>
public static class PlaybackReasons
{
    public const string Container = "container";
    public const string VideoCodec = "video_codec";
    public const string VideoProfile = "video_profile";
    public const string VideoLevel = "video_level";
    public const string VideoBitDepth = "video_bit_depth";
    public const string VideoResolution = "video_resolution";
    public const string VideoHdr = "video_hdr";
    public const string VideoNotStreamable = "video_not_streamable";
    public const string AudioCodec = "audio_codec";
    public const string AudioChannels = "audio_channels";
    public const string AudioNotStreamable = "audio_not_streamable";
    public const string BitRate = "bit_rate";

    /// <summary>The video could be copied, but where its keyframes are is not known, so it is encoded again.</summary>
    public const string KeyframesUnknown = "keyframes_unknown";
}

/// <summary>
/// Compares a client's profile with a file's streams (DESIGN.md 7.3). Rule 8:
/// never more work than needed: the original when the client plays every
/// part of it, else a remux, else the audio alone converted, and only then
/// the video encoded again.
/// </summary>
public static class PlaybackDecider
{
    /// <summary>Video codecs HLS with fMP4 segments can carry, so a copy into HLS is possible.</summary>
    private static readonly HashSet<string> StreamableVideo = new(StringComparer.Ordinal) { "h264", "hevc", "av1", "vp9" };

    /// <summary>Audio codecs fMP4 segments can carry.</summary>
    private static readonly HashSet<string> StreamableAudio = new(StringComparer.Ordinal) { "aac", "mp3", "ac3", "eac3", "flac", "opus", "alac" };

    /// <returns>The decision, or null when the file has no video stream to play.</returns>
    public static PlaybackDecision? Decide(
        ClientProfile client, string container, IReadOnlyList<MediaStream> streams, long? fileBitRate, int? audioStreamIndex = null)
    {
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (video is null)
        {
            return null;
        }

        var audio = ChooseAudio(streams, audioStreamIndex);
        var videoProblems = VideoProblems(client, video);
        var audioProblems = audio is null ? [] : AudioProblems(client, audio);
        var bitRateTooHigh = client.MaxBitRate is { } max && (fileBitRate ?? TotalBitRate(streams)) is { } rate && rate > max;
        var containerPlays = client.Containers.Any(c => Same(c, container));

        if (videoProblems.Count == 0 && audioProblems.Count == 0 && !bitRateTooHigh && containerPlays)
        {
            return new PlaybackDecision(PlaybackMethod.DirectPlay, [], video.Index, audio?.Index);
        }

        var reasons = new List<string>();
        if (!containerPlays)
        {
            reasons.Add(PlaybackReasons.Container);
        }

        reasons.AddRange(videoProblems);
        reasons.AddRange(audioProblems);
        if (bitRateTooHigh)
        {
            reasons.Add(PlaybackReasons.BitRate);
        }

        // Copying the video needs a client that plays it, at a rate it takes,
        // and a codec HLS can carry.
        var videoCopies = videoProblems.Count == 0 && !bitRateTooHigh;
        if (videoCopies && !StreamableVideo.Contains(video.Codec ?? string.Empty))
        {
            reasons.Add(PlaybackReasons.VideoNotStreamable);
            videoCopies = false;
        }

        if (!videoCopies)
        {
            return new PlaybackDecision(PlaybackMethod.Transcode, reasons, video.Index, audio?.Index);
        }

        var audioCopies = audio is null || (audioProblems.Count == 0 && StreamableAudio.Contains(audio.Codec ?? string.Empty));
        if (!audioCopies && audioProblems.Count == 0)
        {
            reasons.Add(PlaybackReasons.AudioNotStreamable);
        }

        return new PlaybackDecision(
            audioCopies ? PlaybackMethod.Remux : PlaybackMethod.AudioTranscode, reasons, video.Index, audio?.Index);
    }

    /// <summary>
    /// The container as clients name it. ffprobe names families
    /// ("mov,mp4,m4a,3gp,3g2,mj2", "matroska,webm"), so the file's
    /// extension and codecs tell the members apart.
    /// </summary>
    public static string Container(string? format, string? fileName, IReadOnlyList<MediaStream> streams)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        var family = (format ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (family.Contains("matroska"))
        {
            // WebM is Matroska with only VP8, VP9 or AV1 and Opus or Vorbis.
            var webmCodecs = streams.All(s => s.Type == MediaStreamType.Subtitle
                || s.Codec is "vp8" or "vp9" or "av1" or "opus" or "vorbis" or "webvtt");
            return extension == "webm" && webmCodecs ? "webm" : "mkv";
        }

        if (family.Contains("mov"))
        {
            return extension == "mov" ? "mov" : "mp4";
        }

        return family.Length > 0 ? family[0] : extension;
    }

    private static MediaStream? ChooseAudio(IReadOnlyList<MediaStream> streams, int? index)
    {
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        return audio.FirstOrDefault(s => s.Index == index) ?? audio.FirstOrDefault(s => s.IsDefault) ?? audio.FirstOrDefault();
    }

    private static List<string> VideoProblems(ClientProfile client, MediaStream video)
    {
        var problems = new List<string>();
        var support = client.VideoCodecs.FirstOrDefault(c => Same(c.Codec, video.Codec));
        if (support is null)
        {
            problems.Add(PlaybackReasons.VideoCodec);
            return problems;
        }

        if (support.Profiles is { Count: > 0 } profiles && video.Profile is { } profile && !profiles.Any(p => Same(p, profile)))
        {
            problems.Add(PlaybackReasons.VideoProfile);
        }

        if (support.MaxLevel is { } maxLevel && Level(video.Codec, video.Level) is { } level && level > maxLevel + 0.001)
        {
            problems.Add(PlaybackReasons.VideoLevel);
        }

        if (support.MaxBitDepth is { } maxDepth && video.BitDepth is { } depth && depth > maxDepth)
        {
            problems.Add(PlaybackReasons.VideoBitDepth);
        }

        if (!Fits(video, support))
        {
            problems.Add(PlaybackReasons.VideoResolution);
        }

        if (!ShowsRange(client, video))
        {
            problems.Add(PlaybackReasons.VideoHdr);
        }

        return problems;
    }

    /// <summary>Whether the client shows the stream's dynamic range; Dolby Vision falls back to its base layer.</summary>
    private static bool ShowsRange(ClientProfile client, MediaStream video)
    {
        if (video.Hdr == HdrFormat.None)
        {
            return true;
        }

        if (video.Hdr == HdrFormat.DolbyVision)
        {
            var decodes = client.Hdr.Contains(HdrFormat.DolbyVision)
                && (client.DolbyVisionProfiles is not { Count: > 0 } dv || video.DolbyVisionProfile is not { } p || dv.Contains(p));
            return decodes || (video.DolbyVisionBaseLayer is { } baseLayer && (baseLayer == HdrFormat.None || client.Hdr.Contains(baseLayer)));
        }

        return client.Hdr.Contains(video.Hdr);
    }

    /// <summary>Portrait video fits a landscape limit turned: the longer sides and the shorter sides are compared.</summary>
    private static bool Fits(MediaStream video, VideoCodecSupport support)
    {
        if (video.Width is not { } w || video.Height is not { } h || (support.MaxWidth is null && support.MaxHeight is null))
        {
            return true;
        }

        var maxW = support.MaxWidth ?? int.MaxValue;
        var maxH = support.MaxHeight ?? int.MaxValue;
        return Math.Max(w, h) <= Math.Max(maxW, maxH) && Math.Min(w, h) <= Math.Min(maxW, maxH);
    }

    private static List<string> AudioProblems(ClientProfile client, MediaStream audio)
    {
        var support = client.AudioCodecs.FirstOrDefault(c => Same(c.Codec, audio.Codec));
        if (support is null)
        {
            return [PlaybackReasons.AudioCodec];
        }

        return support.MaxChannels is { } max && audio.Channels is { } channels && channels > max
            ? [PlaybackReasons.AudioChannels]
            : [];
    }

    /// <summary>
    /// A level as the codec writes it. FFmpeg stores H.264 levels times 10
    /// (41 is 4.1), HEVC levels times 30 (153 is 5.1), and AV1 levels as the
    /// index of the specification (8 is 4.0, 13 is 5.1).
    /// </summary>
    public static double? Level(string? codec, int? stored) => (codec, stored) switch
    {
        (_, null) => null,
        ("h264", { } v) => v / 10.0,
        ("hevc", { } v) => Math.Round(v / 30.0, 1),
        ("av1", { } v) => 2 + (v >> 2) + ((v & 3) / 10.0),
        _ => null,
    };

    private static long? TotalBitRate(IReadOnlyList<MediaStream> streams)
    {
        var rates = streams.Where(s => s.Type != MediaStreamType.Subtitle).Select(s => s.BitRate).ToList();
        return rates.Count > 0 && rates.All(r => r is not null) ? rates.Sum() : null;
    }

    /// <summary>Names compared without case, spaces or dashes: "Main 10", "main10" and "MAIN-10" are one profile.</summary>
    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Simple(a), Simple(b), StringComparison.Ordinal);

    private static string Simple(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
