import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";

const SERVER = "http://127.0.0.1:5192";
const OUT = resolve("../screenshots/mobile");
const PASSWORD = "correct horse battery";

mkdirSync(OUT, { recursive: true });

async function shot(page: Page, name: string): Promise<void> {
  await page.waitForTimeout(300);
  await page.screenshot({ path: join(OUT, `${name}.png`) });
}

/** Sets the server up the way the wizard does: an admin, a storage folder, done. */
async function setUpServer(request: APIRequestContext): Promise<void> {
  const state = (await (await request.get(`${SERVER}/api/v1/setup/state`)).json()) as { adminExists: boolean };
  if (state.adminExists) {
    return;
  }

  expect((await request.post(`${SERVER}/api/v1/setup/admin`, { data: { name: "ana", password: PASSWORD } })).ok()).toBe(
    true,
  );
  const login = await request.post(`${SERVER}/api/v1/auth/login`, {
    data: {
      name: "ana",
      password: PASSWORD,
      device: { name: "Preview", kind: "other", platform: null, appVersion: null },
    },
  });
  const token = ((await login.json()) as { tokens: { accessToken: string } }).tokens.accessToken;
  const headers = { Authorization: `Bearer ${token}` };
  const storage = await request.post(`${SERVER}/api/v1/storage-locations`, {
    headers,
    data: {
      name: "Main",
      kind: "localFileSystem",
      local: { path: join(process.env.ATRIUM_E2E_ROOT!, "storage"), createDirectory: true },
      s3: null,
    },
  });
  expect(storage.ok(), await storage.text()).toBe(true);
  expect((await request.post(`${SERVER}/api/v1/setup/complete`, { headers })).ok()).toBe(true);
}

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`connecting, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("from the first screen to a signed-in app and out again", async ({ page, request, baseURL }) => {
      await setUpServer(request);
      const text = (en: string, sr: string) => (language === "en" ? en : sr);

      await page.goto("/");
      await expect(
        page.getByText(text("Your files, on your own server", "Vaši fajlovi, na vašem serveru")),
      ).toBeVisible();
      await shot(page, `${language}-01-welcome`);

      await page.getByRole("button", { name: text("Type the server's address", "Upiši adresu servera") }).click();
      const address = page.getByLabel(text("Address", "Adresa"));
      await expect(address).toBeVisible();
      await shot(page, `${language}-02-address`);

      // Not an address at all.
      await address.fill("not an address");
      await page.getByRole("button", { name: text("Continue", "Nastavi") }).click();
      await expect(page.getByRole("alert")).toBeVisible();
      await shot(page, `${language}-03-address-invalid`);

      // An address where nothing answers.
      await address.fill("127.0.0.1:9");
      await page.getByRole("button", { name: text("Continue", "Nastavi") }).click();
      await expect(page.getByRole("alert")).toContainText(text("Nothing answered", "ništa ne odaziva"));
      await shot(page, `${language}-04-address-unreachable`);

      // Plain HTTP to an address that is not local: refused, with the reason.
      await address.fill("http://cloud.example.com");
      await page.getByRole("button", { name: text("Continue", "Nastavi") }).click();
      await expect(
        page.getByText(text("This server cannot be used from here", "Ovaj server ne može da se koristi odavde")),
      ).toBeVisible();
      await shot(page, `${language}-05-insecure`);
      await page.getByRole("button", { name: text("Try another address", "Probaj drugu adresu") }).click();

      // The real server, through this page's own origin.
      await page.getByLabel(text("Address", "Adresa")).fill(new URL(baseURL!).host);
      await page.getByRole("button", { name: text("Continue", "Nastavi") }).click();
      await expect(page.getByLabel(text("Password", "Lozinka"))).toBeVisible();
      await shot(page, `${language}-06-sign-in`);

      await page.getByLabel(text("Name", "Ime")).fill("ana");
      await page.getByLabel(text("Password", "Lozinka")).fill("wrong password");
      await page.getByRole("button", { name: text("Sign in", "Prijavi se") }).click();
      await expect(page.getByRole("alert")).toBeVisible();
      await shot(page, `${language}-07-sign-in-wrong`);

      await page.getByLabel(text("Password", "Lozinka")).fill(PASSWORD);
      await page.getByRole("button", { name: text("Sign in", "Prijavi se") }).click();
      // The app opens on Files; who is signed in where is under Account.
      await expect(page.getByRole("heading", { name: text("My files", "Moji fajlovi") })).toBeVisible();
      await page.getByRole("tab", { name: text("Account", "Nalog") }).click();
      await expect(page.getByText(text("This connection is not encrypted", "Ova veza nije šifrovana"))).toBeVisible();
      await shot(page, `${language}-08-account`);

      // The session survives a restart of the app.
      await page.goto("/");
      await expect(page.getByRole("heading", { name: text("My files", "Moji fajlovi") })).toBeVisible();
      await page.getByRole("tab", { name: text("Account", "Nalog") }).click();

      await page.getByRole("button", { name: text("Sign out of this server", "Odjavi se sa ovog servera") }).click();
      await shot(page, `${language}-09-sign-out`);
      await page.getByRole("button", { name: text("Sign out of this server", "Odjavi se sa ovog servera") }).click();
      await expect(
        page.getByText(text("Your files, on your own server", "Vaši fajlovi, na vašem serveru")),
      ).toBeVisible();
    });
  });
}
