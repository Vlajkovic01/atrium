// Generates the Serbian Cyrillic translations from the Latin ones (D-018).
// Run with: pnpm --filter @atrium/shared generate:cyrillic
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { messagesToCyrillic, type MessageTree } from "../src/i18n/transliterate.ts";

const locales = new URL("../src/i18n/locales/", import.meta.url);
const latin = JSON.parse(readFileSync(new URL("sr-Latn.json", locales), "utf8")) as MessageTree;
const target = fileURLToPath(new URL("sr-Cyrl.json", locales));

writeFileSync(target, `${JSON.stringify(messagesToCyrillic(latin), null, 2)}\n`, "utf8");
console.log(`Wrote ${target}`);
