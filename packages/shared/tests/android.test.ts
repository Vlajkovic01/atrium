import { describe, expect, it } from "vitest";

import { androidProfile, type AndroidMediaCapabilities } from "../src/index.ts";

/** What a mid-range phone reports: hardware H.264 and HEVC Main 10, software VP9 and AV1, the usual sound. */
const PHONE: AndroidMediaCapabilities = {
  sdk: 34,
  hdrTypes: [2, 3],
  decoders: [
    {
      mime: "video/avc",
      profileLevels: [
        [0x8, 0x8000],
        [0x2, 0x8000],
        [0x10000, 0x8000],
      ],
      maxWidth: 4096,
      maxHeight: 2176,
      hardware: true,
    },
    { mime: "video/avc", profileLevels: [[0x1, 0x800]], maxWidth: 1920, maxHeight: 1088, hardware: false },
    {
      mime: "video/hevc",
      profileLevels: [
        [0x2, 0x10000],
        [0x1000, 0x10000],
      ],
      maxWidth: 4096,
      maxHeight: 2176,
      hardware: true,
    },
    {
      mime: "video/x-vnd.on2.vp9",
      profileLevels: [
        [0x1, 0x100],
        [0x4, 0x100],
      ],
      maxWidth: 3840,
      maxHeight: 2160,
    },
    { mime: "video/av01", profileLevels: [[0x1, 0x100]], maxWidth: 1920, maxHeight: 1080 },
    { mime: "audio/mp4a-latm", profileLevels: [[2, 0]], maxChannels: 8 },
    { mime: "audio/opus", profileLevels: [], maxChannels: 8 },
    { mime: "audio/ac3", profileLevels: [], maxChannels: 6 },
    { mime: "audio/eac3", profileLevels: [], maxChannels: 8 },
    { mime: "audio/raw", profileLevels: [], maxChannels: 8 },
    { mime: "video/x-unknown", profileLevels: [] },
  ],
};

describe("android profile", () => {
  it("names the decoders in the server's terms, the most of each format", () => {
    const profile = androidProfile(PHONE);

    expect(profile.videoCodecs).toEqual([
      {
        codec: "h264",
        profiles: ["High", "Main", "Constrained Baseline", "Baseline"],
        maxLevel: 5.1,
        maxBitDepth: 8,
        maxWidth: 4096,
        maxHeight: 2176,
      },
      { codec: "hevc", profiles: ["Main 10", "Main"], maxLevel: 5.1, maxBitDepth: 10, maxWidth: 4096, maxHeight: 2176 },
      { codec: "vp9", maxBitDepth: 10, maxWidth: 3840, maxHeight: 2160 },
      { codec: "av1", maxLevel: 4, maxBitDepth: 8, maxWidth: 1920, maxHeight: 1080 },
    ]);
    expect(profile.audioCodecs).toEqual([
      { codec: "aac", maxChannels: 8 },
      { codec: "opus", maxChannels: 8 },
      { codec: "ac3", maxChannels: 6 },
      { codec: "eac3", maxChannels: 8 },
    ]);
    expect(profile.containers).toContain("mkv");
    expect(profile.hls).toBe(true);
  });

  it("shows the HDR the screen shows, and Dolby Vision only with its decoder", () => {
    expect(androidProfile(PHONE).hdr).toEqual(["hdr10", "hlg"]);

    const dv = androidProfile({
      ...PHONE,
      hdrTypes: [1, 2],
      decoders: [
        ...PHONE.decoders,
        {
          mime: "video/dolby-vision",
          profileLevels: [
            [0x20, 0x40],
            [0x100, 0x40],
          ],
        },
      ],
    });
    expect(dv.hdr).toEqual(["dolbyVision", "hdr10"]);
    expect(dv.dolbyVisionProfiles).toEqual([5, 8]);

    expect(androidProfile({ ...PHONE, hdrTypes: [1] }).hdr).toEqual([]);
  });

  it("claims no HDR without a 10-bit decoder, whatever the screen", () => {
    const eightBit = PHONE.decoders.filter((d) => d.mime !== "video/hevc" && d.mime !== "video/x-vnd.on2.vp9");

    expect(androidProfile({ ...PHONE, decoders: eightBit }).hdr).toEqual([]);
  });

  it("carries the bit rate the person chose", () => {
    expect(androidProfile(PHONE, { maxBitRate: 2_000_000 }).maxBitRate).toBe(2_000_000);
  });
});
