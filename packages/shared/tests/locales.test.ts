import { describe, expect, it } from "vitest";

import { isLocale, localeNames, messages, pickLocale, supportedLocales } from "../src/i18n/index.ts";

type MessageTree = { readonly [key: string]: string | MessageTree };

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
const serbian = flatten(messages["sr-Latn"]);

describe("translations", () => {
  it("has every key in both languages", () => {
    expect([...serbian.keys()].sort()).toEqual([...english.keys()].sort());
  });

  it("has no empty message", () => {
    for (const [language, table] of [
      ["en", english],
      ["sr-Latn", serbian],
    ] as const) {
      for (const [key, value] of table) {
        expect(value.trim(), `${language}: ${key}`).not.toBe("");
      }
    }
  });

  it("uses the same arguments in both languages", () => {
    for (const [key, value] of english) {
      expect(argumentNames(serbian.get(key)!), key).toEqual(argumentNames(value));
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
    for (const [key, value] of serbian) {
      for (const categories of pluralCategories(value)) {
        expect(categories, key).toEqual(expect.arrayContaining(["one", "few", "other"]));
      }
    }
  });

  it("writes Serbian in Latin script only", () => {
    const cyrillic: string[] = [];
    for (const [key, value] of serbian) {
      if (/\p{Script=Cyrillic}/u.test(value)) {
        cyrillic.push(key);
      }
    }

    expect(cyrillic).toEqual([]);
  });
});

describe("choosing a language", () => {
  it("names every language", () => {
    expect(Object.keys(localeNames).sort()).toEqual([...supportedLocales].sort());
  });

  it.each([
    [["sr-Latn-RS", "en"], "sr-Latn"],
    [["sr-Cyrl-RS"], "sr-Latn"],
    [["sr-RS"], "sr-Latn"],
    [["sr"], "sr-Latn"],
    [["en-GB", "sr"], "en"],
    [["de-DE", "sr"], "sr-Latn"],
    [["de-DE", "fr"], "en"],
    [["srn"], "en"],
    [[], "en"],
  ])("picks the best match for %j", (preferred, expected) => {
    expect(pickLocale(preferred)).toBe(expected);
  });

  it("recognizes only supported languages", () => {
    expect(isLocale("sr-Latn")).toBe(true);
    expect(isLocale("sr-Cyrl")).toBe(false);
    expect(isLocale("sr")).toBe(false);
    expect(isLocale(null)).toBe(false);
  });
});
