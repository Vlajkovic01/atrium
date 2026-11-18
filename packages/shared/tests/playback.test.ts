import { describe, expect, it } from "vitest";

import en from "../src/i18n/locales/en.json";
import sr from "../src/i18n/locales/sr-Latn.json";
import { browserProfile, playbackMethodKey, playbackReasonKeys, type MediaProbe } from "../src/index.ts";

/** A probe that answers yes to every type matching one of the patterns. */
function probe(patterns: readonly RegExp[], extra: Partial<MediaProbe> = {}): MediaProbe {
  return {
    canPlay: (type) => patterns.some((pattern) => pattern.test(type)),
    nativeHls: false,
    mediaSource: true,
    hdrScreen: false,
    ...extra,
  };
}

describe("browser profile", () => {
  it("describes a desktop browser with H.264, VP9, AV1 and common sound", () => {
    const profile = browserProfile(
      probe([
        /video\/mp4; codecs="avc1\./,
        /video\/webm; codecs="vp[89]"/,
        /video\/mp4; codecs="av01\.0\.08M\.08"/,
        /audio\/mp4; codecs="(mp4a\.40\.2|opus|flac)"/,
        /audio\/mpeg/,
      ]),
    );

    expect(profile.containers).toEqual(["mp4", "mov", "webm"]);
    expect(profile.videoCodecs).toEqual([
      { codec: "h264", profiles: ["Constrained Baseline", "Main", "High"], maxLevel: 5.2, maxBitDepth: 8 },
      { codec: "av1", maxBitDepth: 8 },
      { codec: "vp9", maxBitDepth: 8 },
      { codec: "vp8" },
    ]);
    expect(profile.audioCodecs.map((a) => a.codec)).toEqual(["aac", "mp3", "opus", "flac"]);
    expect(profile.hdr).toEqual([]);
    expect(profile.hls).toBe(true);
  });

  it("describes a browser with native HLS, 10-bit HEVC and an HDR screen", () => {
    const profile = browserProfile(
      probe([/video\/mp4; codecs="(avc1\.|hvc1\.)/, /audio\/mp4; codecs="(mp4a\.40\.2|ac-3|ec-3|alac)"/], {
        nativeHls: true,
        mediaSource: false,
        hdrScreen: true,
      }),
    );

    expect(profile.videoCodecs.find((v) => v.codec === "hevc")).toEqual({
      codec: "hevc",
      profiles: ["Main", "Main 10"],
      maxLevel: 6.1,
      maxBitDepth: 10,
    });
    expect(profile.audioCodecs.map((a) => a.codec)).toEqual(["aac", "ac3", "eac3", "alac"]);
    expect(profile.hdr).toEqual(["hdr10", "hlg"]);
    expect(profile.hls).toBe(true);
  });

  it("finds the highest H.264 profile and level a weak decoder takes", () => {
    const profile = browserProfile(probe([/avc1\.42E0(1E|1F|28|29)/]));

    expect(profile.videoCodecs).toEqual([
      { codec: "h264", profiles: ["Constrained Baseline"], maxLevel: 4.1, maxBitDepth: 8 },
    ]);
  });

  it("claims no HDR on an HDR screen without a 10-bit decoder, and no HLS without a way to play it", () => {
    const profile = browserProfile(probe([/avc1\./], { hdrScreen: true, mediaSource: false }));

    expect(profile.hdr).toEqual([]);
    expect(profile.hls).toBe(false);
  });

  it("carries the bit rate the person chose", () => {
    expect(browserProfile(probe([/avc1\./]), { maxBitRate: 4_000_000 }).maxBitRate).toBe(4_000_000);
    expect(browserProfile(probe([/avc1\./])).maxBitRate).toBeNull();
  });
});

describe("playback texts", () => {
  /** The reasons the server gives; Atrium.Media.Playback.PlaybackReasons, checked from the server's side too. */
  const reasons = [
    "container",
    "video_codec",
    "video_profile",
    "video_level",
    "video_bit_depth",
    "video_resolution",
    "video_hdr",
    "video_not_streamable",
    "audio_codec",
    "audio_channels",
    "audio_not_streamable",
    "bit_rate",
    "keyframes_unknown",
  ];

  const lookup = (messages: unknown, key: string) =>
    key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], messages);

  it.each([
    ["en", en],
    ["sr", sr],
  ])("has every reason and way of playing in %s", (_, messages) => {
    for (const reason of reasons) {
      expect(typeof lookup(messages, playbackReasonKeys(reason)[0]!), reason).toBe("string");
    }

    for (const method of ["directPlay", "remux", "audioTranscode", "transcode"] as const) {
      expect(typeof lookup(messages, playbackMethodKey(method)), method).toBe("string");
    }
  });

  it("falls back to a general text for a reason a newer server gives", () => {
    expect(playbackReasonKeys("video_dolby_vision_enhancement")).toEqual([
      "playback.reason.video_dolby_vision_enhancement",
      "playback.reason.other",
    ]);
  });
});
