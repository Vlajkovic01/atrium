import { expect, test, type Page } from "@playwright/test";

/**
 * Notifications are kept on the server: an upload leaves one behind after
 * its panel has gone away by itself, the bell counts what is unread, and
 * the list is read, followed and cleared.
 */
test.describe.configure({ mode: "serial" });

const OWNER = "owner";
const PASSWORD = "correct horse battery";

async function signIn(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("Name", { exact: true }).fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
}

test("an upload is remembered in the notifications after its panel goes away by itself", async ({ page }) => {
  await signIn(page, "/files");
  await page.getByRole("button", { name: /^Notifications/ }).click();
  await page.getByRole("button", { name: "Clear all" }).click();
  await expect(page.getByText("Nothing new.")).toBeVisible();
  await page.keyboard.press("Escape");

  await page.getByTestId("upload-files").setInputFiles([
    { name: "first.txt", mimeType: "text/plain", buffer: Buffer.from("one") },
    { name: "second.txt", mimeType: "text/plain", buffer: Buffer.from("two") },
  ]);
  const panel = page.getByRole("region", { name: "Uploads" });
  await expect(panel.getByText("Uploads finished")).toBeVisible();
  await page.mouse.move(0, 0);
  await expect(panel).toHaveCount(0, { timeout: 20_000 });

  const bell = page.getByRole("button", { name: "Notifications, 1 unread" });
  await expect(bell).toBeVisible();
  await bell.click();
  const list = page.getByRole("region", { name: "Notifications" });
  await expect(list.getByText("2 files uploaded")).toBeVisible();

  // Still there after a reload: it lives on the server.
  await page.reload();
  await page.getByRole("button", { name: "Notifications, 1 unread" }).click();
  await page.getByRole("button", { name: /2 files uploaded/ }).click();
  await expect(page.getByRole("heading", { name: "My files" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Notifications", exact: true })).toBeVisible();
});
