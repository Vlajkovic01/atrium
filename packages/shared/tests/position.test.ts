import { describe, expect, it } from "vitest";

import { resumeFrom, savesPosition } from "../src/index.ts";

const FILM = 90 * 60_000;

describe("resuming", () => {
  it("resumes a film where it was", () => {
    expect(resumeFrom(20 * 60_000, FILM)).toBe(20 * 60_000);
  });

  it("starts over near the start, near the end, or without a saved place", () => {
    expect(resumeFrom(29_000, FILM)).toBe(0);
    expect(resumeFrom(FILM - 4 * 60_000, FILM)).toBe(0);
    expect(resumeFrom(null, FILM)).toBe(0);
    expect(resumeFrom(undefined, FILM)).toBe(0);
  });

  it("uses the larger of a minute and five per cent as the end", () => {
    // A ten-minute video: the last minute is the end.
    expect(resumeFrom(8.9 * 60_000, 10 * 60_000)).toBe(8.9 * 60_000);
    expect(resumeFrom(9.1 * 60_000, 10 * 60_000)).toBe(0);
  });

  it("never resumes or saves a clip under five minutes", () => {
    expect(resumeFrom(2 * 60_000, 4 * 60_000)).toBe(0);
    expect(savesPosition(4 * 60_000)).toBe(false);
    expect(savesPosition(5 * 60_000)).toBe(true);
    expect(savesPosition(null)).toBe(false);
  });
});
