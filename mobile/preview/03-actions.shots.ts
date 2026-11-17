import { expect, test, type Page } from "@playwright/test";

import { asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 02-files. Each language works in a folder of its own, so the
// two runs do not see each other's changes.

/** The screen in front; screens under it stay in the page. */
const visible = (page: Page) => ({
  text: (text: string) => page.getByText(text, { exact: true }).filter({ visible: true }),
  button: (name: string) => page.getByRole("button", { name, exact: true }).filter({ visible: true }),
});

/** The folder picker or another dialog in front, where the screens under it cannot be hit by mistake. */
const dialog = (page: Page) => page.getByRole("dialog").last();

/** A long press, which starts choosing several items. */
async function longPress(page: Page, text: string): Promise<void> {
  await visible(page).text(text).click({ delay: 900 });
}

async function openMenu(page: Page, say: Say, name: string): Promise<void> {
  await visible(page)
    .button(`${say("Actions", "Radnje")}: ${name}`)
    .click();
}

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`file actions, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("new folder, rename, move, copy, trash and back", async ({ page, request, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const at = visible(page);
      const work = `Actions ${language}`;

      const ana = await asAna(request);
      const workId = await ana.folder(ana.root, work);
      await ana.folder(workId, "Target");
      for (const name of ["a.txt", "b.txt", "c.txt"]) {
        await ana.upload(workId, name, Buffer.from(name));
      }

      await signIn(page, baseURL!, say);
      await at.text(work).click();
      await expect(at.text("a.txt")).toBeVisible();

      // A new folder.
      await at.button(say("New folder", "Novi folder")).click();
      await page.getByRole("textbox", { name: say("Name", "Ime"), exact: true }).fill("Reports");
      await shot(page, `${language}-40-new-folder`);
      await page.getByRole("button", { name: say("Create", "Napravi"), exact: true }).click();
      await expect(at.text("Reports")).toBeVisible();

      // The same name again: the server's reason, in the dialog, which stays open.
      await at.button(say("New folder", "Novi folder")).click();
      await page.getByRole("textbox", { name: say("Name", "Ime"), exact: true }).fill("Reports");
      await page.getByRole("button", { name: say("Create", "Napravi"), exact: true }).click();
      await expect(page.getByRole("alert").filter({ hasText: /./ }).last()).toBeVisible();
      await shot(page, `${language}-41-name-taken`);
      await page.getByRole("button", { name: say("Cancel", "Otkaži"), exact: true }).click();

      // Rename through the item's own menu.
      await openMenu(page, say, "a.txt");
      await shot(page, `${language}-42-item-menu`);
      await page.getByRole("button", { name: say("Rename", "Preimenuj"), exact: true }).click();
      const name = page.getByRole("textbox", { name: say("Name", "Ime"), exact: true });
      await expect(name).toHaveValue("a.txt");
      await shot(page, `${language}-43-rename`);
      await name.fill("alpha.txt");
      await page
        .getByRole("button", { name: say("Rename", "Preimenuj"), exact: true })
        .last()
        .click();
      await expect(at.text("alpha.txt")).toBeVisible();
      await expect(at.text("a.txt")).toHaveCount(0);

      // Choosing several items starts with a long press.
      await longPress(page, "b.txt");
      await expect(page.getByRole("heading", { name: say("1 item selected", "Izabrana je 1 stavka") })).toBeVisible();
      await at.text("c.txt").click();
      await expect(page.getByRole("heading", { name: say("2 items selected", "Izabrane su 2 stavke") })).toBeVisible();
      await shot(page, `${language}-44-selected`);

      // Move both into a folder below.
      await at.button(say("Move", "Premesti")).click();
      await expect(page.getByText(say("Move 2 items to…", "Premesti 2 stavke u…"))).toBeVisible();
      await dialog(page).getByText("Target", { exact: true }).click();
      await expect(page.getByText(say("No folders here.", "Ovde nema foldera."))).toBeVisible();
      await shot(page, `${language}-45-move-picker`);
      await page.getByRole("button", { name: say("Move here", "Premesti ovde"), exact: true }).click();
      await expect(at.text("b.txt")).toHaveCount(0);
      await expect(at.text("c.txt")).toHaveCount(0);

      // Copy one back up, picking the folder above in the path.
      await at.text("Target").click();
      await expect(at.text("b.txt")).toBeVisible();
      await openMenu(page, say, "c.txt");
      await page.getByRole("button", { name: say("Copy", "Kopiraj"), exact: true }).click();
      await dialog(page).getByRole("link", { name: work, exact: true }).click();
      await page.getByRole("button", { name: say("Copy here", "Kopiraj ovde"), exact: true }).click();
      await expect(at.text("c.txt")).toBeVisible();
      await at.button(say("Back", "Nazad")).click();
      await expect(at.text("c.txt")).toBeVisible();

      // To the trash, and undone at once.
      await openMenu(page, say, "alpha.txt");
      await page.getByRole("button", { name: say("Move to trash", "Premesti u korpu"), exact: true }).click();
      await expect(at.text("alpha.txt")).toHaveCount(0);
      await expect(page.getByText(say("1 item moved to the trash.", "1 stavka je premeštena u korpu."))).toBeVisible();
      await shot(page, `${language}-46-undo`);
      await page.getByRole("button", { name: say("Undo", "Poništi"), exact: true }).click();
      await expect(at.text("alpha.txt")).toBeVisible();

      // To the trash for real: a file in a folder, then the folder itself.
      await at.text("Target").click();
      await openMenu(page, say, "b.txt");
      await page.getByRole("button", { name: say("Move to trash", "Premesti u korpu"), exact: true }).click();
      await expect(at.text("b.txt")).toHaveCount(0);
      await at.button(say("Back", "Nazad")).click();
      await openMenu(page, say, "Target");
      await page.getByRole("button", { name: say("Move to trash", "Premesti u korpu"), exact: true }).click();
      await expect(at.text("Target")).toHaveCount(0);

      // The trash says where each item was.
      await at.button(say("Back", "Nazad")).click();
      await at.button(say("Trash", "Korpa")).click();
      await expect(at.text("b.txt")).toBeVisible();
      await expect(at.text("Target")).toBeVisible();
      // The top folder is called as everywhere else in the app, not by its stored name.
      await expect(
        at.text(`${say("From", "Iz")}: ${say("My files", "Moji fajlovi")} › ${work} › Target`),
      ).toBeVisible();
      await shot(page, `${language}-47-trash`);

      // The folder it came from is in the trash too: the app asks where to put it.
      await at.text("b.txt").click();
      await page.getByRole("button", { name: say("Restore", "Vrati"), exact: true }).click();
      await expect(page.getByRole("button", { name: say("Restore here", "Vrati ovde"), exact: true })).toBeVisible();
      await dialog(page).getByText(work, { exact: true }).click();
      await shot(page, `${language}-48-restore-to`);
      await page.getByRole("button", { name: say("Restore here", "Vrati ovde"), exact: true }).click();
      await expect(at.text("b.txt")).toHaveCount(0);

      // Removing for good asks first and says how much goes: the folder and the file still in it.
      await at.text("Target").click();
      await page.getByRole("button", { name: say("Delete permanently", "Obriši trajno"), exact: true }).click();
      await expect(page.getByText(say("2 items", "2 stavke"), { exact: false }).last()).toBeVisible();
      await shot(page, `${language}-49-delete-confirm`);
      await page
        .getByRole("button", { name: say("Delete permanently", "Obriši trajno"), exact: true })
        .last()
        .click();
      await expect(at.text(say("The trash is empty.", "Korpa je prazna."))).toBeVisible();
      await shot(page, `${language}-50-trash-empty`);

      // The restored file is back where it was put.
      await at.button(say("Back", "Nazad")).click();
      await at.text(work).click();
      await expect(at.text("b.txt")).toBeVisible();
      await expect(at.text("c.txt")).toBeVisible();
      await expect(at.text("alpha.txt")).toBeVisible();
      await expect(at.text("Reports")).toBeVisible();
      await at.button(say("Back", "Nazad")).click();

      // Where nothing can be changed, nothing is offered but the details.
      await page.getByRole("button", { name: "Family archive", exact: true }).click();
      await expect(at.text("readme.txt")).toBeVisible();
      await expect(at.button(say("New folder", "Novi folder"))).toHaveCount(0);
      await openMenu(page, say, "readme.txt");
      await expect(page.getByRole("button", { name: say("Rename", "Preimenuj"), exact: true })).toHaveCount(0);
      await expect(page.getByRole("button", { name: say("Details", "Detalji"), exact: true })).toBeVisible();
      await shot(page, `${language}-51-read-only-menu`);
      await page
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .last()
        .click();
      await longPress(page, "readme.txt");
      await expect(page.getByRole("heading", { name: say("1 item selected", "Izabrana je 1 stavka") })).toHaveCount(0);
      // Nothing to choose here, so a long press does what a touch does: it shows the details.
      await expect(page.getByRole("heading", { name: "readme.txt" })).toBeVisible();
      await page
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .last()
        .click();

      await signOut(page, say);
    });
  });
}
