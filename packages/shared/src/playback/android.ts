import type { AudioCodecSupport, ClientProfile, HdrFormat, VideoCodecSupport } from "./profile.ts";

/** One decoder as Android's MediaCodecList reports it; numbers are Android's own constants. */
export interface AndroidDecoder {
  readonly mime: string;
  /** Pairs of [profile, level], MediaCodecInfo.CodecProfileLevel constants. */
  readonly profileLevels: readonly (readonly [number, number])[];
  readonly maxWidth?: number;
  readonly maxHeight?: number;
  readonly maxChannels?: number;
  readonly hardware?: boolean;
}

/** What the Android module reads (mobile/modules/atrium-media-capabilities). */
export interface AndroidMediaCapabilities {
  readonly decoders: readonly AndroidDecoder[];
  /** Display.HdrCapabilities types: 1 Dolby Vision, 2 HDR10, 3 HLG, 4 HDR10+. */
  readonly hdrTypes: readonly number[];
  readonly sdk: number;
}

const VIDEO: Readonly<Record<string, string>> = {
  "video/avc": "h264",
  "video/hevc": "hevc",
  "video/av01": "av1",
  "video/x-vnd.on2.vp9": "vp9",
  "video/x-vnd.on2.vp8": "vp8",
  "video/mp4v-es": "mpeg4",
  "video/mpeg2": "mpeg2video",
};

const AUDIO: Readonly<Record<string, string>> = {
  "audio/mp4a-latm": "aac",
  "audio/mpeg": "mp3",
  "audio/opus": "opus",
  "audio/vorbis": "vorbis",
  "audio/flac": "flac",
  "audio/ac3": "ac3",
  "audio/eac3": "eac3",
  "audio/eac3-joc": "eac3",
  "audio/alac": "alac",
  "audio/vnd.dts": "dts",
};

/** AVC profiles by flag, in FFmpeg's names, and which carry 10 bits. */
const AVC_PROFILES: readonly (readonly [flag: number, name: string, bits: number])[] = [
  [0x10000, "Constrained Baseline", 8],
  [0x1, "Baseline", 8],
  [0x2, "Main", 8],
  [0x8, "High", 8],
  [0x80000, "High", 8],
  [0x10, "High 10", 10],
];

/** AVC level flags, from 1 (0x1) to 6.2 (0x80000), as levels. */
const AVC_LEVELS: readonly number[] = [
  1, 1, 1.1, 1.2, 1.3, 2, 2.1, 2.2, 3, 3.1, 3.2, 4, 4.1, 4.2, 5, 5.1, 5.2, 6, 6.1, 6.2,
];

/** HEVC main-tier level flags (each high tier is the next bit), as levels. */
const HEVC_LEVELS: readonly number[] = [1, 2, 2.1, 3, 3.1, 4, 4.1, 5, 5.1, 5.2, 6, 6.1, 6.2];

/** The containers Media3 opens itself, as the server names them. */
const CONTAINERS = ["mp4", "mov", "mkv", "webm", "mpegts", "avi"];

/**
 * What an Android phone plays in its own player (Media3 through
 * expo-video), from what Android says its decoders take and its screen
 * shows (DESIGN.md 7.2, D-067). Several decoders of one format count
 * together: the most any of them takes.
 */
export function androidProfile(
  capabilities: AndroidMediaCapabilities,
  options: { readonly maxBitRate?: number } = {},
): ClientProfile {
  const video = new Map<
    string,
    { profiles: Set<string>; level: number; bits: number; width: number; height: number }
  >();
  const audio = new Map<string, number>();
  const dolbyVision = new Set<number>();

  for (const decoder of capabilities.decoders) {
    const mime = decoder.mime.toLowerCase();
    if (mime === "video/dolby-vision") {
      for (const [profile] of decoder.profileLevels) {
        for (let bit = 0; bit < 16; bit++) {
          if (profile & (1 << bit)) {
            dolbyVision.add(bit);
          }
        }
      }

      continue;
    }

    const audioCodec = AUDIO[mime];
    if (audioCodec) {
      audio.set(audioCodec, Math.max(audio.get(audioCodec) ?? 0, decoder.maxChannels ?? 2));
      continue;
    }

    const codec = VIDEO[mime];
    if (!codec) {
      continue;
    }

    const entry = video.get(codec) ?? { profiles: new Set<string>(), level: 0, bits: 8, width: 0, height: 0 };
    for (const [profile, level] of decoder.profileLevels) {
      const read = readProfile(codec, profile, level);
      read.profiles.forEach((name) => entry.profiles.add(name));
      entry.level = Math.max(entry.level, read.level);
      entry.bits = Math.max(entry.bits, read.bits);
    }

    // A decoder of a profile decodes the simpler ones it contains.
    if (codec === "h264") {
      if (entry.profiles.has("High") || entry.profiles.has("High 10")) {
        entry.profiles.add("Main");
      }

      if (entry.profiles.has("Main") || entry.profiles.has("Baseline")) {
        entry.profiles.add("Constrained Baseline");
      }
    }

    if (codec === "hevc" && entry.profiles.has("Main 10")) {
      entry.profiles.add("Main");
    }

    entry.width = Math.max(entry.width, decoder.maxWidth ?? 0);
    entry.height = Math.max(entry.height, decoder.maxHeight ?? 0);
    video.set(codec, entry);
  }

  const videoCodecs: VideoCodecSupport[] = [...video].map(([codec, entry]) => ({
    codec,
    ...(entry.profiles.size > 0 ? { profiles: [...entry.profiles] } : {}),
    ...(entry.level > 0 ? { maxLevel: entry.level } : {}),
    maxBitDepth: entry.bits,
    ...(entry.width > 0 && entry.height > 0 ? { maxWidth: entry.width, maxHeight: entry.height } : {}),
  }));
  const audioCodecs: AudioCodecSupport[] = [...audio].map(([codec, channels]) => ({ codec, maxChannels: channels }));

  // The screen must show it and a decoder must take 10 bits.
  const tenBit = videoCodecs.some((v) => Number(v.maxBitDepth ?? 8) >= 10);
  const hdr = new Set<HdrFormat>();
  if (tenBit) {
    for (const type of capabilities.hdrTypes) {
      if (type === 2 || type === 4) {
        hdr.add("hdr10");
      } else if (type === 3) {
        hdr.add("hlg");
      } else if (type === 1 && dolbyVision.size > 0) {
        hdr.add("dolbyVision");
      }
    }
  }

  return {
    containers: CONTAINERS,
    videoCodecs,
    audioCodecs,
    hdr: [...hdr],
    hls: true,
    maxBitRate: options.maxBitRate ?? null,
    dolbyVisionProfiles: hdr.has("dolbyVision") ? [...dolbyVision].sort((a, b) => a - b) : null,
  };
}

function readProfile(
  codec: string,
  profile: number,
  level: number,
): { profiles: string[]; level: number; bits: number } {
  const highestBit = (value: number) => (value > 0 ? 31 - Math.clz32(value) : -1);
  switch (codec) {
    case "h264": {
      const matched = AVC_PROFILES.filter(([flag]) => profile === flag);
      return {
        profiles: matched.map(([, name]) => name),
        level: AVC_LEVELS[highestBit(level)] ?? 0,
        bits: Math.max(8, ...matched.map(([, , bits]) => bits)),
      };
    }
    case "hevc": {
      // Main 0x1, Main 10 0x2, Main 10 HDR10 0x1000, Main 10 HDR10+ 0x2000.
      const tenBit = (profile & (0x2 | 0x1000 | 0x2000)) !== 0;
      const names = [...(profile & 0x1 ? ["Main"] : []), ...(tenBit ? ["Main 10"] : [])];
      return { profiles: names, level: HEVC_LEVELS[Math.floor(highestBit(level) / 2)] ?? 0, bits: tenBit ? 10 : 8 };
    }
    case "vp9":
      // Profile 2 and 3 (0x4, 0x8) and their HDR flags carry 10 bits.
      return { profiles: [], level: 0, bits: profile & (0x4 | 0x8 | 0x1000 | 0x2000 | 0x4000 | 0x8000) ? 10 : 8 };
    case "av1": {
      const bit = highestBit(level);
      return {
        profiles: [],
        level: bit < 0 ? 0 : 2 + (bit >> 2) + (bit & 3) / 10,
        bits: profile & (0x2 | 0x1000 | 0x2000) ? 10 : 8,
      };
    }
    default:
      return { profiles: [], level: 0, bits: 8 };
  }
}
