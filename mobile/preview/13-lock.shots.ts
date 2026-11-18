import { expect, test } from "@playwright/test";

import { shot, signIn, signOut, type Say } from "./helpers";

// Runs after 12-devices. In the browser there is no fingerprint: an
// unlock succeeds by itself after a moment, so the lock screen shows.

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`lock, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("turning the lock on, opening locked, and turning it off", async ({ page, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const toggle = page.getByRole("switch", {
        name: say(
          "Lock Atrium with fingerprint, face or screen lock",
          "Zaključaj Atrium otiskom, licem ili zaključavanjem ekrana",
        ),
      });

      await signIn(page, baseURL!, say);
      await page.getByRole("tab", { name: new RegExp(say("Account", "Nalog")) }).click();
      await expect(toggle).toHaveAttribute("aria-checked", "false");
      await toggle.click();
      await expect(toggle).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
      await page.getByRole("radio", { name: say("At once", "Odmah") }).click();
      await shot(page, `${language}-150-lock-on`);

      // Opened again: locked, then unlocked by the phone's prompt.
      await page.goto("/");
      await expect(page.getByText(say("Atrium is locked", "Atrium je zaključan"))).toBeVisible();
      await shot(page, `${language}-151-locked`);
      await expect(page.getByText(say("Atrium is locked", "Atrium je zaključan"))).toHaveCount(0, { timeout: 10_000 });
      await expect(page.getByRole("heading", { name: say("Photos", "Fotografije") })).toBeVisible();

      // Off again.
      await page.getByRole("tab", { name: new RegExp(say("Account", "Nalog")) }).click();
      await toggle.click();
      await expect(toggle).toHaveAttribute("aria-checked", "false");

      await signOut(page, say);
    });
  });
}
