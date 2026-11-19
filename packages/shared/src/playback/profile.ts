import type { components } from "../api/schema.d.ts";

export type ClientProfile = components["schemas"]["ClientProfile"];
export type VideoCodecSupport = components["schemas"]["VideoCodecSupport"];
export type AudioCodecSupport = components["schemas"]["AudioCodecSupport"];
export type HdrFormat = components["schemas"]["HdrFormat"];

/**
 * What a browser can be asked about playing video. In a page it is
 * `browserMediaProbe()`; tests give their own, so the profile can be
 * checked without a browser.
 */
export interface MediaProbe {
  /** Whether a media element or Media Source plays this type with its codecs: `video/mp4; codecs="avc1.640033"`. */
  canPlay(type: string): boolean;
  /** HLS in the media element itself, as in Safari. */
  readonly nativeHls: boolean;
  /** Media Source Extensions, which HLS through hls.js needs. */
  readonly mediaSource: boolean;
  /** The screen shows high dynamic range: `(dynamic-range: high)`. */
  readonly hdrScreen: boolean;
}

/** H.264 profiles with their codec string prefix (profile_idc and constraints). */
const H264_PROFILES: readonly (readonly [name: string, prefix: string])[] = [
  ["Constrained Baseline", "avc1.42E0"],
  ["Main", "avc1.4D40"],
  ["High", "avc1.6400"],
];

/** Levels to try, highest first, as written (5.1) and as in a codec string (0x33). */
const H264_LEVELS: readonly (readonly [level: number, hex: string])[] = [
  [5.2, "34"],
  [5.1, "33"],
  [5.0, "32"],
  [4.2, "2A"],
  [4.1, "29"],
  [4.0, "28"],
  [3.1, "1F"],
];

/** HEVC levels to try, highest first, with the level times 30 as codec strings write it. */
const HEVC_LEVELS: readonly (readonly [level: number, coded: number])[] = [
  [6.1, 183],
  [5.1, 153],
  [5.0, 150],
  [4.1, 123],
  [4.0, 120],
];

/**
 * What this browser plays itself (DESIGN.md 7.2), described in the
 * server's terms: FFmpeg's codec and container names. Every client builds
 * its profile here or in a sibling for its platform, so all describe
 * themselves the same way.
 *
 * QuickTime files (.mov) count as playable whenever MP4 is: they are the
 * same file structure, browsers that answer "no" for `video/quicktime`
 * play them, and the server still checks every codec inside.
 */
export function browserProfile(probe: MediaProbe, options: { readonly maxBitRate?: number } = {}): ClientProfile {
  const mp4 = (codecs: string) => probe.canPlay(`video/mp4; codecs="${codecs}"`);
  const webm = (codecs: string) => probe.canPlay(`video/webm; codecs="${codecs}"`);
  const audio = (codecs: string) => probe.canPlay(`audio/mp4; codecs="${codecs}"`);

  const videoCodecs: VideoCodecSupport[] = [];
  const h264Profiles = H264_PROFILES.filter(([, prefix]) => mp4(`${prefix}1E`)).map(([name]) => name);
  if (h264Profiles.length > 0) {
    const high = H264_PROFILES.find(([name]) => name === h264Profiles[h264Profiles.length - 1])![1];
    const level = H264_LEVELS.find(([, hex]) => mp4(`${high}${hex}`))?.[0] ?? 3.0;
    videoCodecs.push({ codec: "h264", profiles: h264Profiles, maxLevel: level, maxBitDepth: 8 });
  }

  if (mp4("hvc1.1.6.L93.B0") || mp4("hev1.1.6.L93.B0")) {
    const main10 = mp4("hvc1.2.4.L153.B0") || mp4("hev1.2.4.L153.B0");
    const level = HEVC_LEVELS.find(([, coded]) => mp4(`hvc1.${main10 ? "2.4" : "1.6"}.L${coded}.B0`))?.[0] ?? 3.1;
    videoCodecs.push({
      codec: "hevc",
      profiles: main10 ? ["Main", "Main 10"] : ["Main"],
      maxLevel: level,
      maxBitDepth: main10 ? 10 : 8,
    });
  }

  if (mp4("av01.0.08M.08")) {
    videoCodecs.push({ codec: "av1", maxBitDepth: mp4("av01.0.08M.10") ? 10 : 8 });
  }

  if (webm("vp9") || mp4("vp09.00.40.08")) {
    videoCodecs.push({ codec: "vp9", maxBitDepth: mp4("vp09.02.40.10") || webm("vp09.02.40.10") ? 10 : 8 });
  }

  if (webm("vp8")) {
    videoCodecs.push({ codec: "vp8" });
  }

  const audioCodecs: AudioCodecSupport[] = [];
  const audioChecks: readonly (readonly [codec: string, check: () => boolean])[] = [
    ["aac", () => audio("mp4a.40.2")],
    ["mp3", () => audio("mp4a.69") || audio("mp4a.6B") || probe.canPlay("audio/mpeg")],
    ["opus", () => audio("opus") || probe.canPlay('audio/webm; codecs="opus"')],
    ["vorbis", () => probe.canPlay('audio/webm; codecs="vorbis"')],
    ["flac", () => audio("flac")],
    ["ac3", () => audio("ac-3")],
    ["eac3", () => audio("ec-3")],
    ["alac", () => audio("alac")],
  ];
  for (const [codec, check] of audioChecks) {
    if (check()) {
      audioCodecs.push({ codec });
    }
  }

  const containers: string[] = [];
  if (
    videoCodecs.some((v) => v.codec !== "vp8") &&
    (mp4("avc1.42E01E") || mp4("hvc1.1.6.L93.B0") || mp4("av01.0.08M.08"))
  ) {
    containers.push("mp4", "mov");
  }

  if (webm("vp9") || webm("vp8") || webm("av01.0.08M.08")) {
    containers.push("webm");
  }

  if (probe.canPlay('video/x-matroska; codecs="avc1.42E01E"') || probe.canPlay("video/x-matroska")) {
    containers.push("mkv");
  }

  // A browser shows HDR only on an HDR screen, and only with a decoder for 10-bit video.
  const tenBit = videoCodecs.some((v) => Number(v.maxBitDepth ?? 8) >= 10);
  const hdr: HdrFormat[] = probe.hdrScreen && tenBit ? ["hdr10", "hlg"] : [];

  return {
    containers,
    videoCodecs,
    audioCodecs,
    hdr,
    hls: probe.nativeHls || probe.mediaSource,
    maxBitRate: options.maxBitRate ?? null,
    dolbyVisionProfiles: null,
  };
}

/** The probe of the page this runs in; only in a browser. */
export function browserMediaProbe(): MediaProbe {
  const element = document.createElement("video");
  const source = (globalThis as { MediaSource?: { isTypeSupported(type: string): boolean } }).MediaSource;
  return {
    canPlay: (type) => element.canPlayType(type) === "probably" || source?.isTypeSupported(type) === true,
    nativeHls: element.canPlayType("application/vnd.apple.mpegurl") !== "",
    mediaSource: source !== undefined,
    hdrScreen: globalThis.matchMedia?.("(dynamic-range: high)").matches === true,
  };
}
