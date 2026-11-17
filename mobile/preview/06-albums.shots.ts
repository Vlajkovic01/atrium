import { expect, test, type Page } from "@playwright/test";

import { shot, signIn, signOut, type Say } from "./helpers";

// Runs after 05-photos, which put the pictures of the "Camera" folder on
// the timeline. Each language makes, changes and deletes an album of its own.

/** A long press, which starts choosing pictures. */
async function longPress(page: Page, name: string): Promise<void> {
  await page.getByRole("button", { name, exact: true }).filter({ visible: true }).click({ delay: 900 });
}

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`albums, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("choose pictures, make an album, change it and delete it", async ({ page, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const albumName = `Holidays ${language}`;
      const visible = (name: string) => page.getByRole("button", { name, exact: true }).filter({ visible: true });

      await signIn(page, baseURL!, say);
      await page.getByRole("tab", { name: say("Photos", "Fotografije") }).click();
      await expect(visible("beach.jpg")).toBeVisible();

      // Choosing starts with a long press; touches then choose more.
      await longPress(page, "beach.jpg");
      await expect(page.getByRole("heading", { name: say("1 item selected", "Izabrana je 1 stavka") })).toBeVisible();
      await visible("dunes.jpg").click();
      await expect(page.getByRole("heading", { name: say("2 items selected", "Izabrane su 2 stavke") })).toBeVisible();
      await shot(page, `${language}-80-chosen`);

      // Into a new album.
      await page.getByRole("button", { name: say("Add to album", "Dodaj u album"), exact: true }).click();
      await expect(page.getByText(say("Add 2 items to an album", "Dodaj 2 stavke u album"))).toBeVisible();
      await shot(page, `${language}-81-add-to-album`);
      await page.getByRole("button", { name: say("New album", "Novi album"), exact: true }).click();
      await page.getByRole("textbox", { name: say("Name", "Ime"), exact: true }).fill(albumName);
      await page.getByRole("button", { name: say("Create album", "Napravi album"), exact: true }).click();
      await expect(
        page.getByText(say(`2 items added to ${albumName}.`, `Dodate su 2 stavke u ${albumName}.`)),
      ).toBeVisible();
      await shot(page, `${language}-82-added`);

      // The albums.
      await page.getByRole("tab", { name: say("Albums", "Albumi") }).click();
      const card = page.getByRole("button", { name: new RegExp(`^${albumName}, `) });
      await expect(card).toBeVisible();
      await shot(page, `${language}-83-albums`);

      // One album, with what is in it.
      await card.click();
      await expect(page.getByRole("heading", { name: albumName })).toBeVisible();
      await expect(visible("beach.jpg")).toBeVisible();
      await expect(visible("dunes.jpg")).toBeVisible();
      await shot(page, `${language}-84-album`);

      // A cover, chosen among its pictures.
      await longPress(page, "dunes.jpg");
      await page.getByRole("button", { name: say("Make cover", "Postavi za naslovnu"), exact: true }).click();
      await expect(page.getByRole("heading", { name: albumName })).toBeVisible();

      // Taken out of the album, not deleted.
      await longPress(page, "beach.jpg");
      await page.getByRole("button", { name: say("Remove from album", "Ukloni iz albuma"), exact: true }).click();
      await expect(visible("beach.jpg")).toHaveCount(0);
      await expect(
        page.getByText(say("1 item", "1 stavka"), { exact: false }).filter({ visible: true }).first(),
      ).toBeVisible();

      // Renamed.
      await page.getByRole("button", { name: say("Actions", "Radnje"), exact: true }).click();
      await page.getByRole("button", { name: say("Rename", "Preimenuj"), exact: true }).click();
      await page.getByRole("textbox", { name: say("Name", "Ime"), exact: true }).fill(`${albumName}, summer`);
      await page.getByRole("button", { name: say("Save", "Sačuvaj"), exact: true }).click();
      await expect(page.getByRole("heading", { name: `${albumName}, summer` })).toBeVisible();

      // Deleted, after saying that the pictures stay.
      await page.getByRole("button", { name: say("Actions", "Radnje"), exact: true }).click();
      await page.getByRole("button", { name: say("Delete album", "Obriši album"), exact: true }).click();
      await expect(
        page.getByText(say("Only the album is deleted.", "Briše se samo album."), { exact: false }),
      ).toBeVisible();
      await shot(page, `${language}-85-delete-album`);
      await page
        .getByRole("button", { name: say("Delete album", "Obriši album"), exact: true })
        .last()
        .click();
      await expect(page.getByRole("button", { name: new RegExp(`^${albumName}`) })).toHaveCount(0);

      // The pictures are still on the timeline.
      await page.getByRole("tab", { name: say("Timeline", "Vremenska linija") }).click();
      await expect(visible("beach.jpg")).toBeVisible();
      await expect(visible("dunes.jpg")).toBeVisible();

      await signOut(page, say);
    });
  });
}
