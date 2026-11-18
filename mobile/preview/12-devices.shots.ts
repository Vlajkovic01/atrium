import { expect, test } from "@playwright/test";

import { SERVER, asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 11-offline. A "TV" asks to be signed in through the API, as
// the TV app will, and is approved from the phone by its code; another
// device is signed out from the list.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`devices, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("the list of devices, signing one out, and approving a TV", async ({ page, request, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      // A device to sign out: another session of the same person.
      await asAna(request);

      // The TV asks.
      const tvName = `Living room TV ${language}`;
      const started = await request.post(`${SERVER}/api/v1/auth/pairing`, {
        data: { device: { name: tvName, kind: "tv", platform: "Android TV", appVersion: null } },
      });
      expect(started.ok(), await started.text()).toBe(true);
      const { userCode, deviceCode } = (await started.json()) as { userCode: string; deviceCode: string };

      await signIn(page, baseURL!, say);
      await page.getByRole("tab", { name: new RegExp(say("Account", "Nalog")) }).click();
      await page.getByRole("button", { name: say("Devices", "Uređaji"), exact: true }).click();

      // This phone, and the others.
      await expect(page.getByText(say("This device", "Ovaj uređaj"), { exact: false }).first()).toBeVisible();
      await shot(page, `${language}-140-devices`);
      const others = page.getByRole("button", { name: say("Sign out", "Odjavi"), exact: true });
      const before = await others.count();
      expect(before).toBeGreaterThan(0);
      await others.first().click();
      await expect(
        page.getByText(say("Its session ends at once.", "Njegova sesija se odmah završava."), { exact: false }),
      ).toBeVisible();
      await page
        .getByRole("button", { name: say("Sign out", "Odjavi"), exact: true })
        .last()
        .click();
      await expect(others).toHaveCount(before - 1);

      // Approving the TV by its code; the browser has no camera, so the code is typed.
      await page.getByRole("button", { name: say("Approve a device", "Odobrite uređaj"), exact: true }).click();
      await page
        .getByLabel(say("Code shown on the device", "Kod prikazan na uređaju"))
        .fill(userCode.replace("-", "").toLowerCase());
      await page.getByRole("button", { name: say("Find device", "Pronađi uređaj"), exact: true }).click();
      await expect(
        page.getByText(say(`${tvName} wants to sign in`, `Uređaj ${tvName} želi da se prijavi`), { exact: false }),
      ).toBeVisible();
      await expect(page.getByText(userCode, { exact: true })).toBeVisible();
      await page.getByRole("checkbox", { name: say("Read-only", "Samo čitanje"), exact: false }).click();
      await shot(page, `${language}-141-approve`);
      await page.getByRole("button", { name: say("Approve", "Odobri"), exact: true }).click();
      await expect(
        page.getByText(say("Approved. The device is signing in.", "Odobreno. Uređaj se prijavljuje.")),
      ).toBeVisible();
      await shot(page, `${language}-142-approved`);

      // The TV now gets its session, read-only.
      const token = await request.post(`${SERVER}/api/v1/auth/pairing/token`, { data: { deviceCode } });
      expect(token.ok(), await token.text()).toBe(true);

      // A code nobody is waiting with says so.
      await page.getByLabel(say("Code shown on the device", "Kod prikazan na uređaju")).fill("ZZZZ-9999");
      await page.getByRole("button", { name: say("Find device", "Pronađi uređaj"), exact: true }).click();
      await expect(
        page.getByText(say("No device is waiting with that code.", "Nijedan uređaj ne čeka sa tim kodom."), {
          exact: false,
        }),
      ).toBeVisible();

      await page
        .getByRole("button", { name: say("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .first()
        .click();
      await page
        .getByRole("button", { name: say("Back", "Nazad"), exact: true })
        .filter({ visible: true })
        .first()
        .click();
      await signOut(page, say);
    });
  });
}
