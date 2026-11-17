import { describe, expect, it } from "vitest";

import {
  bucketRange,
  formatDuration,
  formatPhotoDay,
  formatTakenAt,
  groupByDay,
  justify,
  parseSearch,
} from "../src/photos/layout.ts";

describe("justify", () => {
  it("fills every row but the last to the container width", () => {
    const items = Array.from({ length: 7 }, () => ({ width: 400, height: 300 }));

    const rows = justify(items, 1000, 200, 4);

    for (const row of rows.slice(0, -1)) {
      const width = row.reduce((sum, p) => sum + p.width, 0) + 4 * (row.length - 1);
      expect(width).toBeCloseTo(1000, 5);
    }

    expect(rows.flat()).toHaveLength(7);
    expect(rows.at(-1)![0]!.height).toBe(200);
  });

  it("keeps each picture's shape and treats unknown sizes as squares", () => {
    const rows = justify(
      [
        { width: 300, height: 600 },
        { width: null, height: null },
      ],
      10_000,
      100,
      0,
    );

    const [tall, unknown] = rows[0]!;
    expect(tall!.width / tall!.height).toBeCloseTo(0.5, 5);
    expect(unknown!.width).toBe(unknown!.height);
  });
});

describe("groupByDay", () => {
  it("puts consecutive pictures of one day together", () => {
    const days = groupByDay([
      { capturedAt: "2024-05-02T23:59:00Z" },
      { capturedAt: "2024-05-02T08:00:00Z" },
      { capturedAt: "2024-05-01T12:00:00Z" },
    ]);

    expect(days.map((d) => [d.day, d.items.length])).toEqual([
      ["2024-05-02", 2],
      ["2024-05-01", 1],
    ]);
  });
});

describe("parseSearch", () => {
  it("reads a year", () => {
    expect(parseSearch("beach 2024", "en")).toEqual({
      words: "beach",
      range: { from: "2024-01-01T00:00:00Z", to: "2025-01-01T00:00:00Z" },
    });
  });

  it("reads a month and year in English and in Serbian", () => {
    expect(parseSearch("May 2024", "en").range).toEqual({ from: "2024-05-01T00:00:00Z", to: "2024-06-01T00:00:00Z" });
    expect(parseSearch("more decembar 2023", "sr-Latn")).toEqual({
      words: "more",
      range: { from: "2023-12-01T00:00:00Z", to: "2024-01-01T00:00:00Z" },
    });
    expect(parseSearch("avg 2022", "sr-Latn").range).toEqual({
      from: "2022-08-01T00:00:00Z",
      to: "2022-09-01T00:00:00Z",
    });
  });

  it("leaves text without a year alone", () => {
    expect(parseSearch("summer trip", "en")).toEqual({ words: "summer trip", range: null });
  });
});

describe("bucketRange", () => {
  it("ends at the start of the next month, across the year", () => {
    expect(bucketRange(2023, 12)).toEqual({ from: "2023-12-01T00:00:00Z", to: "2024-01-01T00:00:00Z" });
  });
});

describe("photo dates and lengths", () => {
  it("writes the day of a photo in full, in the language's own way", () => {
    // The punctuation differs between versions of the language data; the parts do not.
    expect(formatPhotoDay("2024-05-02", "en-GB")).toMatch(/^Thursday,? 2 May 2024$/);
    expect(formatPhotoDay("2024-05-02", "sr-Latn")).toContain("2024");
  });

  it("keeps the day of local clock time, whatever the time zone", () => {
    expect(formatPhotoDay("2024-12-31", "en-US")).toContain("December 31, 2024");
  });

  it("writes lengths as minutes and seconds, with hours when needed", () => {
    expect(formatDuration(0)).toBe("0:00");
    expect(formatDuration(42_400)).toBe("0:42");
    expect(formatDuration(62_000)).toBe("1:02");
    expect(formatDuration(3_723_000)).toBe("1:02:03");
  });
});

describe("when a photo was taken", () => {
  it("shows the clock of the capture time, whatever offset it carries", () => {
    for (const captured of ["2026-10-15T09:00:00Z", "2026-10-15T09:00:00+00:00", "2026-10-15T09:00:00.000Z"]) {
      expect(formatTakenAt(captured, "en-GB")).toMatch(/15 Oct 2026,? 09:00/);
    }
  });
});
