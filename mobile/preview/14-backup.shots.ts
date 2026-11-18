import { expect, test } from "@playwright/test";

import { asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 13-lock. In the browser the "camera roll" is six made-up
// pictures (four in Camera, two in Screenshots), the same in every run.
// The first language backs them up; the second finds them all on the
// server already, by their content, and sends nothing.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`camera backup, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("turning it on, backing up, and nothing sent twice", async ({ page, request, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const ana = await asAna(request);

      await signIn(page, baseURL!, say);
      await page.getByRole("tab", { name: new RegExp(say("Account", "Nalog")) }).click();
      await page.getByRole("button", { name: say("Camera backup", "Rezervna kopija kamere"), exact: true }).click();
      await expect(
        page.getByText(say("Nothing is ever deleted from this phone", "Rezervna kopija nikad ništa ne briše"), {
          exact: false,
        }),
      ).toBeVisible();
      await shot(page, `${language}-160-backup-off`);

      await page
        .getByRole("button", { name: say("Turn on camera backup", "Uključi rezervnu kopiju kamere"), exact: true })
        .click();
      await expect(page.getByText(say("Everything chosen is backed up", "Sve izabrano je kopirano"))).toBeVisible({
        timeout: 60_000,
      });
      await expect(page.getByText(say("6 of 6 on the server", "6 od 6 na serveru"))).toBeVisible();
      await expect(page.getByText(say("Last finished", "Poslednji put završeno"), { exact: false })).toBeVisible();
      await expect(
        page.getByText(say("Android lets backup run in the background", "Android pušta rezervnu kopiju"), {
          exact: false,
        }),
      ).toBeVisible();
      await shot(page, `${language}-161-backup-done`);

      // Only one album: the counts follow.
      await page.getByRole("checkbox", { name: "Camera", exact: true }).click();
      await expect(page.getByText(say("4 of 4 on the server", "4 od 4 na serveru"))).toBeVisible({ timeout: 30_000 });
      await shot(page, `${language}-162-backup-camera-only`);

      // On the server: one folder for this device, holding each picture once, whichever run sent it.
      const top = (await ana.children(ana.root)).find((n) => n.name === say("Camera backup", "Rezervna kopija kamere"));
      const any = top ?? (await ana.children(ana.root)).find((n) => n.name === "Camera backup")!;
      const device = (await ana.children(any.id)).find((n) => n.name === "Atrium app")!;
      expect((await ana.children(device.id)).length).toBe(6);

      await page
        .getByRole("button", { name: say("Turn off camera backup", "Isključi rezervnu kopiju kamere"), exact: true })
        .click();
      await expect(
        page.getByText(say("Turn on camera backup", "Uključi rezervnu kopiju kamere"), { exact: true }),
      ).toBeVisible();
      await page
        .getByRole("button", { name: say("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .first()
        .click();
      await signOut(page, say);
    });
  });
}
