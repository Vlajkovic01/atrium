using Atrium.Core.Media;
using Atrium.Media.Playback;

namespace Atrium.Media.Tests;

public sealed class PlaybackDeciderTests
{
    private static readonly MediaStream H264 = new(0, MediaStreamType.Video, "h264", "High", 41, 8, 1920, 1080, 23.976, 8_000_000);
    private static readonly MediaStream Aac = new(1, MediaStreamType.Audio, "aac", "LC", Channels: 2, Language: "eng", IsDefault: true);

    /// <summary>A browser that plays MP4 with H.264 and AAC, through HLS too, without HDR.</summary>
    private static ClientProfile Browser(long? maxBitRate = null) => new(
        ["mp4", "mov", "webm"],
        [new VideoCodecSupport("h264", ["Constrained Baseline", "Main", "High"], 5.1, 8, 3840, 2160), new VideoCodecSupport("vp9")],
        [new AudioCodecSupport("aac", 6), new AudioCodecSupport("mp3"), new AudioCodecSupport("opus")],
        [],
        Hls: true,
        maxBitRate);

    /// <summary>A phone that plays MKV and HEVC up to 10 bits with HDR10 and HLG.</summary>
    private static readonly ClientProfile Phone = new(
        ["mp4", "mkv", "webm"],
        [new VideoCodecSupport("h264", MaxLevel: 5.2), new VideoCodecSupport("hevc", ["Main", "Main 10"], 5.1, 10, 3840, 2160)],
        [new AudioCodecSupport("aac", 8), new AudioCodecSupport("ac3", 6), new AudioCodecSupport("eac3", 8), new AudioCodecSupport("opus")],
        [HdrFormat.Hdr10, HdrFormat.Hlg],
        Hls: true);

    [Fact]
    public void The_original_is_played_when_the_client_plays_every_part()
    {
        var decision = PlaybackDecider.Decide(Browser(), "mp4", [H264, Aac], 8_200_000)!;

        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Empty(decision.Reasons);
        Assert.Equal((0, 1), (decision.VideoStream, decision.AudioStream));
    }

    [Fact]
    public void A_container_the_client_cannot_open_is_remuxed_not_transcoded()
    {
        var decision = PlaybackDecider.Decide(Browser(), "mkv", [H264, Aac], null)!;

        Assert.Equal(PlaybackMethod.Remux, decision.Method);
        Assert.Equal([PlaybackReasons.Container], decision.Reasons);
    }

    [Fact]
    public void Only_the_audio_is_converted_when_only_the_audio_does_not_play()
    {
        var dts = new MediaStream(1, MediaStreamType.Audio, "dts", Channels: 6);

        var decision = PlaybackDecider.Decide(Browser(), "mkv", [H264, dts], null)!;

        Assert.Equal(PlaybackMethod.AudioTranscode, decision.Method);
        Assert.Equal([PlaybackReasons.Container, PlaybackReasons.AudioCodec], decision.Reasons);
    }

    [Fact]
    public void Too_many_audio_channels_convert_the_audio()
    {
        var surround = Aac with { Channels = 8 };

        var decision = PlaybackDecider.Decide(Browser(), "mp4", [H264, surround], null)!;

        Assert.Equal(PlaybackMethod.AudioTranscode, decision.Method);
        Assert.Equal([PlaybackReasons.AudioChannels], decision.Reasons);
    }

    [Fact]
    public void Audio_the_client_plays_but_HLS_cannot_carry_is_converted_with_its_own_reason()
    {
        var client = Browser() with { AudioCodecs = [new AudioCodecSupport("vorbis")] };
        var vorbis = new MediaStream(1, MediaStreamType.Audio, "vorbis", Channels: 2);

        var decision = PlaybackDecider.Decide(client, "mkv", [H264, vorbis], null)!;

        Assert.Equal(PlaybackMethod.AudioTranscode, decision.Method);
        Assert.Equal([PlaybackReasons.Container, PlaybackReasons.AudioNotStreamable], decision.Reasons);
    }

    public static TheoryData<MediaStream, string> VideoTheClientCannotPlay => new()
    {
        { H264 with { Codec = "hevc", Profile = "Main" }, PlaybackReasons.VideoCodec },
        { H264 with { Profile = "High 4:4:4 Predictive" }, PlaybackReasons.VideoProfile },
        { H264 with { Level = 52 }, PlaybackReasons.VideoLevel },
        { H264 with { BitDepth = 10, Profile = "High" }, PlaybackReasons.VideoBitDepth },
        { H264 with { Width = 7680, Height = 4320, Level = 51 }, PlaybackReasons.VideoResolution },
        { H264 with { Hdr = HdrFormat.Hdr10 }, PlaybackReasons.VideoHdr },
    };

    [Theory]
    [MemberData(nameof(VideoTheClientCannotPlay))]
    public void Video_the_client_cannot_play_is_encoded_again_with_the_reason(MediaStream video, string reason)
    {
        var decision = PlaybackDecider.Decide(Browser(), "mp4", [video, Aac], null)!;

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal([reason], decision.Reasons);
    }

    [Fact]
    public void A_codec_HLS_cannot_carry_is_encoded_again_even_when_the_client_plays_it()
    {
        var client = Browser() with { VideoCodecs = [new VideoCodecSupport("mpeg4")] };
        var divx = new MediaStream(0, MediaStreamType.Video, "mpeg4", Width: 720, Height: 400);

        var decision = PlaybackDecider.Decide(client, "avi", [divx, Aac], null)!;

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal([PlaybackReasons.Container, PlaybackReasons.VideoNotStreamable], decision.Reasons);
    }

    [Fact]
    public void A_file_above_the_clients_bit_rate_is_encoded_again()
    {
        var decision = PlaybackDecider.Decide(Browser(maxBitRate: 4_000_000), "mp4", [H264, Aac], 8_200_000)!;

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal([PlaybackReasons.BitRate], decision.Reasons);
    }

    [Fact]
    public void The_bit_rate_of_the_streams_counts_when_the_file_has_none()
    {
        var withRate = Aac with { BitRate = 192_000 };

        var under = PlaybackDecider.Decide(Browser(maxBitRate: 9_000_000), "mp4", [H264, withRate], null)!;
        var over = PlaybackDecider.Decide(Browser(maxBitRate: 8_100_000), "mp4", [H264, withRate], null)!;

        Assert.Equal(PlaybackMethod.DirectPlay, under.Method);
        Assert.Equal(PlaybackMethod.Transcode, over.Method);
    }

    [Fact]
    public void A_phone_plays_10_bit_HDR10_HEVC_in_MKV_as_it_is()
    {
        var hevc = new MediaStream(0, MediaStreamType.Video, "hevc", "Main 10", 153, 10, 3840, 2160, 23.976, Hdr: HdrFormat.Hdr10);
        var eac3 = new MediaStream(1, MediaStreamType.Audio, "eac3", Channels: 6);

        var decision = PlaybackDecider.Decide(Phone, "mkv", [hevc, eac3], 40_000_000)!;

        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
    }

    [Theory]
    [InlineData(8, HdrFormat.Hdr10, PlaybackMethod.DirectPlay)]
    [InlineData(8, HdrFormat.Hlg, PlaybackMethod.DirectPlay)]
    [InlineData(8, HdrFormat.None, PlaybackMethod.DirectPlay)]
    [InlineData(5, null, PlaybackMethod.Transcode)]
    public void Dolby_Vision_without_a_decoder_plays_its_base_layer_when_the_client_shows_it(
        int profile, HdrFormat? baseLayer, PlaybackMethod expected)
    {
        var dv = new MediaStream(
            0, MediaStreamType.Video, "hevc", "Main 10", 153, 10, 3840, 2160, Hdr: HdrFormat.DolbyVision,
            DolbyVisionProfile: profile, DolbyVisionBaseLayer: baseLayer);

        var decision = PlaybackDecider.Decide(Phone, "mp4", [dv, Aac], null)!;

        Assert.Equal(expected, decision.Method);
    }

    [Fact]
    public void Dolby_Vision_plays_on_a_client_that_decodes_its_profile()
    {
        var client = Phone with { Hdr = [HdrFormat.DolbyVision], DolbyVisionProfiles = [5] };
        var dv = new MediaStream(
            0, MediaStreamType.Video, "hevc", "Main 10", 153, 10, 3840, 2160, Hdr: HdrFormat.DolbyVision, DolbyVisionProfile: 5);

        Assert.Equal(PlaybackMethod.DirectPlay, PlaybackDecider.Decide(client, "mp4", [dv, Aac], null)!.Method);
        Assert.Equal(
            PlaybackMethod.Transcode,
            PlaybackDecider.Decide(client with { DolbyVisionProfiles = [8] }, "mp4", [dv, Aac], null)!.Method);
    }

    [Fact]
    public void Portrait_video_fits_a_landscape_limit()
    {
        var client = Browser() with { VideoCodecs = [new VideoCodecSupport("h264", MaxWidth: 1920, MaxHeight: 1080)] };

        var decision = PlaybackDecider.Decide(client, "mp4", [H264 with { Width = 1080, Height = 1920 }, Aac], null)!;

        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
    }

    [Fact]
    public void The_chosen_audio_track_decides_otherwise_the_default_one()
    {
        var english = new MediaStream(1, MediaStreamType.Audio, "aac", Channels: 2, Language: "eng");
        var commentary = new MediaStream(2, MediaStreamType.Audio, "dts", Channels: 2, Language: "eng", IsDefault: true);

        var byDefault = PlaybackDecider.Decide(Browser(), "mp4", [H264, english, commentary], null)!;
        var chosen = PlaybackDecider.Decide(Browser(), "mp4", [H264, english, commentary], null, audioStreamIndex: 1)!;

        Assert.Equal((PlaybackMethod.AudioTranscode, 2), (byDefault.Method, byDefault.AudioStream));
        Assert.Equal((PlaybackMethod.DirectPlay, 1), (chosen.Method, chosen.AudioStream));
    }

    [Fact]
    public void Video_without_sound_plays_and_a_file_without_video_has_no_decision()
    {
        Assert.Equal(PlaybackMethod.DirectPlay, PlaybackDecider.Decide(Browser(), "mp4", [H264], null)!.Method);
        Assert.Equal(PlaybackMethod.Remux, PlaybackDecider.Decide(Browser(), "mkv", [H264], null)!.Method);
        Assert.Null(PlaybackDecider.Decide(Browser(), "mp4", [Aac], null));
    }

    [Theory]
    [InlineData("h264", 41, 4.1)]
    [InlineData("h264", 51, 5.1)]
    [InlineData("hevc", 120, 4.0)]
    [InlineData("hevc", 153, 5.1)]
    [InlineData("hevc", 186, 6.2)]
    [InlineData("av1", 8, 4.0)]
    [InlineData("av1", 13, 5.1)]
    public void Levels_are_read_in_the_codecs_own_terms(string codec, int stored, double level)
    {
        Assert.Equal(level, PlaybackDecider.Level(codec, stored)!.Value, 3);
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "film.mp4", "mp4")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "film.m4v", "mp4")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "IMG_0001.MOV", "mov")]
    [InlineData("matroska,webm", "film.mkv", "mkv")]
    [InlineData("matroska,webm", "clip.webm", "webm")]
    [InlineData("avi", "old.avi", "avi")]
    [InlineData("mpegts", "recording.ts", "mpegts")]
    public void Containers_are_named_as_clients_name_them(string format, string name, string container)
    {
        Assert.Equal(container, PlaybackDecider.Container(format, name, [new MediaStream(0, MediaStreamType.Video, "vp9")]));
    }

    [Fact]
    public void A_webm_name_on_matroska_with_other_codecs_is_mkv()
    {
        Assert.Equal("mkv", PlaybackDecider.Container("matroska,webm", "mislabelled.webm", [H264, Aac]));
    }
}
