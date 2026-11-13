import { expect, test, type Page } from "@playwright/test";

/**
 * Administration is one entry in the menu leading to an overview, with
 * every section a tab away; backups and the storage check run from there.
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

test("administration is one menu entry that opens an overview of the server", async ({ page }) => {
  await signIn(page, "/files");
  const menu = page.getByRole("navigation", { name: "Main" });
  await expect(menu.getByRole("link", { name: "People" })).toHaveCount(0);
  await menu.getByRole("link", { name: /^Administration/ }).click();

  await expect(page.getByRole("heading", { name: "Administration", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "People" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Storage" })).toBeVisible();

  const tabs = page.getByRole("navigation", { name: "Administration sections" });
  await tabs.getByRole("link", { name: "Libraries" }).click();
  await expect(page).toHaveURL(/\/admin\/libraries$/);

  // An earlier address still leads to its section.
  await page.goto("/people");
  await expect(page).toHaveURL(/\/admin\/people$/);
});

test("a backup is made and the storage is checked on demand", async ({ page }) => {
  await signIn(page, "/admin/backups");
  await page.getByRole("button", { name: "Back up now" }).click();
  await expect(page.getByText("The backup is ready.")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText("Made by hand")).toBeVisible();

  await page.getByRole("button", { name: "Check now" }).click();
  await expect(page.getByRole("button", { name: "Check now" })).toBeEnabled({ timeout: 30_000 });
  await expect(page.getByText("Every file is where it should be and unchanged.")).toBeVisible();
});
