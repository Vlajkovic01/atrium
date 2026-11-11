import { describe, expect, it } from "vitest";

import { messageToCyrillic, messagesToCyrillic, textToCyrillic, wordToCyrillic } from "../src/i18n/transliterate.ts";

describe("words", () => {
  it.each([
    ["uređaji", "уређаји"],
    ["Uređaji", "Уређаји"],
    ["šifrovana", "шифрована"],
    ["Čačak", "Чачак"],
    ["ćirilica", "ћирилица"],
    ["žurka", "журка"],
    ["Đorđe", "Ђорђе"],
  ])("converts %s", (latin, cyrillic) => {
    expect(wordToCyrillic(latin)).toBe(cyrillic);
  });

  it.each([
    ["ljubav", "љубав"],
    ["Ljubljana", "Љубљана"],
    ["LJUBAV", "ЉУБАВ"],
    ["njegov", "његов"],
    ["Njegoš", "Његош"],
    ["konj", "коњ"],
    ["džep", "џеп"],
    ["Džep", "Џеп"],
    ["DŽEP", "ЏЕП"],
    ["učitavanje", "учитавање"],
    ["podešavanje", "подешавање"],
  ])("reads the letter pair in %s as one letter", (latin, cyrillic) => {
    expect(wordToCyrillic(latin)).toBe(cyrillic);
  });

  it.each([
    ["injekcija", "инјекција"],
    ["Injekcija", "Инјекција"],
    ["konjunkcija", "конјункција"],
    ["konjugacija", "конјугација"],
    ["nadživeti", "надживети"],
    ["Tanjug", "Танјуг"],
  ])("reads the letter pair in %s as two letters", (latin, cyrillic) => {
    expect(wordToCyrillic(latin)).toBe(cyrillic);
  });

  it("leaves text that is already Cyrillic unchanged", () => {
    expect(wordToCyrillic("уређаји")).toBe("уређаји");
  });
});

describe("text", () => {
  it("keeps punctuation, digits and spacing", () => {
    expect(textToCyrillic("Učitavanje… (2 od 10), molimo sačekajte!")).toBe("Учитавање… (2 од 10), молимо сачекајте!");
  });

  it.each([
    ["Atrium server", "Atrium сервер"],
    ["QR kod", "QR код"],
    ["Prikažite novi QR kod", "Прикажите нови QR код"],
    ["S3 bucket na AWS", "S3 bucket на AWS"],
    ["Folder unutar bucket-a", "Фолдер унутар bucket-а"],
    ["reverse proxy koji već imate", "reverse proxy који већ имате"],
    ["Android TV", "Android TV"],
    ["Wi-Fi mreža", "Wi-Fi мрежа"],
  ])("keeps names and abbreviations in Latin script: %s", (latin, cyrillic) => {
    expect(textToCyrillic(latin)).toBe(cyrillic);
  });

  it("matches kept words whole and case-sensitively", () => {
    // "atrium" in lower case and "QRkod" are ordinary words, not the names.
    expect(textToCyrillic("atrium")).toBe("атриум");
    expect(textToCyrillic("Atriumov")).toBe("Атриумов");
  });
});

describe("ICU messages", () => {
  it("leaves argument names alone", () => {
    expect(messageToCyrillic("Zdravo, {displayName}!")).toBe("Здраво, {displayName}!");
  });

  it("leaves formatted arguments alone", () => {
    expect(messageToCyrillic("Slobodno: {free, number} od {total, number}")).toBe(
      "Слободно: {free, number} од {total, number}",
    );
  });

  it("converts the text of plural branches and keeps the keywords", () => {
    expect(
      messageToCyrillic("Pokušajte ponovo za {seconds, plural, one {# sekundu} few {# sekunde} other {# sekundi}}."),
    ).toBe("Покушајте поново за {seconds, plural, one {# секунду} few {# секунде} other {# секунди}}.");
  });

  it("converts the text of select branches and keeps the selectors", () => {
    expect(messageToCyrillic("{kind, select, tv {Televizor} mobile {Telefon} other {Uređaj}}")).toBe(
      "{kind, select, tv {Телевизор} mobile {Телефон} other {Уређај}}",
    );
  });

  it("handles arguments nested inside branches", () => {
    expect(messageToCyrillic("{count, plural, one {{name} ima # fajl} other {{name} ima # fajlova}}")).toBe(
      "{count, plural, one {{name} има # фајл} other {{name} има # фајлова}}",
    );
  });

  it("keeps explicit plural values and offsets", () => {
    expect(messageToCyrillic("{n, plural, offset:1 =0 {niko} one {jedan} other {# drugih}}")).toBe(
      "{n, plural, offset:1 =0 {нико} one {један} other {# других}}",
    );
  });

  it("leaves tags alone and converts the text between them", () => {
    expect(messageToCyrillic("Odobrite <strong>samo uređaj</strong> ispred vas.")).toBe(
      "Одобрите <strong>само уређај</strong> испред вас.",
    );
  });

  it("leaves quoted sections alone", () => {
    expect(messageToCyrillic("Koristite '{ime}' kao primer")).toBe("Користите '{ime}' као пример");
  });

  it("converts an ordinary apostrophe as text", () => {
    expect(messageToCyrillic("Ne mož' biti")).toBe("Не мож' бити");
  });

  it("rejects a message with unbalanced braces instead of guessing", () => {
    expect(() => messageToCyrillic("Zdravo, {name")).toThrow(/Unbalanced/);
  });
});

describe("message files", () => {
  it("keeps keys and structure and converts every message", () => {
    expect(messagesToCyrillic({ common: { save: "Sačuvaj", nested: { back: "Nazad" } }, top: "Gotovo" })).toEqual({
      common: { save: "Сачувај", nested: { back: "Назад" } },
      top: "Готово",
    });
  });
});
