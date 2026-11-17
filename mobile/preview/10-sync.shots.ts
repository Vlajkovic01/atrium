import { expect, test } from "@playwright/test";

import { SERVER, asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 09-notifications. Changes made elsewhere (here: through the
// API, as the web or another phone would) reach an open folder by
// themselves, through the change feed.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`staying in step, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("a file added and renamed elsewhere shows in the open folder", async ({ page, request, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const ana = await asAna(request);
      const folderName = `Elsewhere ${language}`;
      const folderId = await ana.folder(ana.root, folderName);
      const row = (name: string) => page.getByText(name, { exact: true }).filter({ visible: true });

      await signIn(page, baseURL!, say);
      await row(folderName).click();
      await expect(
        page.getByText(say("This folder is empty.", "Ovaj folder je prazan.")).filter({ visible: true }),
      ).toBeVisible();

      // Added elsewhere: it appears without pulling down.
      await ana.upload(folderId, "from the web.txt", Buffer.from("hello"));
      await expect(row("from the web.txt")).toBeVisible({ timeout: 45_000 });
      await shot(page, `${language}-120-arrived`);

      // Renamed elsewhere.
      const children = await ana.children(folderId);
      const file = children.find((c) => c.name === "from the web.txt")!;
      const node = (await (
        await request.get(`${SERVER}/api/v1/nodes/${file.id}`, { headers: ana.headers })
      ).json()) as {
        version: number;
      };
      const renamed = await request.patch(`${SERVER}/api/v1/nodes/${file.id}`, {
        headers: { ...ana.headers, "If-Match": `"${node.version}"` },
        data: { name: "renamed elsewhere.txt", parentId: null },
      });
      expect(renamed.ok(), await renamed.text()).toBe(true);
      await expect(row("renamed elsewhere.txt")).toBeVisible({ timeout: 45_000 });
      await expect(row("from the web.txt")).toHaveCount(0);

      // Moved to the trash elsewhere: gone here too.
      const trashed = await request.post(`${SERVER}/api/v1/nodes/batch`, {
        headers: ana.headers,
        data: { operation: "trash", ids: [file.id], parentId: null },
      });
      expect(trashed.ok(), await trashed.text()).toBe(true);
      await expect(row("renamed elsewhere.txt")).toHaveCount(0, { timeout: 45_000 });
      await shot(page, `${language}-121-gone`);

      await signOut(page, say);
    });
  });
}
