using System.Buffers.Binary;
using Atrium.Core.Media;
using Atrium.Media.Playback;
using Atrium.Media.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atrium.Media.Tests;

public sealed class StreamSessionsTests : IAsyncDisposable
{
    private static readonly MediaStream Video = new(0, MediaStreamType.Video, "h264", "High", 40, 8, 1920, 1080);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _time = new();
    private readonly StreamSessions _sessions;

    public StreamSessionsTests() =>
        _sessions = new StreamSessions(new MediaOptions { CacheDir = _dir }, new StreamLimits(1, 2, 2), _time, NullLoggerFactory.Instance);

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<(StreamSession? Session, StreamRefusal? Refusal)> OpenAsync(Guid user, bool encode)
    {
        var decision = new PlaybackDecision(encode ? PlaybackMethod.Transcode : PlaybackMethod.Remux, [], 0, null);
        var output = new StreamOutput(0, null, !encode, false, 4_000_000, 720, false);
        return _sessions.OpenAsync(user, Guid.NewGuid(), Guid.NewGuid(), decision, output, SegmentPlan.Fixed(60_000_000, 6_000_000), Video, null, 0, _ => Task.FromResult("unused"));
    }

    [Fact]
    public async Task Encoding_and_copying_have_their_own_limits_on_the_server()
    {
        Assert.NotNull((await OpenAsync(Guid.NewGuid(), encode: true)).Session);
        Assert.Equal(StreamRefusal.ServerBusy, (await OpenAsync(Guid.NewGuid(), encode: true)).Refusal);

        Assert.NotNull((await OpenAsync(Guid.NewGuid(), encode: false)).Session);
        Assert.NotNull((await OpenAsync(Guid.NewGuid(), encode: false)).Session);
        Assert.Equal(StreamRefusal.ServerBusy, (await OpenAsync(Guid.NewGuid(), encode: false)).Refusal);
    }

    [Fact]
    public async Task One_person_has_a_few_streams_and_an_idle_one_makes_room()
    {
        var ana = Guid.NewGuid();
        var first = (await OpenAsync(ana, encode: false)).Session!;
        _time.Advance(TimeSpan.FromSeconds(1));
        await OpenAsync(ana, encode: false);

        Assert.Equal(StreamRefusal.TooManyForUser, (await OpenAsync(ana, encode: false)).Refusal);

        // A player that went away without closing its stream.
        _time.Advance(StreamSessions.IdleStop);
        Assert.NotNull((await OpenAsync(ana, encode: true)).Session);
        Assert.Null(_sessions.Find(first.Id));
    }

    [Fact]
    public async Task Long_idle_streams_are_forgotten_with_their_folder_and_identifiers_cannot_be_guessed()
    {
        var a = (await OpenAsync(Guid.NewGuid(), encode: false)).Session!;
        var b = (await OpenAsync(Guid.NewGuid(), encode: false)).Session!;
        Assert.True(Directory.Exists(a.Directory));
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(24, a.Id.Length);

        _time.Advance(StreamSessions.IdleEnd);
        await _sessions.SweepAsync();

        Assert.Empty(_sessions.All);
        Assert.False(Directory.Exists(a.Directory));
    }

    [Fact]
    public void Folders_left_by_an_earlier_run_of_the_server_are_removed()
    {
        var left = Path.Combine(_sessions.Directory, "old-stream");
        Directory.CreateDirectory(left);

        _sessions.RemoveLeftovers();

        Assert.False(Directory.Exists(left));
    }
}

/// <summary>A clock the test moves.</summary>
public sealed class Clock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class StreamOutputTests
{
    private static readonly MediaStream FullHd = new(0, MediaStreamType.Video, "hevc", "Main 10", 153, 10, 1920, 1080, Hdr: HdrFormat.Hdr10);

    private static ClientProfile Client(long? maxBitRate) => new(["mp4"], [new VideoCodecSupport("h264")], [new AudioCodecSupport("aac")], [], true, maxBitRate);

    [Theory]
    [InlineData(null, 1080, 8_000_000)]
    [InlineData(5_000_000L, 720, 4_000_000)]
    [InlineData(2_500_000L, 480, 2_000_000)]
    [InlineData(600_000L, 360, 408_000)]
    public void Encoded_video_takes_the_largest_size_the_bit_rate_carries(long? maxBitRate, int height, long bitRate)
    {
        var decision = new PlaybackDecision(PlaybackMethod.Transcode, [PlaybackReasons.VideoCodec, PlaybackReasons.VideoHdr], 0, 1);

        var output = StreamOutput.For(decision, FullHd, Client(maxBitRate));

        Assert.Equal((height, bitRate), (output.MaxHeight, output.VideoBitRate));
        Assert.True(output.ToneMap);
        Assert.False(output.CopyVideo || output.CopyAudio);
    }

    [Fact]
    public void A_small_video_is_never_made_larger()
    {
        var small = FullHd with { Width = 640, Height = 360, Hdr = HdrFormat.None };

        var output = StreamOutput.For(new PlaybackDecision(PlaybackMethod.Transcode, [PlaybackReasons.VideoCodec], 0, null), small, Client(null));

        Assert.Equal(360, output.MaxHeight);
        Assert.False(output.ToneMap);
    }

    [Fact]
    public void Copied_video_starts_on_its_keyframe_and_is_cut_at_the_planned_boundaries()
    {
        var output = new StreamOutput(0, 2, CopyVideo: true, CopyAudio: false, 0, 1080, false);
        Segment[] plan = [new(0, 1_400_000, 7_000_000), new(1, 8_400_000, 6_500_000), new(2, 14_900_000, 3_000_000)];

        var args = output.Arguments("in.mkv", plan, 1, "out").ToList();

        Assert.Equal(["-seek_timestamp", "1", "-ss", "8.400500"], args.Skip(4).Take(4));
        Assert.Equal("6.499000", args[args.IndexOf("-segment_times") + 1]);
        Assert.Equal("1", args[args.IndexOf("-segment_start_number") + 1]);
        Assert.Contains("copy", args);
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
    }
}

public sealed class HlsTests
{
    [Theory]
    [InlineData("h264", "High", 41, 8, "avc1.640029")]
    [InlineData("h264", "Constrained Baseline", 30, 8, "avc1.42E01E")]
    [InlineData("hevc", "Main 10", 153, 10, "hvc1.2.4.L153.B0")]
    [InlineData("hevc", "Main", 120, 8, "hvc1.1.6.L120.B0")]
    [InlineData("av1", null, 8, 10, "av01.0.08M.10")]
    public void Codec_names_follow_RFC_6381(string codec, string? profile, int level, int depth, string expected)
    {
        var video = new MediaStream(0, MediaStreamType.Video, codec, profile, level, depth);

        Assert.Equal(expected, HlsPlaylist.Codecs(video, null));
        Assert.Equal($"{expected},ec-3", HlsPlaylist.Codecs(video, new MediaStream(1, MediaStreamType.Audio, "eac3")));
    }

    [Fact]
    public void Unknown_codecs_are_left_for_the_player_to_find()
    {
        Assert.Null(HlsPlaylist.Codecs(new MediaStream(0, MediaStreamType.Video, "mpeg2video"), null));
        Assert.Null(HlsPlaylist.Codecs(new MediaStream(0, MediaStreamType.Video, "h264", "High", 41), new MediaStream(1, MediaStreamType.Audio, "dts")));
    }

    [Fact]
    public void The_media_playlist_lists_every_segment_and_ends()
    {
        var text = HlsPlaylist.Media([new(0, 0, 6_000_000), new(1, 6_000_000, 6_500_000), new(2, 12_500_000, 1_250_000)], "init.mp4", n => $"{n}.m4s");

        Assert.Equal(
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:7\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-INDEPENDENT-SEGMENTS\n" +
            "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.000000,\n0.m4s\n#EXTINF:6.500000,\n1.m4s\n#EXTINF:1.250000,\n2.m4s\n#EXT-X-ENDLIST\n",
            text);
    }

    [Fact]
    public void Fragments_are_moved_in_each_tracks_own_timescale_and_the_header_is_left_out()
    {
        // Track 1 at 90 kHz, track 2 at 48 kHz.
        var init = Box("ftyp", [0, 0, 0, 0]).Concat(Box("moov", [.. Trak(1, 90_000), .. Trak(2, 48_000)])).ToArray();
        var moof = Box("moof", [.. Box("mfhd", new byte[8]), .. Traf(1, 10), .. Traf(2, 20)]);
        var file = init.Concat(moof).Concat(Box("mdat", [9, 9])).ToArray();

        var media = Fmp4.Media(file, Fmp4.Timescales(Fmp4.Init(file)), offsetUs: 2_000_000);

        Assert.Equal("moof", System.Text.Encoding.ASCII.GetString(media, 4, 4));
        Assert.Equal(moof.Length + 10, media.Length);
        Assert.Equal([10UL + 180_000, 20UL + 96_000], DecodeTimes(media));
    }

    private static List<ulong> DecodeTimes(byte[] media)
    {
        var times = new List<ulong>();
        for (var i = 0; i + 8 <= media.Length; i++)
        {
            if (media[i] == (byte)'t' && media[i + 1] == (byte)'f' && media[i + 2] == (byte)'d' && media[i + 3] == (byte)'t')
            {
                times.Add(BinaryPrimitives.ReadUInt64BigEndian(media.AsSpan(i + 8)));
            }
        }

        return times;
    }

    private static byte[] Trak(uint track, uint timescale)
    {
        var tkhd = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(12), track);
        var mdhd = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(mdhd.AsSpan(12), timescale);
        return Box("trak", [.. Box("tkhd", tkhd), .. Box("mdia", Box("mdhd", mdhd))]);
    }

    private static byte[] Traf(uint track, ulong time)
    {
        var tfhd = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tfhd.AsSpan(4), track);
        var tfdt = new byte[12];
        tfdt[0] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(tfdt.AsSpan(4), time);
        return Box("traf", [.. Box("tfhd", tfhd), .. Box("tfdt", tfdt)]);
    }

    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }
}
