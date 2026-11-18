import { expect, test, type Page } from "@playwright/test";

import { asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 07-search. Each language uploads into a folder of its own.

/** Chooses files in the system's file chooser, as the person would after "Files on this phone". */
async function chooseFiles(page: Page, say: Say, files: { name: string; buffer: Buffer }[]): Promise<void> {
  await page
    .getByRole("button", { name: say("Upload files", "Otpremi fajlove"), exact: true })
    .filter({ visible: true })
    .click();
  const chooser = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: say("Files on this phone", "Fajlovi sa ovog telefona"), exact: true }).click();
  await (await chooser).setFiles(files.map((f) => ({ ...f, mimeType: "application/octet-stream" })));
}

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`uploads, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("files from the phone into a folder, a taken name, and the list", async ({ page, request, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const folderName = `Uploads ${language}`;
      const ana = await asAna(request);
      await ana.folder(ana.root, folderName);
      const row = (name: string) => page.getByText(name, { exact: true }).filter({ visible: true });

      await signIn(page, baseURL!, say);
      await row(folderName).click();
      await expect(
        page.getByText(say("This folder is empty.", "Ovaj folder je prazan.")).filter({ visible: true }),
      ).toBeVisible();

      // Where to upload from.
      await page
        .getByRole("button", { name: say("Upload files", "Otpremi fajlove"), exact: true })
        .filter({ visible: true })
        .click();
      await expect(page.getByRole("button", { name: say("Take a photo", "Fotografiši"), exact: true })).toBeVisible();
      await shot(page, `${language}-100-upload-from`);
      await page
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .last()
        .click();

      // Two files, one of them several chunks long.
      await chooseFiles(page, say, [
        { name: "summer notes.txt", buffer: Buffer.from("Sea, sun, šljunak.\n") },
        { name: "archive.bin", buffer: Buffer.alloc(12 * 1024 * 1024, 7) },
      ]);
      await expect(row("summer notes.txt")).toBeVisible({ timeout: 60_000 });
      await expect(row("archive.bin")).toBeVisible({ timeout: 60_000 });
      await expect(page.getByText(say("Uploads finished", "Otpremanja su završena"))).toBeVisible();
      await shot(page, `${language}-101-uploaded`);

      // The list of uploads, then cleared.
      await page.getByRole("button", { name: say("Show uploads", "Prikaži otpremanja") }).click();
      await expect(page.getByText(say("Uploaded · 12 MB", "Otpremljeno · 12 MB"))).toBeVisible();
      await shot(page, `${language}-102-uploads-list`);
      await page.getByRole("button", { name: say("Clear", "Očisti"), exact: true }).click();
      await page
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .last()
        .click();
      await expect(page.getByRole("button", { name: say("Show uploads", "Prikaži otpremanja") })).toHaveCount(0);

      // The same name again: asked, and both kept.
      await chooseFiles(page, say, [{ name: "summer notes.txt", buffer: Buffer.from("A second version.\n") }]);
      await expect(page.getByText(say("This name is already taken", "Ovo ime je već zauzeto"))).toBeVisible();
      await shot(page, `${language}-103-name-taken`);
      await page.getByRole("button", { name: say("Keep both", "Zadrži oba"), exact: true }).click();
      await expect(page.getByText(/summer notes.*\.txt/).filter({ visible: true })).toHaveCount(2, { timeout: 30_000 });

      // And replaced: still two, the old content kept as an earlier version.
      await chooseFiles(page, say, [{ name: "summer notes.txt", buffer: Buffer.from("A third version.\n") }]);
      await page.getByRole("button", { name: say("Replace", "Zameni"), exact: true }).click();
      await expect(page.getByText(say("Uploads finished", "Otpremanja su završena"))).toBeVisible();
      await expect(page.getByText(/summer notes.*\.txt/).filter({ visible: true })).toHaveCount(2);

      await signOut(page, say);
    });
  });
}
