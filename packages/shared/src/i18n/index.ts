import en from "./locales/en.json" with { type: "json" };
import srLatn from "./locales/sr-Latn.json" with { type: "json" };

/**
 * The languages every client ships: English and Serbian in Latin script
 * (D-022). The server accepts exactly these for a user's language.
 *
 * Serbian keeps the full tag "sr-Latn" on purpose. Platforms format dates
 * and numbers for plain "sr" in Cyrillic, which would put Cyrillic month
 * names into a Latin interface.
 */
export const supportedLocales = ["en", "sr-Latn"] as const;

export type Locale = (typeof supportedLocales)[number];

export const defaultLocale: Locale = "en";

/** Each language's own name, so it can be found by someone who cannot read the current one. */
export const localeNames: Readonly<Record<Locale, string>> = {
  en: "English",
  "sr-Latn": "Srpski",
};

export const messages: Readonly<Record<Locale, typeof en>> = {
  en,
  "sr-Latn": srLatn,
};

/**
 * Picks the best supported language for what the device asks for, in
 * order of preference. Any Serbian setting, whatever its script, gets the
 * Serbian translation.
 */
export function pickLocale(preferred: readonly string[]): Locale {
  for (const tag of preferred) {
    const lower = tag.toLowerCase();
    if (lower === "sr" || lower.startsWith("sr-")) {
      return "sr-Latn";
    }

    if (lower === "en" || lower.startsWith("en-")) {
      return "en";
    }
  }

  return defaultLocale;
}

export function isLocale(value: unknown): value is Locale {
  return typeof value === "string" && (supportedLocales as readonly string[]).includes(value);
}
