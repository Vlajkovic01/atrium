const UNITS = ["byte", "kilobyte", "megabyte", "gigabyte", "terabyte"] as const;
const UNIT_LETTERS = ["B", "kB", "MB", "GB", "TB"] as const;

/** A size such as "12.4 GB", formatted for a language. */
export function formatBytes(bytes: number, locale: string): string {
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }

  const maximumFractionDigits = value >= 100 || unit === 0 ? 0 : 1;
  try {
    return new Intl.NumberFormat(locale, {
      style: "unit",
      unit: UNITS[unit]!,
      unitDisplay: "short",
      maximumFractionDigits,
    }).format(value);
  } catch {
    // An engine without unit formatting (older Android): the number in the
    // language's own form, followed by the unit's letters.
    return `${new Intl.NumberFormat(locale, { maximumFractionDigits }).format(value)} ${UNIT_LETTERS[unit]!}`;
  }
}

/** A moment such as "10 Nov 2026, 14:32", formatted for a language. */
export function formatDateTime(iso: string, locale: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso));
}

/** A day such as "10 Nov 2026", formatted for a language. */
export function formatDay(iso: string, locale: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso));
}

/** Whole days from now until a date, never below zero. */
export function daysUntil(iso: string, now: number = Date.now()): number {
  return Math.max(0, Math.ceil((new Date(iso).getTime() - now) / 86_400_000));
}
