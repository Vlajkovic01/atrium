import { expect, test } from "@playwright/test";

import { shot, signIn, signOut, type Say } from "./helpers";

// Runs after 02-files and 05-photos, which put files, folders and dated
// pictures on the server.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`search, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("names, kinds and dates, from Photos and from Files", async ({ page, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const field = page.getByRole("textbox", {
        name: say("Search names, types and dates", "Pretraga po imenu, vrsti i datumu"),
      });
      const visible = (name: string) => page.getByRole("button", { name, exact: true }).filter({ visible: true });

      await signIn(page, baseURL!, say);

      // From Files: before anything is typed, the field says what it can do.
      // The search button of this screen's own top bar; the Photos tab keeps one of its own.
      await page
        .getByRole("heading", { name: say("My files", "Moji fajlovi") })
        .filter({ visible: true })
        .locator("xpath=..")
        .getByRole("button", { name: say("Search", "Pretraga"), exact: true })
        .click();
      await expect(field).toBeFocused();
      await expect(page.getByText(say("Type part of a name", "Upišite deo imena"), { exact: false })).toBeVisible();
      await shot(page, `${language}-90-search`);

      // A name: a picture and a file.
      await field.fill("beach");
      await expect(visible("beach.jpg")).toBeVisible();
      await shot(page, `${language}-91-search-name`);

      // A month, in the language's own words: a range, and the pictures of that month only.
      await field.fill(say("September 2026", "septembar 2026"));
      await expect(page.getByText(/2026.*–.*2026/)).toBeVisible();
      await expect(visible("winter.jpg")).toBeVisible();
      await expect(visible("beach.jpg")).toHaveCount(0);
      await shot(page, `${language}-92-search-month`);

      // A kind with a word: documents, as a list that opens in the viewer.
      await field.fill("rental");
      await page.getByRole("button", { name: say("Documents", "Dokumenti"), exact: true }).click();
      await expect(visible("Rental agreement.pdf")).toBeVisible();
      await shot(page, `${language}-93-search-documents`);
      await visible("Rental agreement.pdf").click();
      await expect(page.getByRole("dialog").last().getByText("Rental agreement.pdf")).toBeVisible();
      await page
        .getByRole("dialog")
        .last()
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .first()
        .click();

      // A folder opens in Files.
      await page.getByRole("button", { name: say("Documents", "Dokumenti"), exact: true }).click();
      await page.getByRole("button", { name: say("Folders", "Folderi"), exact: true }).click();
      await field.fill("Taxes");
      await visible("Taxes").click();
      await expect(page.getByRole("heading", { name: "Taxes" }).filter({ visible: true })).toBeVisible();
      await expect(page.getByText(/2026 with a rather long/).filter({ visible: true })).toBeVisible();

      // Nothing found says so.
      await page.getByRole("tab", { name: say("Photos", "Fotografije") }).click();
      await page
        .getByRole("heading", { name: say("Photos", "Fotografije"), exact: true })
        .filter({ visible: true })
        .locator("xpath=..")
        .getByRole("button", { name: say("Search", "Pretraga"), exact: true })
        .click();
      await field.fill("nothing like this");
      await expect(page.getByText(say("Nothing found.", "Ništa nije pronađeno."))).toBeVisible();
      await shot(page, `${language}-94-search-nothing`);
      await page
        .getByRole("button", { name: say("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .click();

      await signOut(page, say);
    });
  });
}
