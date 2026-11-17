import { expect, test } from "@playwright/test";

import { asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 10-sync. Each language keeps a folder of its own on the
// "phone" (here: the page's memory), opens it without a connection, and
// sees a file added on the server arrive by itself.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`offline, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("a folder kept on the phone, opened without a connection, and kept in step", async ({
      page,
      request,
      baseURL,
    }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const ana = await asAna(request);
      const folderName = `Offline ${language}`;
      const folderId = await ana.folder(ana.root, folderName);
      await ana.upload(folderId, "trip plan.txt", Buffer.from("Day one: the coast.\nDay two: the hills.\n"));
      // Several parts long, so fetching goes part by part.
      await ana.upload(folderId, "maps.bin", Buffer.alloc(9 * 1024 * 1024, 3));

      await signIn(page, baseURL!, say);

      // Kept on this phone from the folder's menu.
      await page.getByRole("button", { name: `${say("Actions", "Radnje")}: ${folderName}`, exact: true }).click();
      await shot(page, `${language}-130-keep`);
      await page
        .getByRole("button", { name: say("Keep on this phone", "Zadrži na ovom telefonu"), exact: true })
        .click();

      // The downloads, until the folder is whole.
      await page.getByRole("tab", { name: new RegExp(say("Account", "Nalog")) }).click();
      await page.getByRole("button", { name: say("Downloads", "Preuzimanja"), exact: true }).click();
      // The card on the Downloads screen, named with its state; the folder's row in Files is named with its date.
      const states = say("On this phone|Downloading|Reading", "Na ovom telefonu|Preuzima se|Čita se");
      const card = page.getByRole("button", { name: new RegExp(`^${folderName}, (${states})`) });
      await expect(card).toBeVisible();
      await expect(card).toHaveAccessibleName(new RegExp(say("On this phone", "Na ovom telefonu")), {
        timeout: 60_000,
      });
      await expect(
        page.getByText(new RegExp(say("on this phone, 2 files", "na ovom telefonu, 2 fajla"))),
      ).toBeVisible();
      await shot(page, `${language}-131-downloads`);

      // Without a connection: the copy opens.
      await page.route("**/api/**", (route) => route.abort());
      await card.click();
      await page.getByRole("button", { name: "trip plan.txt", exact: true }).click();
      await expect(page.getByText("Day two: the hills.", { exact: false })).toBeVisible();
      await shot(page, `${language}-132-offline-open`);
      await page
        .getByRole("dialog")
        .last()
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .first()
        .click();
      await page.unroute("**/api/**");

      // A file added on the server arrives by itself, through the change feed.
      await ana.upload(folderId, "tickets.txt", Buffer.from("Seat 12A"));
      await expect(page.getByText(new RegExp(say("on this phone, 3 files", "na ovom telefonu, 3 fajla")))).toBeVisible({
        timeout: 60_000,
      });

      // Removed from the phone, not from the server.
      await page
        .getByRole("button", { name: `${say("Remove from this phone", "Ukloni sa ovog telefona")}: ${folderName}` })
        .click();
      await expect(card).toHaveCount(0);
      await expect(
        page.getByText(say("Nothing is kept on this phone yet.", "Na ovom telefonu još ništa nije zadržano."), {
          exact: false,
        }),
      ).toBeVisible();
      await shot(page, `${language}-133-removed`);
      expect((await ana.children(folderId)).map((c) => c.name).sort()).toEqual([
        "maps.bin",
        "tickets.txt",
        "trip plan.txt",
      ]);

      await page
        .getByRole("button", { name: say("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .click();
      await signOut(page, say);
    });
  });
}
