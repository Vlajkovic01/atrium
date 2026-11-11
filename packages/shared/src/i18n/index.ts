import en from "./locales/en.json" with { type: "json" };
import srCyrl from "./locales/sr-Cyrl.json" with { type: "json" };
import srLatn from "./locales/sr-Latn.json" with { type: "json" };

/** The languages every client ships. The server accepts exactly these for a user's language. */
export const supportedLocales = ["en", "sr-Latn", "sr-Cyrl"] as const;

export type Locale = (typeof supportedLocales)[number];

export const defaultLocale: Locale = "en";

/** Each language's own name, in its own script, so it can be found by someone who cannot read the current one. */
export const localeNames: Readonly<Record<Locale, string>> = {
  en: "English",
  "sr-Latn": "Srpski (latinica)",
  "sr-Cyrl": "Српски (ћирилица)",
};

export const messages: Readonly<Record<Locale, typeof en>> = {
  en,
  "sr-Latn": srLatn,
  "sr-Cyrl": srCyrl,
};

/**
 * Picks the best supported language for what the device asks for, in
 * order of preference. "sr" alone follows the design decision: Latin
 * unless the device says Cyrillic (D-018).
 */
export function pickLocale(preferred: readonly string[]): Locale {
  for (const tag of preferred) {
    const lower = tag.toLowerCase();
    if (lower.startsWith("sr")) {
      return lower.includes("cyrl") ? "sr-Cyrl" : "sr-Latn";
    }

    if (lower.startsWith("en")) {
      return "en";
    }
  }

  return defaultLocale;
}

export function isLocale(value: unknown): value is Locale {
  return typeof value === "string" && (supportedLocales as readonly string[]).includes(value);
}
