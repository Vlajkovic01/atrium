/**
 * The pure parts of the photo screens: grouping by day, the justified
 * grid, and reading dates out of a search. Kept apart from React so they
 * can be tested on their own.
 */

/** What the layout needs of a photo or video. */
export interface Sized {
  readonly width?: number | string | null;
  readonly height?: number | string | null;
}

export interface Placed<T> {
  readonly item: T;
  readonly width: number;
  readonly height: number;
}

/** The shape of a picture whose size is not known yet: a square. */
function ratioOf(item: Sized): number {
  const width = Number(item.width ?? 0);
  const height = Number(item.height ?? 0);
  if (width > 0 && height > 0) {
    // Panoramas and very tall screenshots would make rows unusable.
    return Math.min(Math.max(width / height, 0.4), 3);
  }

  return 1;
}

/**
 * Justified rows, as photo apps lay them out: every row fills the width,
 * pictures keep their shape, and rows are about the target height. The
 * last row keeps the target height rather than stretching a lone picture.
 */
export function justify<T extends Sized>(
  items: readonly T[],
  containerWidth: number,
  targetHeight: number,
  gap: number,
): Placed<T>[][] {
  const rows: Placed<T>[][] = [];
  let row: T[] = [];
  let ratios = 0;

  const flush = (fill: boolean): void => {
    if (row.length === 0) {
      return;
    }

    const gaps = gap * (row.length - 1);
    const height = fill ? (containerWidth - gaps) / ratios : targetHeight;
    rows.push(row.map((item) => ({ item, width: ratioOf(item) * height, height })));
    row = [];
    ratios = 0;
  };

  for (const item of items) {
    row.push(item);
    ratios += ratioOf(item);
    const width = ratios * targetHeight + gap * (row.length - 1);
    if (width >= containerWidth) {
      flush(true);
    }
  }

  flush(false);
  return rows;
}

/** The day of a capture time. Capture times are local clock times sent with a zero offset (D-034). */
export function dayOf(capturedAt: string): string {
  return capturedAt.slice(0, 10);
}

export interface Day<T> {
  readonly day: string;
  readonly items: readonly T[];
}

/** Consecutive items of the same day together, in the order given. */
export function groupByDay<T extends { readonly capturedAt: string }>(items: readonly T[]): Day<T>[] {
  const days: { day: string; items: T[] }[] = [];
  for (const item of items) {
    const day = dayOf(item.capturedAt);
    const last = days[days.length - 1];
    if (last?.day === day) {
      last.items.push(item);
    } else {
      days.push({ day, items: [item] });
    }
  }

  return days;
}

/** A date range read from a search, as local clock times: from inclusive, to exclusive. */
export interface DateRange {
  readonly from: string;
  readonly to: string;
}

export interface ParsedSearch {
  readonly words: string;
  readonly range: DateRange | null;
}

const pad = (n: number): string => String(n).padStart(2, "0");

function monthRange(year: number, month: number): DateRange {
  const nextYear = month === 12 ? year + 1 : year;
  const nextMonth = month === 12 ? 1 : month + 1;
  return { from: `${year}-${pad(month)}-01T00:00:00Z`, to: `${nextYear}-${pad(nextMonth)}-01T00:00:00Z` };
}

/**
 * Finds a year ("2024") or a month and year ("May 2024", "maj 2024") in a
 * search and returns the rest as the words to look for. Month names are
 * those of the given locale, matched without case and diacritics, in full
 * or by their first three letters.
 */
export function parseSearch(text: string, locale: string): ParsedSearch {
  const fold = (s: string): string => s.normalize("NFD").replace(/\p{M}/gu, "").replace(/đ/g, "dj").toLowerCase();
  const months = Array.from({ length: 12 }, (_, i) =>
    fold(new Intl.DateTimeFormat(locale, { month: "long", timeZone: "UTC" }).format(new Date(Date.UTC(2000, i, 15)))),
  );

  const tokens = text.split(/\s+/).filter(Boolean);
  const yearIndex = tokens.findIndex((t) => /^(19|20)\d\d\.?$/.test(t));
  if (yearIndex < 0) {
    return { words: tokens.join(" "), range: null };
  }

  const year = Number(tokens[yearIndex]!.replace(".", ""));
  const before = yearIndex > 0 ? fold(tokens[yearIndex - 1]!.replace(/[.,]$/, "")) : "";
  const month =
    before.length >= 3
      ? months.findIndex((name) => name === before || (before.length >= 3 && name.startsWith(before)))
      : -1;

  if (month >= 0) {
    const rest = tokens.filter((_, i) => i !== yearIndex && i !== yearIndex - 1);
    return { words: rest.join(" "), range: monthRange(year, month + 1) };
  }

  const rest = tokens.filter((_, i) => i !== yearIndex);
  return { words: rest.join(" "), range: { from: `${year}-01-01T00:00:00Z`, to: `${year + 1}-01-01T00:00:00Z` } };
}

/** The range of one month of the timeline, for jumping to it. */
export function bucketRange(year: number, month: number): DateRange {
  return monthRange(year, month);
}
