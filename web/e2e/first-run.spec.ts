import { expect, test, type Page } from "@playwright/test";
import { createHmac } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

/**
 * The first-run story, in order, against one real server: set it up,
 * sign out and in, approve a TV, add a person, turn on two-step
 * verification, switch language. Each test continues where the previous
 * one left the server.
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

async function signIn(page: Page, name = OWNER, password = PASSWORD): Promise<void> {
  await page.getByLabel("Name").fill(name);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

/** The code an authenticator app would show for this secret right now (RFC 6238). */
function totp(base32Secret: string): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const char of base32Secret.replace(/\s/g, "")) {
    bits += alphabet.indexOf(char).toString(2).padStart(5, "0");
  }
  const secret = Buffer.from(bits.match(/.{8}/g)!.map((byte) => parseInt(byte, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(Math.floor(Date.now() / 30_000)));
  const mac = createHmac("sha1", secret).update(counter).digest();
  const offset = mac[mac.length - 1]! & 0x0f;
  return String((mac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

test("a fresh server is set up through the wizard", async ({ page }) => {
  const violations = watchForPolicyViolations(page);
  await page.goto("/");

  // Welcome. Opened on the server itself, so no setup token is asked for.
  await expect(page.getByRole("heading", { name: "Welcome to Atrium" })).toBeVisible();
  await expect(page.getByLabel("Setup token")).toHaveCount(0);
  await expect(page.getByRole("listitem").filter({ hasText: "Welcome" })).toHaveAttribute("aria-current", "step");
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
  await expect(page.getByLabel("A folder on this server")).toBeChecked();
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
  await signIn(page, OWNER, "not the password");
  await expect(page.getByRole("alert")).toHaveText("The name, password or code is not correct.");

  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Devices" })).toBeVisible();

  await page.getByRole("button", { name: "Sign out" }).first().click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.reload();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
});

test("a password can be shown while typing it", async ({ page }) => {
  await page.goto("/");
  const password = page.getByLabel("Password", { exact: true });
  await password.fill("visible?");
  await expect(password).toHaveAttribute("type", "password");

  await page.getByRole("button", { name: "Show password" }).click();
  await expect(password).toHaveAttribute("type", "text");

  await page.getByRole("button", { name: "Hide password" }).click();
  await expect(password).toHaveAttribute("type", "password");
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
  await expect(page.getByLabel(pairing.userCode)).toBeVisible();
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

test("an admin adds a person, who then sees no administration", async ({ page, browser }) => {
  await page.goto("/people");
  await signIn(page);
  await expect(page.getByRole("heading", { name: "People", exact: true })).toBeVisible();

  await page.getByLabel("Name", { exact: true }).fill("ana");
  await page.getByLabel("Role", { exact: true }).selectOption("user");
  await page.getByLabel("Password", { exact: true }).fill("ana's own passphrase");
  await page.getByRole("button", { name: "Add person" }).click();
  await expect(page.getByText("ana can now sign in.")).toBeVisible();
  await expect(page.getByRole("listitem").filter({ hasText: "ana" })).toBeVisible();

  // The admin's own role cannot be changed here: nobody could administer the server.
  await expect(page.getByLabel("Role of owner")).toBeDisabled();

  // Ana signs in on her own browser and has no admin navigation.
  const context = await browser.newContext();
  const hers = await context.newPage();
  await hers.goto("/people");
  await signIn(hers, "ana", "ana's own passphrase");
  await expect(hers.getByRole("heading", { name: "Devices" })).toBeVisible();
  await expect(hers.getByRole("link", { name: "People" })).toHaveCount(0);
  await expect(hers.getByRole("link", { name: "Storage" })).toHaveCount(0);
  await context.close();
});

test("an admin resets a person's password, and that person is told", async ({ page, browser }) => {
  page.on("dialog", (dialog) => void dialog.accept());
  await page.goto("/people");
  await signIn(page);
  const ana = page.getByRole("listitem").filter({ hasText: "ana" });
  await ana.getByRole("button", { name: "Reset password" }).click();
  await page.getByLabel("New password for ana").fill("temporary passphrase");
  await page.getByRole("button", { name: "Set password", exact: true }).click();
  await expect(page.getByText("Password reset. Give ana the new password.")).toBeVisible();

  const context = await browser.newContext();
  const hers = await context.newPage();
  await hers.goto("/account");
  await signIn(hers, "ana", "temporary passphrase");
  await expect(hers.getByText("An administrator reset your password.")).toBeVisible();
  await context.close();
});

test("the storage location is listed and checks out as working", async ({ page }) => {
  await page.goto("/storage");
  await signIn(page);
  await expect(page.getByRole("heading", { name: "Storage", exact: true })).toBeVisible();

  const location = page.getByRole("listitem").filter({ hasText: "Main storage" });
  await expect(location.getByText("Online")).toBeVisible();
  await location.getByRole("button", { name: "Check now" }).click();
  await expect(location.getByText(/Working\. Free space/)).toBeVisible();
});

test("two-step verification is turned on, used to sign in, and turned off", async ({ page }) => {
  const violations = watchForPolicyViolations(page);
  await page.goto("/account");
  await signIn(page);

  // Turn on: scan (here: read the key), confirm with a code, save the recovery codes.
  await page.getByRole("button", { name: "Turn on" }).click();
  await expect(page.getByRole("img", { name: "QR code for the authenticator app" })).toBeVisible();
  const secret = (await page.locator(".secret").innerText()).replace(/\s/g, "");
  await page.getByLabel("Code from the app").fill("000000");
  await page.getByRole("button", { name: "Confirm" }).click();
  await expect(page.getByText("That code is not correct.")).toBeVisible();
  await page.getByLabel("Code from the app").fill(totp(secret));
  await page.getByRole("button", { name: "Confirm" }).click();
  await expect(page.getByRole("heading", { name: "Save your recovery codes" })).toBeVisible();
  const recovery = (await page.locator(".codes li").first().innerText()).trim();
  expect(recovery).toMatch(/^[A-Z]{5}-[A-Z]{5}$/);
  await expect(page.locator(".codes li")).toHaveCount(10);
  await page.getByRole("button", { name: "I have saved them" }).click();
  await expect(page.getByText("On. Signing in also asks for a code")).toBeVisible();

  // Signing in now needs the second step; a recovery code works once.
  await page.getByRole("button", { name: "Sign out" }).first().click();
  await signIn(page);
  await expect(page.getByRole("heading", { name: "Two-step verification" })).toBeVisible();
  await page.getByLabel("Code").fill(recovery);
  await page.getByRole("button", { name: "Verify" }).click();
  await expect(page.getByRole("heading", { name: "Account", exact: true })).toBeVisible();

  // Turn off again, which asks for the password.
  await page.getByRole("button", { name: "Turn off" }).click();
  await page.getByLabel("Password", { exact: true }).first().fill(PASSWORD);
  await page.getByRole("button", { name: "Turn off" }).click();
  await expect(page.getByText("Two-step verification is off.")).toBeVisible();
  expect(violations).toEqual([]);
});

test("the interface is available in Serbian", async ({ page }) => {
  await page.goto("/");

  await page.getByLabel("Language").selectOption("sr-Latn");
  await expect(page.getByRole("heading", { name: "Prijava" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "sr-Latn");
  await expect(page.getByText("Vaš privatni oblak.")).toBeVisible();

  // The choice is remembered on this browser.
  await page.reload();
  await expect(page.getByRole("heading", { name: "Prijava" })).toBeVisible();

  // Only the two shipped languages are offered.
  await expect(page.getByLabel("Jezik").locator("option")).toHaveText(["English", "Srpski"]);

  // An error from the server is shown in the chosen language too.
  await page.getByLabel("Ime").fill(OWNER);
  await page.getByLabel("Lozinka", { exact: true }).fill("pogrešna lozinka");
  await page.getByRole("button", { name: "Prijavi se" }).click();
  await expect(page.getByRole("alert")).toHaveText("Ime, lozinka ili kod nisu tačni.");
});

test("on a phone the navigation is a bar at the bottom and everything stays reachable", async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: "en-US" });
  const page = await context.newPage();
  await page.goto("/");
  await signIn(page);
  await expect(page.getByRole("heading", { name: "Devices" })).toBeVisible();

  // No sideways scrolling.
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  await page.getByRole("link", { name: "Account" }).click();
  await expect(page.getByRole("heading", { name: "Account", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Sign out" }).first().click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await context.close();
});
