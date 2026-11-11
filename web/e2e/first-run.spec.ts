import { expect, test, type Page } from "@playwright/test";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

/**
 * The first-run story, in order, against one real server: set it up,
 * sign out and in, approve a TV, change the language. Each test continues
 * where the previous one left the server.
 */
test.describe.configure({ mode: "serial" });

const OWNER = "owner";
const PASSWORD = "correct horse battery";
const storagePath = join(process.env.ATRIUM_E2E_ROOT ?? "", "storage");

// Any violation of the content security policy fails the test it happens in.
function watchForPolicyViolations(page: Page): string[] {
  const violations: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error" && /Content Security Policy|Refused to/i.test(message.text())) {
      violations.push(message.text());
    }
  });
  return violations;
}

async function signIn(page: Page): Promise<void> {
  await page.getByLabel("Name").fill(OWNER);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
}

test("a fresh server is set up through the wizard", async ({ page }) => {
  const violations = watchForPolicyViolations(page);
  await page.goto("/");

  // Welcome. Opened on the server itself, so no setup token is asked for.
  await expect(page.getByRole("heading", { name: "Welcome to Atrium" })).toBeVisible();
  await expect(page.getByLabel("Setup token")).toHaveCount(0);
  await page.getByRole("button", { name: "Continue" }).click();

  // Admin account: a mismatch is caught before anything is sent.
  await page.getByLabel("Name").fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByLabel("Repeat the password").fill("something else");
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByText("The two passwords are not the same.")).toBeVisible();
  await page.getByLabel("Repeat the password").fill(PASSWORD);
  await page.getByRole("button", { name: "Create account" }).click();

  // Storage: testing a folder that does not exist creates nothing.
  await expect(page.getByRole("heading", { name: "Where should files be stored?" })).toBeVisible();
  await page.getByLabel("Folder", { exact: true }).fill(storagePath);
  await page.getByRole("button", { name: "Test" }).click();
  await expect(page.getByText("This folder does not exist yet.")).toBeVisible();
  expect(existsSync(storagePath)).toBe(false);

  await page.getByRole("button", { name: "Use this location" }).click();

  // Access, then done.
  await expect(page.getByRole("heading", { name: "Reaching Atrium" })).toBeVisible();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Atrium is ready" })).toBeVisible();
  await expect(page.getByRole("img", { name: "QR code for connecting a phone" })).toBeVisible();

  // The storage folder now exists and says which location it is.
  const identity = JSON.parse(readFileSync(join(storagePath, "atrium", "location.json"), "utf8")) as { id: string };
  expect(identity.id).toMatch(/^[0-9a-f-]{36}$/);

  await page.getByRole("button", { name: "Open Atrium" }).click();
  await expect(page.getByRole("heading", { name: "Devices" })).toBeVisible();
  await expect(page.getByText("This device")).toBeVisible();
  expect(violations).toEqual([]);
});

test("the wizard does not come back, and reloading keeps the user signed in", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await signIn(page);
  await expect(page.getByRole("heading", { name: "Devices" })).toBeVisible();

  await page.reload();

  await expect(page.getByRole("heading", { name: "Devices" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Sign in" })).toHaveCount(0);
});

test("a wrong password is refused with an explanation, and signing out ends the session", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Name").fill(OWNER);
  await page.getByLabel("Password").fill("not the password");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("alert")).toHaveText("The name, password or code is not correct.");

  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Devices" })).toBeVisible();

  await page.getByRole("button", { name: "Sign out" }).first().click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.reload();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
});

test("a TV is approved from the link in its QR code", async ({ page, request }) => {
  const violations = watchForPolicyViolations(page);

  // The TV asks to be paired and shows a code.
  const started = await request.post("/api/v1/auth/pairing", {
    data: { device: { name: "Living room TV", kind: "tv", platform: "Android TV", appVersion: "0.1.0" } },
  });
  const pairing = (await started.json()) as { deviceCode: string; userCode: string; verificationUrl: string };

  // The phone's camera opens the link from the QR code. The user is not
  // signed in on this browser yet, and lands on the approval afterwards.
  await page.goto(new URL(pairing.verificationUrl).pathname + new URL(pairing.verificationUrl).search);
  await signIn(page);

  await expect(page.getByLabel("Code shown on the device")).toHaveValue(pairing.userCode);
  await page.getByRole("button", { name: "Find device" }).click();
  await expect(page.getByText("Living room TV wants to sign in to your account.")).toBeVisible();
  await expect(page.getByText("Approve only if this device is in front of you")).toBeVisible();
  await page.getByLabel("Read-only: it cannot change or delete anything").check();
  await page.getByRole("button", { name: "Approve" }).click();
  await expect(page.getByText("Approved. The device is signing in.")).toBeVisible();

  // The TV collects its session, and the list shows it as read-only.
  const token = await request.post("/api/v1/auth/pairing/token", { data: { deviceCode: pairing.deviceCode } });
  expect(((await token.json()) as { status: string }).status).toBe("approved");
  await page.reload();
  const tv = page.getByRole("listitem").filter({ hasText: "Living room TV" });
  await expect(tv).toBeVisible();
  await expect(tv.getByText("Read-only")).toBeVisible();
  expect(violations).toEqual([]);
});

test("an unknown pairing code is explained, not just rejected", async ({ page }) => {
  await page.goto("/devices");
  await signIn(page);

  await page.getByLabel("Code shown on the device").fill("BBBB-BBBB");
  await page.getByRole("button", { name: "Find device" }).click();

  await expect(page.getByText("No device is waiting with that code.")).toBeVisible();
});

test("the interface is available in Serbian, in both scripts", async ({ page }) => {
  await page.goto("/");

  await page.getByLabel("Language").selectOption("sr-Latn");
  await expect(page.getByRole("heading", { name: "Prijava" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "sr-Latn");

  await page.getByLabel("Jezik").selectOption("sr-Cyrl");
  await expect(page.getByRole("heading", { name: "Пријава" })).toBeVisible();

  // The choice is remembered on this browser.
  await page.reload();
  await expect(page.getByRole("heading", { name: "Пријава" })).toBeVisible();

  // An error from the server is shown in the chosen language too.
  await page.getByLabel("Име").fill(OWNER);
  await page.getByLabel("Лозинка").fill("погрешна лозинка");
  await page.getByRole("button", { name: "Пријави се" }).click();
  await expect(page.getByRole("alert")).toHaveText("Име, лозинка или код нису тачни.");
});
