import { expect, test, type APIRequestContext } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { SERVER, asAna, shot, signIn, signOut } from "./helpers";

// Runs after 01-connect, which sets the server up and leaves the admin
// "ana" with an empty library.

/** Files of many kinds, nested folders, a folder with more than one page, and a folder of the server shown as it is. */
async function seed(request: APIRequestContext): Promise<void> {
  const { headers, userId, root, children, folder, upload } = await asAna(request);
  if ((await children(root)).length > 0) {
    return;
  }

  const documents = await folder(root, "Documents");
  await folder(root, "Holiday pictures");
  const taxes = await folder(documents, "Taxes");
  const year = await folder(taxes, "2026 with a rather long folder name that does not fit");
  await upload(year, "summary.txt", Buffer.from("Nothing owed."));

  await upload(root, "Rental agreement.pdf", Buffer.from("%PDF-1.4\n%%EOF\n"));
  await upload(root, "Budget.xlsx", Buffer.alloc(48_000, 1));
  await upload(root, "notes.md", Buffer.from("# Notes\n"));
  await upload(root, "backup.tar.gz", Buffer.alloc(3_500_000, 2));
  await upload(root, "voice memo.mp3", Buffer.alloc(900_000, 3));
  await upload(
    root,
    "A file whose name is long enough to need a second line on a phone screen, and then some more.txt",
    Buffer.from("x"),
  );
  await upload(root, "program.bin", Buffer.alloc(10, 4));

  // More than one page (the app asks for 200 at a time).
  const many = await folder(root, "Many files");
  for (let i = 1; i <= 230; i++) {
    await upload(many, `file ${String(i).padStart(3, "0")}.txt`, Buffer.from(String(i)));
  }

  // A folder that already exists on the server: shown as it is, nothing in it can be changed.
  const shared = join(process.env.ATRIUM_E2E_ROOT!, "existing");
  mkdirSync(join(shared, "Scans"), { recursive: true });
  writeFileSync(join(shared, "Scans", "passport.txt"), "scan");
  writeFileSync(join(shared, "readme.txt"), "hello");
  const library = await request.post(`${SERVER}/api/v1/admin/libraries`, {
    headers,
    data: { name: "Family archive", path: shared, userIds: [userId], showInTimeline: false },
  });
  expect(library.ok(), await library.text()).toBe(true);
}

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`files, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("browsing folders, libraries and orders", async ({ page, request, baseURL }) => {
      await seed(request);
      const text = (en: string, sr: string) => (language === "en" ? en : sr);

      await signIn(page, baseURL!, text);

      // The app opens on Files, at the person's own files.
      await expect(page.getByRole("heading", { name: text("My files", "Moji fajlovi") })).toBeVisible();
      await expect(page.getByText("Rental agreement.pdf")).toBeVisible();
      await shot(page, `${language}-20-files`);

      // Folders come first, then names A to Z.
      const names = page.getByRole("button").filter({ hasText: /Documents|Budget|notes/ });
      await expect(names.first()).toContainText("Documents");

      // Another order: asked of the server, remembered by the app.
      await page
        .getByRole("button", { name: new RegExp(text("^Sort by", "^Poređaj po")) })
        .filter({ visible: true })
        .click();
      await shot(page, `${language}-21-sort`);
      await page.getByRole("button", { name: text("Largest first", "Najveće prvo") }).click();
      await expect(
        page
          .getByRole("button")
          .filter({ hasText: /\.(pdf|xlsx|gz|mp3|md|bin|txt)/ })
          .first(),
      ).toContainText("backup.tar.gz");
      await shot(page, `${language}-22-sorted-by-size`);

      // What a file is.
      await page.getByText("backup.tar.gz").click();
      await expect(page.getByText(text("Archive", "Arhiva"), { exact: true })).toBeVisible();
      await shot(page, `${language}-23-details`);
      await page
        .getByRole("button", { name: text("Close", "Zatvori") })
        .last()
        .click();

      // Down three folders; the way back up is shown.
      await page.getByText("Documents").click();
      await page.getByText("Taxes").click();
      await page.getByText(/2026 with a rather long/).click();
      await expect(page.getByText("summary.txt")).toBeVisible();
      await shot(page, `${language}-24-nested`);

      // Up by the path, two folders at once.
      await page.getByRole("link", { name: "Documents" }).click();
      await expect(page.getByRole("heading", { name: "Documents" }).filter({ visible: true })).toBeVisible();
      await expect(page.getByText("Taxes", { exact: true }).filter({ visible: true })).toHaveCount(1);
      await expect(page.getByText("summary.txt").filter({ visible: true })).toHaveCount(0);

      // Up by the back button.
      await page
        .getByRole("button", { name: text("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .click();
      await expect(
        page.getByRole("heading", { name: text("My files", "Moji fajlovi") }).filter({ visible: true }),
      ).toBeVisible();

      // An empty folder says so.
      await page.getByText("Holiday pictures").filter({ visible: true }).click();
      await expect(page.getByText(text("This folder is empty.", "Ovaj folder je prazan."))).toBeVisible();
      await shot(page, `${language}-25-empty`);
      await page
        .getByRole("button", { name: text("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .click();

      // A folder with more files than one page: the rest arrives while scrolling.
      await page
        .getByRole("button", { name: new RegExp(text("^Sort by", "^Poređaj po")) })
        .filter({ visible: true })
        .click();
      await page.getByRole("button", { name: text("Name, A to Z", "Ime, od A do Ž") }).click();
      await page.getByText("Many files").filter({ visible: true }).click();
      await expect(page.getByText("file 001.txt")).toBeVisible();
      await page.getByText("file 001.txt").hover();
      for (let i = 0; i < 40 && !(await page.getByText("file 230.txt").isVisible()); i++) {
        await page.mouse.wheel(0, 4000);
        await page.waitForTimeout(150);
      }

      await expect(page.getByText("file 230.txt")).toBeVisible();
      await expect(page.getByText(text("230 items", "230 stavki"))).toBeVisible();
      await shot(page, `${language}-26-many-end`);
      await page
        .getByRole("button", { name: text("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .click();

      // The other library: a folder of the server, read only, with the reason.
      await page.getByRole("button", { name: "Family archive" }).click();
      await expect(page.getByText("readme.txt")).toBeVisible();
      await expect(page.getByText(text("nothing in it can be changed here", "u njemu ništa ne može"))).toBeVisible();
      await shot(page, `${language}-27-library-read-only`);
      await page.getByText("Scans").click();
      await expect(page.getByText("passport.txt")).toBeVisible();
      await shot(page, `${language}-28-library-folder`);

      // The other tabs.
      await page.getByRole("tab", { name: text("Photos", "Fotografije") }).click();
      await shot(page, `${language}-29-photos`);
      await page.getByRole("tab", { name: text("Account", "Nalog") }).click();
      await expect(page.getByText(text("ana on", "ana na"))).toBeVisible();
      await shot(page, `${language}-30-account`);

      // After a restart the app is still signed in, opens on Photos, and Files remembers the order.
      await page.goto("/");
      await expect(page.getByRole("heading", { name: text("Photos", "Fotografije") })).toBeVisible();
      await page.getByRole("tab", { name: text("Files", "Fajlovi") }).click();
      await expect(page.getByRole("heading", { name: text("My files", "Moji fajlovi") })).toBeVisible();
      await expect(page.getByText("Rental agreement.pdf")).toBeVisible();

      // When the server cannot be reached, the screen says so and offers to try again.
      await page.route("**/api/v1/nodes/**", (route) => route.abort());
      await page.getByText("Documents").filter({ visible: true }).click();
      await expect(page.getByRole("alert")).toBeVisible({ timeout: 30_000 });
      await shot(page, `${language}-31-unreachable`);
      await page.unroute("**/api/v1/nodes/**");
      await page.getByRole("button", { name: text("Try again", "Pokušaj ponovo") }).click();
      await expect(page.getByText("Taxes", { exact: true }).filter({ visible: true })).toHaveCount(1);

      await signOut(page, text);
    });
  });
}
