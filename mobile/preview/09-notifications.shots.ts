import { expect, test } from "@playwright/test";

import { SERVER, asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 08-uploads, whose folders the notifications lead to.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`notifications, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("the list from the server, opening one, and clearing", async ({ page, request, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const ana = await asAna(request);
      const folder = (await ana.children(ana.root)).find((item) => item.name === `Uploads ${language}`)!;
      // Start from a known list: one about uploads into that folder, one about a failure.
      await request.delete(`${SERVER}/api/v1/me/notifications`, { headers: ana.headers });
      for (const [kind, link] of [
        ["uploads.failed", null],
        ["uploads.finished", `/files/${folder.id}`],
      ] as const) {
        const added = await request.post(`${SERVER}/api/v1/me/notifications`, {
          headers: ana.headers,
          data: { kind, params: { count: "2" }, link },
        });
        expect(added.ok(), await added.text()).toBe(true);
      }

      await signIn(page, baseURL!, say);

      // The account tab says there is something new.
      await page.getByRole("tab", { name: say("Account", "Nalog") }).click();
      const open = page.getByRole("button", { name: say("Notifications, 2 unread", "Obaveštenja, nepročitanih: 2") });
      await expect(open).toBeVisible();
      await shot(page, `${language}-110-account-unread`);
      await open.click();

      // Newest first, unread marked.
      await expect(page.getByText(say("2 files uploaded", "Otpremljena 2 fajla"))).toBeVisible();
      await expect(page.getByText(say("2 uploads failed", "2 otpremanja nisu uspela"))).toBeVisible();
      await shot(page, `${language}-111-notifications`);

      // Opening one marks it read and goes to its folder.
      await page.getByText(say("2 files uploaded", "Otpremljena 2 fajla")).click();
      await expect(page.getByRole("heading", { name: `Uploads ${language}` }).filter({ visible: true })).toBeVisible();
      // Back leads to where files start, inside Files.
      await page
        .getByRole("button", { name: say("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .click();
      await expect(
        page.getByRole("heading", { name: say("My files", "Moji fajlovi") }).filter({ visible: true }),
      ).toBeVisible();
      await page.getByRole("tab", { name: new RegExp(say("Account", "Nalog")) }).click();
      await page.getByRole("button", { name: say("Notifications, 1 unread", "Obaveštenja, nepročitanih: 1") }).click();
      await expect(page.getByRole("button", { name: say("Mark all read", "Označi sve kao pročitano") })).toBeVisible();

      // All read, one removed, then none.
      await page.getByRole("button", { name: say("Mark all read", "Označi sve kao pročitano") }).click();
      await expect(page.getByRole("button", { name: say("Mark all read", "Označi sve kao pročitano") })).toHaveCount(0);
      await page
        .getByRole("button", { name: say("Remove", "Ukloni"), exact: true })
        .first()
        .click();
      await expect(page.getByRole("button", { name: say("Remove", "Ukloni"), exact: true })).toHaveCount(1);
      await page.getByRole("button", { name: say("Clear all", "Obriši sve"), exact: true }).click();
      // Only one list: the one opened before the folder was closed when the folder opened.
      await expect(page.getByText(say("Nothing new.", "Ništa novo."))).toHaveCount(1);
      await expect(page.getByText(say("Nothing new.", "Ništa novo."))).toBeVisible();
      await shot(page, `${language}-112-notifications-empty`);
      await page
        .getByRole("button", { name: say("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .click();

      // The account tab no longer counts any.
      await expect(page.getByRole("button", { name: say("Notifications", "Obaveštenja"), exact: true })).toBeVisible();

      await signOut(page, say);
    });
  });
}
