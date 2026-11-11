/**
 * Serbian Latin to Cyrillic (D-018). Translators maintain the Latin text;
 * the Cyrillic file is generated from it with these functions, so the two
 * scripts can never drift apart.
 *
 * The conversion is mechanical except for three things handled here:
 * - words that stay in Latin script (product names, abbreviations),
 * - words where "nj", "lj" or "dž" are two separate sounds,
 * - ICU message syntax, which must not be touched.
 *
 * Rule for translators: never write a command, a path or a URL inside a
 * message. Pass it as an argument ({command}), and it is left alone.
 */

const DIGRAPHS: readonly (readonly [string, string])[] = [
  ["Lj", "Љ"],
  ["LJ", "Љ"],
  ["lj", "љ"],
  ["Nj", "Њ"],
  ["NJ", "Њ"],
  ["nj", "њ"],
  ["Dž", "Џ"],
  ["DŽ", "Џ"],
  ["dž", "џ"],
];

const LETTERS: Readonly<Record<string, string>> = {
  a: "а",
  b: "б",
  c: "ц",
  č: "ч",
  ć: "ћ",
  d: "д",
  đ: "ђ",
  e: "е",
  f: "ф",
  g: "г",
  h: "х",
  i: "и",
  j: "ј",
  k: "к",
  l: "л",
  m: "м",
  n: "н",
  o: "о",
  p: "п",
  r: "р",
  s: "с",
  š: "ш",
  t: "т",
  u: "у",
  v: "в",
  z: "з",
  ž: "ж",
  A: "А",
  B: "Б",
  C: "Ц",
  Č: "Ч",
  Ć: "Ћ",
  D: "Д",
  Đ: "Ђ",
  E: "Е",
  F: "Ф",
  G: "Г",
  H: "Х",
  I: "И",
  J: "Ј",
  K: "К",
  L: "Л",
  M: "М",
  N: "Н",
  O: "О",
  P: "П",
  R: "Р",
  S: "С",
  Š: "Ш",
  T: "Т",
  U: "У",
  V: "В",
  Z: "З",
  Ž: "Ж",
};

/** Words written in Latin script in Cyrillic text too. Matched as whole words, case-sensitive. */
export const KEEP_LATIN: ReadonlySet<string> = new Set([
  "Atrium",
  "QR",
  "TOTP",
  "PIN",
  "URL",
  "HTTP",
  "HTTPS",
  "IP",
  "TV",
  "S3",
  "AWS",
  "Amazon",
  "Backblaze",
  "B2",
  "Cloudflare",
  "R2",
  "MinIO",
  "Docker",
  "Compose",
  "Android",
  "Apple",
  "Google",
  "Wi",
  "Fi",
  "GB",
  "MB",
  "KB",
  "TB",
  // Technical terms with no settled Serbian word. Written with a hyphen
  // before a case ending (bucket-a), so the ending is converted and the
  // term is not.
  "bucket",
  "Bucket",
  "reverse",
  "proxy",
]);

/**
 * Stems in which a letter pair is two sounds, not one: "injekcija" is
 * и-н-ј-е, not и-њ-е. Lowercase; matched anywhere in a word.
 * Extend this list when a new such word enters the translations; the
 * native-speaker review before 1.0 (D-018) checks it.
 */
export const SPLIT_DIGRAPH_STEMS: readonly string[] = [
  "injekc",
  "injunk",
  "konjug",
  "konjunk",
  "tanjug",
  "vanjezi",
  "nadživ",
  "nadžnj",
  "odžive",
  "podžup",
];

const PAIR = /nj|lj|dž/;

// Positions in the word where a letter pair must be read as two letters.
function splitPositions(word: string): ReadonlySet<number> {
  const positions = new Set<number>();
  const lower = word.toLowerCase();
  for (const stem of SPLIT_DIGRAPH_STEMS) {
    const pair = PAIR.exec(stem);
    if (!pair) {
      continue;
    }

    for (let at = lower.indexOf(stem); at >= 0; at = lower.indexOf(stem, at + 1)) {
      positions.add(at + pair.index);
    }
  }

  return positions;
}

/** Converts one word, with no regard for the keep-Latin list. */
export function wordToCyrillic(word: string): string {
  const split = splitPositions(word);
  let out = "";
  for (let i = 0; i < word.length;) {
    const two = word.slice(i, i + 2);
    const digraph = split.has(i) ? undefined : DIGRAPHS.find(([latin]) => latin === two);
    if (digraph) {
      out += digraph[1];
      i += 2;
    } else {
      const c = word[i]!;
      out += LETTERS[c] ?? c;
      i++;
    }
  }

  return out;
}
const WORD = /[\p{L}\p{N}]+/gu;

/** Converts plain text: every word except those that stay in Latin script. */
export function textToCyrillic(text: string): string {
  return text.replace(WORD, (word) => (KEEP_LATIN.has(word) ? word : wordToCyrillic(word)));
}

function matchingBrace(text: string, open: number): number {
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === "{") {
      depth++;
    } else if (text[i] === "}") {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }

  throw new Error(`Unbalanced braces in message: ${text}`);
}

function topLevelComma(text: string, from: number): number {
  let depth = 0;
  for (let i = from; i < text.length; i++) {
    const c = text[i];
    if (c === "{") {
      depth++;
    } else if (c === "}") {
      depth--;
    } else if (c === "," && depth === 0) {
      return i;
    }
  }

  return -1;
}

const BRANCHING = new Set(["plural", "select", "selectordinal"]);

// `inner` is what stands between the braces of one ICU argument.
function argumentToCyrillic(inner: string): string {
  const first = topLevelComma(inner, 0);
  if (first < 0) {
    return `{${inner}}`; // {name}
  }

  const second = topLevelComma(inner, first + 1);
  const type = inner.slice(first + 1, second < 0 ? undefined : second).trim();
  if (second < 0 || !BRANCHING.has(type)) {
    return `{${inner}}`; // {count, number} and the like
  }

  // name, plural, one {...} few {...} other {...}
  // Selectors stay as they are; each branch is a message of its own.
  let out = inner.slice(0, second + 1);
  const options = inner.slice(second + 1);
  for (let i = 0; i < options.length;) {
    if (options[i] === "{") {
      const end = matchingBrace(options, i);
      out += `{${messageToCyrillic(options.slice(i + 1, end))}}`;
      i = end + 1;
    } else {
      out += options[i];
      i++;
    }
  }

  return `{${out}}`;
}

/**
 * Converts one ICU message. Argument names, plural and select keywords,
 * tags and quoted sections are left exactly as they are; only the text a
 * person reads is converted.
 */
export function messageToCyrillic(message: string): string {
  let out = "";
  let literal = "";
  const flush = (): void => {
    out += textToCyrillic(literal);
    literal = "";
  };

  for (let i = 0; i < message.length;) {
    const c = message[i]!;
    if (c === "{") {
      flush();
      const end = matchingBrace(message, i);
      out += argumentToCyrillic(message.slice(i + 1, end));
      i = end + 1;
    } else if (c === "<") {
      // A tag such as <strong> or </strong>.
      const end = message.indexOf(">", i);
      if (end < 0) {
        literal += c;
        i++;
      } else {
        flush();
        out += message.slice(i, end + 1);
        i = end + 1;
      }
    } else if (c === "'" && (message[i + 1] === "{" || message[i + 1] === "}")) {
      // ICU quoting: '{literal braces}' up to the closing apostrophe.
      const end = message.indexOf("'", i + 1);
      flush();
      out += message.slice(i, end < 0 ? message.length : end + 1);
      i = end < 0 ? message.length : end + 1;
    } else {
      literal += c;
      i++;
    }
  }

  flush();
  return out;
}

export type MessageTree = { readonly [key: string]: string | MessageTree };

/** Converts every message in a translation file, keeping its keys and structure. */
export function messagesToCyrillic(tree: MessageTree): MessageTree {
  const out: Record<string, string | MessageTree> = {};
  for (const [key, value] of Object.entries(tree)) {
    out[key] = typeof value === "string" ? messageToCyrillic(value) : messagesToCyrillic(value);
  }

  return out;
}
