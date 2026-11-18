import { expect, test } from "@playwright/test";

import { PASSWORD, signIn, signOut, shot, type Say } from "./helpers";

// The browser maps a name without a dot to this machine (see the config),
// so the app can sign in to a local address that is not loopback: plain
// HTTP the app allows (D-049) and marks as unencrypted (DESIGN.md 12,
// risk 6). Loopback never leaves the device and is not marked.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`unencrypted connection, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("marked on every tab over plain HTTP, not on loopback", async ({ page, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const strip = page.getByTestId("insecure-strip");
      const why = page.getByText(say("This connection is not encrypted.", "Ova veza nije šifrovana."), {
        exact: false,
      });

      // Loopback: no mark.
      await signIn(page, baseURL!, say);
      await expect(strip).toHaveCount(0);
      await signOut(page, say);

      // A local name over plain HTTP. The app is opened from that name too, so
      // its requests stay on one origin as they do in the other scenarios.
      const port = new URL(baseURL!).port;
      await page.goto(`http://atrium-home:${port}/`);
      await page.getByRole("button", { name: say("Type the server's address", "Upiši adresu servera") }).click();
      await page.getByLabel(say("Address", "Adresa")).fill(`atrium-home:${port}`);
      await page.getByRole("button", { name: say("Continue", "Nastavi") }).click();
      await expect(why.filter({ visible: true })).toBeVisible();
      await page.getByLabel(say("Name", "Ime")).fill("ana");
      await page.getByLabel(say("Password", "Lozinka")).fill(PASSWORD);
      await page.getByRole("button", { name: say("Sign in", "Prijavi se") }).click();
      await expect(page.getByRole("heading", { name: say("Photos", "Fotografije") })).toBeVisible();

      await expect(strip).toBeVisible();
      await expect(strip).toContainText(say("Not encrypted", "Nije šifrovano"));
      await shot(page, `${language}-170-unencrypted-photos`);

      await page.getByRole("tab", { name: say("Files", "Fajlovi") }).click();
      await expect(strip).toBeVisible();
      await strip.click();
      await expect(strip).toContainText(say("Anyone on the same network", "Svako na istoj mreži"));
      await shot(page, `${language}-171-unencrypted-explained`);
      await strip.click();
      await expect(strip).not.toContainText(say("Anyone on the same network", "Svako na istoj mreži"));

      // Account says it in full, beside the server it is about.
      await page.getByRole("tab", { name: new RegExp(say("Account", "Nalog")) }).click();
      await expect(why.filter({ visible: true })).toBeVisible();
      await shot(page, `${language}-172-unencrypted-account`);

      await signOut(page, say);
    });
  });
}
