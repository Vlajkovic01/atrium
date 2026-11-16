import { defaultLocale, isLocale, messages, pickLocale, supportedLocales, type Locale } from "@atrium/shared";
import i18next from "i18next";
import ICU from "i18next-icu";
import { initReactI18next } from "react-i18next";

const STORAGE_KEY = "atrium.locale";

/** The language chosen on this browser, else the best match for the browser's own settings. */
export function initialLocale(): Locale {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (isLocale(stored)) {
      return stored;
    }
  } catch {
    // Storage can be unavailable (private mode); the browser's language is used.
  }

  return pickLocale(navigator.languages);
}

export async function setLocale(locale: Locale): Promise<void> {
  try {
    localStorage.setItem(STORAGE_KEY, locale);
  } catch {
    // Not remembered on this browser; still applied now.
  }

  document.documentElement.lang = locale;
  await i18next.changeLanguage(locale);
}

export function currentLocale(): Locale {
  return isLocale(i18next.language) ? i18next.language : defaultLocale;
}

export async function initI18n(): Promise<void> {
  const locale = initialLocale();
  document.documentElement.lang = locale;
  await i18next
    .use(ICU)
    .use(initReactI18next)
    .init({
      lng: locale,
      fallbackLng: defaultLocale,
      supportedLngs: [...supportedLocales],
      resources: Object.fromEntries(supportedLocales.map((l) => [l, { translation: messages[l] }])),
      // React escapes what it renders; escaping here too would double it.
      interpolation: { escapeValue: false },
      returnNull: false,
    });
}

/** A size such as "12.4 GB", formatted for the current language. */
export function formatBytes(bytes: number): string {
  const units = ["byte", "kilobyte", "megabyte", "gigabyte", "terabyte"] as const;
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return new Intl.NumberFormat(currentLocale(), {
    style: "unit",
    unit: units[unit],
    unitDisplay: "short",
    maximumFractionDigits: value >= 100 || unit === 0 ? 0 : 1,
  }).format(value);
}

/** A moment such as "10 Nov 2026, 14:32", formatted for the current language. */
export function formatDateTime(iso: string): string {
  return new Intl.DateTimeFormat(currentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso));
}

/** How long ago, such as "2 days ago", in the current language. */
export function formatAgo(iso: string, now: number = Date.now()): string {
  const seconds = Math.round((Date.parse(iso) - now) / 1000);
  const steps: [Intl.RelativeTimeFormatUnit, number][] = [
    ["day", 86_400],
    ["hour", 3_600],
    ["minute", 60],
  ];
  const format = new Intl.RelativeTimeFormat(currentLocale(), { numeric: "auto" });
  for (const [unit, size] of steps) {
    if (Math.abs(seconds) >= size) {
      return format.format(Math.round(seconds / size), unit);
    }
  }

  return format.format(0, "minute");
}
