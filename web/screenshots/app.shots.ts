import { test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";

// Walks through the app once and saves a picture of every screen.
const out = resolve(process.env.ATRIUM_SCREENSHOT_DIR ?? "../screenshots");
mkdirSync(out, { recursive: true });

const OWNER = "stefan";
const PASSWORD = "correct horse battery";
const storagePath = join(process.env.ATRIUM_E2E_ROOT ?? "", "storage");

let counter = 0;
async function shot(page: Page, name: string): Promise<void> {
  counter++;
  await page.waitForTimeout(300);
  await page.screenshot({ path: join(out, `${String(counter).padStart(2, "0")}-${name}.png`), fullPage: true });
}

async function signIn(page: Page): Promise<void> {
  await page.getByLabel("Name").fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
}

test("screenshots of every screen", async ({ page, request, browser }) => {
  // --- Setup wizard ---
  await page.goto("/");
  await page.getByRole("heading", { name: "Welcome to Atrium" }).waitFor();
  await shot(page, "wizard-welcome");
  await page.getByRole("button", { name: "Continue" }).click();

  await page.getByLabel("Name").fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByLabel("Repeat the password").fill(PASSWORD);
  await shot(page, "wizard-admin");
  await page.getByRole("button", { name: "Create account" }).click();

  await page.getByRole("heading", { name: "Where should files be stored?" }).waitFor();
  await page.getByLabel("Folder", { exact: true }).fill(storagePath);
  await page.getByRole("button", { name: "Test" }).click();
  await page.getByText("This folder does not exist yet.").waitFor();
  await shot(page, "wizard-storage-folder");

  await page.getByLabel("S3-compatible storage").check();
  await page.getByLabel("Bucket", { exact: true }).fill("atrium-files");
  await page.getByLabel(/Service address/).fill("https://s3.example.com");
  await page.getByLabel("Access key").fill("AKIAEXAMPLE");
  await page.getByLabel("Secret key", { exact: true }).fill("not-a-real-secret");
  await shot(page, "wizard-storage-s3");

  await page.getByLabel("A folder on this server").check();
  await page.getByLabel("Folder", { exact: true }).fill(storagePath);
  await page.getByRole("button", { name: "Use this location" }).click();

  await page.getByRole("heading", { name: "Reaching Atrium" }).waitFor();
  await shot(page, "wizard-access");
  await page.getByRole("button", { name: "Continue" }).click();

  await page.getByRole("img", { name: "QR code for connecting a phone" }).waitFor();
  await shot(page, "wizard-done");
  await page.getByRole("button", { name: "Open Atrium" }).click();

  // --- A second device and a TV waiting for approval ---
  await request.post("/api/v1/auth/login", {
    data: { name: OWNER, password: PASSWORD, device: { name: "Pixel 9", kind: "mobile", platform: "Android 16" } },
  });
  const started = await request.post("/api/v1/auth/pairing", {
    data: { device: { name: "Living room TV", kind: "tv", platform: "Android TV", appVersion: "0.1.0" } },
  });
  const pairing = (await started.json()) as { userCode: string; deviceCode: string };

  // --- Devices ---
  await page.goto("/devices");
  await page.getByText("Pixel 9").waitFor();
  await shot(page, "devices");

  await page.getByLabel("Code shown on the device").fill(pairing.userCode);
  await page.getByRole("button", { name: "Find device" }).click();
  await page.getByText("Living room TV wants to sign in to your account.").waitFor();
  await page.getByLabel("Read-only: it cannot change or delete anything").check();
  await shot(page, "devices-approve");
  await page.getByRole("button", { name: "Approve" }).click();
  await page.getByText("Approved. The device is signing in.").waitFor();
  await request.post("/api/v1/auth/pairing/token", { data: { deviceCode: pairing.deviceCode } });

  await page.reload();
  await page.getByText("Living room TV").waitFor();
  await page.getByRole("button", { name: "Show QR code" }).click();
  await page.getByRole("img", { name: "QR code for connecting a phone" }).waitFor();
  await shot(page, "devices-with-tv-and-qr");

  // --- Account, with two-step verification being turned on ---
  await page.goto("/account");
  await page.getByRole("heading", { name: "Account", exact: true }).waitFor();
  await shot(page, "account");
  await page.getByRole("button", { name: "Turn on" }).click();
  await page.getByRole("img", { name: "QR code for the authenticator app" }).waitFor();
  await shot(page, "account-two-step");
  await page.getByRole("button", { name: "Cancel" }).click();

  // --- People ---
  await page.goto("/people");
  await page.getByRole("heading", { name: "People", exact: true }).waitFor();
  for (const [name, role] of [
    ["ana", "user"],
    ["marko", "guest"],
  ] as const) {
    await page.getByLabel("Name", { exact: true }).fill(name);
    await page.getByLabel("Role", { exact: true }).selectOption(role);
    await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
    await page.getByRole("button", { name: "Add person" }).click();
    await page.getByText(`${name} can now sign in.`).waitFor();
  }
  await shot(page, "people");

  // --- Storage ---
  await page.goto("/storage");
  await page.getByRole("heading", { name: "Storage", exact: true }).waitFor();
  await page.getByRole("button", { name: "Check now" }).click();
  await page.getByText(/Working\. Free space/).waitFor();
  await shot(page, "storage");

  // --- Sign in, and an error ---
  await page.getByRole("button", { name: "Sign out" }).first().click();
  await page.getByRole("heading", { name: "Sign in" }).waitFor();
  await shot(page, "sign-in");
  await page.getByLabel("Name").fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill("wrong password");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.getByRole("alert").waitFor();
  await shot(page, "sign-in-error");

  // --- Serbian ---
  await page.getByLabel("Language").selectOption("sr-Latn");
  await page.goto("/devices");
  await page.getByRole("heading", { name: "Prijava" }).waitFor();
  await shot(page, "sign-in-serbian");
  await page.getByLabel("Ime").fill(OWNER);
  await page.getByLabel("Lozinka", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Prijavi se" }).click();
  await page.getByText("Pixel 9").waitFor();
  await shot(page, "devices-serbian");

  // --- Phone width, dark mode ---
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    deviceScaleFactor: 3,
    colorScheme: "dark",
    locale: "en-US",
  });
  const mobile = await context.newPage();
  await mobile.goto("/");
  await mobile.getByRole("heading", { name: "Sign in" }).waitFor();
  await shot(mobile, "sign-in-phone-dark");
  await signIn(mobile);
  await mobile.getByText("Pixel 9").waitFor();
  await shot(mobile, "devices-phone-dark");
  await mobile.goto("/account");
  await mobile.getByRole("heading", { name: "Account", exact: true }).waitFor();
  await shot(mobile, "account-phone-dark");
  await context.close();

  // --- Desktop, dark mode ---
  const dark = await browser.newContext({
    viewport: { width: 1280, height: 860 },
    deviceScaleFactor: 2,
    colorScheme: "dark",
    locale: "en-US",
  });
  const darkPage = await dark.newPage();
  await darkPage.goto("/");
  await darkPage.getByRole("heading", { name: "Sign in" }).waitFor();
  await shot(darkPage, "sign-in-dark");
  await signIn(darkPage);
  await darkPage.getByText("Pixel 9").waitFor();
  await shot(darkPage, "devices-dark");
  await dark.close();
});
