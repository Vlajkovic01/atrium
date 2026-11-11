import { describe, expect, it } from "vitest";

import { isLocale, localeNames, messages, pickLocale, supportedLocales } from "../src/i18n/index.ts";
import { messagesToCyrillic, type MessageTree } from "../src/i18n/transliterate.ts";

function flatten(tree: MessageTree, prefix = ""): Map<string, string> {
  const out = new Map<string, string>();
  for (const [key, value] of Object.entries(tree)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (typeof value === "string") {
      out.set(path, value);
    } else {
      for (const [k, v] of flatten(value, path)) {
        out.set(k, v);
      }
    }
  }

  return out;
}

/** Names of the arguments a message uses, at any depth. */
function argumentNames(message: string): string[] {
  const names = new Set<string>();
  for (const match of message.matchAll(/\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*[,}]/g)) {
    names.add(match[1]!);
  }

  return [...names].sort();
}

/** The plural categories each plural argument of a message provides. */
function pluralCategories(message: string): string[][] {
  const out: string[][] = [];
  for (const match of message.matchAll(/\{\s*[A-Za-z_][A-Za-z0-9_]*\s*,\s*plural\s*,/g)) {
    let depth = 1;
    let i = match.index + match[0].length;
    let selectors = "";
    for (; i < message.length && depth > 0; i++) {
      const c = message[i]!;
      if (c === "{") {
        depth++;
      } else if (c === "}") {
        depth--;
      } else if (depth === 1) {
        selectors += c;
      }
    }

    out.push(selectors.split(/\s+/).filter((s) => s.length > 0 && !s.startsWith("offset:")));
  }

  return out;
}

const english = flatten(messages.en);
const latin = flatten(messages["sr-Latn"]);
const cyrillic = flatten(messages["sr-Cyrl"]);

describe("translations", () => {
  it("has every key in every language", () => {
    expect([...latin.keys()].sort()).toEqual([...english.keys()].sort());
    expect([...cyrillic.keys()].sort()).toEqual([...english.keys()].sort());
  });

  it("has no empty message", () => {
    for (const [language, table] of [
      ["en", english],
      ["sr-Latn", latin],
      ["sr-Cyrl", cyrillic],
    ] as const) {
      for (const [key, value] of table) {
        expect(value.trim(), `${language}: ${key}`).not.toBe("");
      }
    }
  });

  it("uses the same arguments in every language", () => {
    for (const [key, value] of english) {
      expect(argumentNames(latin.get(key)!), key).toEqual(argumentNames(value));
      expect(argumentNames(cyrillic.get(key)!), key).toEqual(argumentNames(value));
    }
  });

  it("gives English plurals the forms English needs", () => {
    for (const [key, value] of english) {
      for (const categories of pluralCategories(value)) {
        expect(categories, key).toEqual(expect.arrayContaining(["one", "other"]));
      }
    }
  });

  it("gives Serbian plurals all three forms Serbian needs", () => {
    for (const [key, value] of latin) {
      for (const categories of pluralCategories(value)) {
        expect(categories, key).toEqual(expect.arrayContaining(["one", "few", "other"]));
      }
    }
  });

  it("keeps the Cyrillic file in step with the Latin one", () => {
    // If this fails, run: pnpm --filter @atrium/shared generate:cyrillic
    expect(messages["sr-Cyrl"]).toEqual(messagesToCyrillic(messages["sr-Latn"]));
  });

  it("writes Cyrillic messages in Cyrillic", () => {
    expect(cyrillic.get("common.save")).toBe("Сачувај");
    expect(cyrillic.get("errors.storage.location_in_use")).toContain("Atrium");
    expect(cyrillic.get("errors.auth.invalid_link_code")).toContain("QR");
  });
});

describe("choosing a language", () => {
  it("names every language in its own script", () => {
    expect(Object.keys(localeNames).sort()).toEqual([...supportedLocales].sort());
    expect(localeNames["sr-Cyrl"]).toMatch(/Српски/);
  });

  it.each([
    [["sr-Latn-RS", "en"], "sr-Latn"],
    [["sr-Cyrl-RS"], "sr-Cyrl"],
    [["sr-RS"], "sr-Latn"],
    [["sr"], "sr-Latn"],
    [["en-GB", "sr"], "en"],
    [["de-DE", "sr-Cyrl"], "sr-Cyrl"],
    [["de-DE", "fr"], "en"],
    [[], "en"],
  ])("picks the best match for %j", (preferred, expected) => {
    expect(pickLocale(preferred)).toBe(expected);
  });

  it("recognizes only supported languages", () => {
    expect(isLocale("sr-Cyrl")).toBe(true);
    expect(isLocale("sr")).toBe(false);
    expect(isLocale(null)).toBe(false);
  });
});
