using System.Globalization;
using Atrium.Core.Media;
using Atrium.Media.Playback;

namespace Atrium.Media.Streaming;

/// <summary>What is sent of a video when it is streamed: each stream copied or encoded, and how (DESIGN.md 7.3, 7.4).</summary>
/// <param name="VideoBitRate">For encoded video, the bit rate aimed at, in bits per second.</param>
/// <param name="MaxHeight">For encoded video, the tallest picture sent; never larger than the original.</param>
/// <param name="ToneMap">HDR shown on a screen without HDR: mapped to SDR while encoding.</param>
public sealed record StreamOutput(
    int VideoStream,
    int? AudioStream,
    bool CopyVideo,
    bool CopyAudio,
    long VideoBitRate,
    int MaxHeight,
    bool ToneMap)
{
    /// <summary>Encoded sound: stereo AAC, which every client plays.</summary>
    public const long AudioBitRate = 192_000;

    /// <summary>
    /// The output for a decision. Encoded video aims at a size and bit rate
    /// by the tallest picture the bit rate carries well, never above the
    /// original or 1080 lines.
    /// </summary>
    public static StreamOutput For(PlaybackDecision decision, MediaStream video, ClientProfile client)
    {
        var copyVideo = decision.Method is PlaybackMethod.Remux or PlaybackMethod.AudioTranscode;
        var copyAudio = decision.Method == PlaybackMethod.Remux;
        var available = (client.MaxBitRate ?? long.MaxValue) - AudioBitRate;
        var sourceHeight = Math.Min(video.Width ?? int.MaxValue, video.Height ?? int.MaxValue) is var shorter and > 0 and < int.MaxValue
            ? shorter
            : 1080;
        (int Height, long BitRate)[] ladder = [(1080, 8_000_000), (720, 4_000_000), (480, 2_000_000), (360, 1_000_000)];
        var (height, bitRate) = ladder.FirstOrDefault(step => step.BitRate <= available && step.Height <= Math.Max(sourceHeight, 360));
        if (height == 0)
        {
            (height, bitRate) = (360, Math.Max(400_000, available));
        }

        var toneMap = video.Hdr != HdrFormat.None && decision.Reasons.Contains(PlaybackReasons.VideoHdr);
        return new StreamOutput(
            decision.VideoStream, decision.AudioStream, copyVideo, copyAudio, bitRate, Math.Min(height, sourceHeight), toneMap);
    }

    /// <summary>
    /// FFmpeg's arguments for producing segments from <paramref name="first"/>
    /// on, each a file of its own in <paramref name="directory"/>, named by
    /// its index, with a list of the finished ones in <c>segments.csv</c>.
    /// </summary>
    /// <param name="input">The source: a local path, or a loopback URL for remote storage.</param>
    /// <param name="segments">The whole plan; cuts are made at its boundaries after the first segment produced.</param>
    public IReadOnlyList<string> Arguments(string input, IReadOnlyList<Segment> segments, int first, string directory)
    {
        var start = segments[first];
        var args = new List<string> { "-nostdin", "-v", "error", "-y" };

        // Copied video starts exactly at the keyframe that opens the segment,
        // on the file's own clock (D-061). A hair later than the keyframe, so
        // a rounding never lands on the one before.
        args.AddRange(CopyVideo
            ? ["-seek_timestamp", "1", "-ss", Seconds(start.StartUs + SeekMarginUs)]
            : ["-ss", Seconds(start.StartUs)]);
        if (input.StartsWith("http://", StringComparison.Ordinal) || input.StartsWith("https://", StringComparison.Ordinal))
        {
            // Remote storage through the server (D-063): a dropped connection is picked up again.
            args.AddRange(["-reconnect", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "5"]);
        }

        args.AddRange(["-i", input, "-map", $"0:{VideoStream.ToString(CultureInfo.InvariantCulture)}"]);
        if (AudioStream is { } audio)
        {
            args.AddRange(["-map", $"0:{audio.ToString(CultureInfo.InvariantCulture)}"]);
        }

        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn"]);
        if (CopyVideo)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            var filters = new List<string>();
            if (ToneMap)
            {
                filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
            }

            filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale=-2:'min({MaxHeight},ih)'"));
            filters.Add("format=yuv420p");
            args.AddRange([
                "-vf", string.Join(',', filters),
                "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high",
                "-b:v", VideoBitRate.ToString(CultureInfo.InvariantCulture),
                "-maxrate", VideoBitRate.ToString(CultureInfo.InvariantCulture),
                "-bufsize", (VideoBitRate * 2).ToString(CultureInfo.InvariantCulture),

                // A keyframe opens every planned segment; the run starts on one.
                "-force_key_frames", string.Create(CultureInfo.InvariantCulture, $"expr:gte(t,n_forced*{SegmentPlan.TargetUs / 1_000_000})"),
            ]);
        }

        if (AudioStream is not null)
        {
            args.AddRange(CopyAudio
                ? ["-c:a", "copy"]
                : ["-c:a", "aac", "-ac", "2", "-b:a", AudioBitRate.ToString(CultureInfo.InvariantCulture)]);
        }

        // Cut times are counted from the start of this run, a millisecond
        // early, so the keyframe that opens each segment always counts as
        // reaching it.
        var cuts = segments.Skip(first + 1)
            .Select(s => Seconds(Math.Max(0, s.StartUs - start.StartUs - CutMarginUs)))
            .ToList();
        args.AddRange(["-f", "segment"]);
        if (cuts.Count > 0)
        {
            args.AddRange(["-segment_times", string.Join(',', cuts)]);
        }

        args.AddRange([
            "-segment_format", "mp4",
            "-segment_format_options", "movflags=+frag_keyframe+empty_moov+default_base_moof",
            "-segment_start_number", first.ToString(CultureInfo.InvariantCulture),
            "-reset_timestamps", "1",
            "-segment_list", Path.Combine(directory, "segments.csv"),
            "-segment_list_type", "csv",
            Path.Combine(directory, "%d.mp4"),
        ]);
        return args;
    }

    private const long SeekMarginUs = 500;

    private const long CutMarginUs = 1_000;

    private static string Seconds(long microseconds) =>
        (microseconds / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture);
}
