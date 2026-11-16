// Hermes, the JavaScript engine of React Native, has no Intl.PluralRules,
// which the message format needs for "1 file, 2 files" and Serbian's three
// forms. The polyfill adds it only where it is missing.
import "@formatjs/intl-pluralrules/polyfill.js";
import "@formatjs/intl-pluralrules/locale-data/en.js";
import "@formatjs/intl-pluralrules/locale-data/sr.js";

import { defaultLocale, isLocale, messages, pickLocale, supportedLocales, type Locale } from "@atrium/shared";
import { getLocales } from "expo-localization";
import i18next from "i18next";
import ICU from "i18next-icu";
import { initReactI18next } from "react-i18next";

/** The best supported language for the languages the device is set to, in the person's order. */
export function deviceLocale(): Locale {
  return pickLocale(getLocales().map((l) => l.languageTag));
}

export function currentLocale(): Locale {
  return isLocale(i18next.language) ? i18next.language : defaultLocale;
}

export async function setLocale(locale: Locale): Promise<void> {
  await i18next.changeLanguage(locale);
}

/** Starts translations in the given language, or the device's. The same messages as the web app, from the shared package. */
export async function initI18n(locale: Locale = deviceLocale()): Promise<void> {
  if (i18next.isInitialized) {
    await i18next.changeLanguage(locale);
    return;
  }

  await i18next
    .use(ICU)
    .use(initReactI18next)
    .init({
      lng: locale,
      fallbackLng: defaultLocale,
      supportedLngs: [...supportedLocales],
      resources: Object.fromEntries(supportedLocales.map((l) => [l, { translation: messages[l] }])),
      // React Native renders text, not HTML; there is nothing to escape.
      interpolation: { escapeValue: false },
      returnNull: false,
    });
}
