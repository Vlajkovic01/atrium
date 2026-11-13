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
  // It sits apart from the files, just above the name, and only there: not again in the person's menu.
  await page.getByRole("button", { name: /your menu$/ }).click();
  await expect(page.getByRole("menuitem", { name: "Customize menu" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: /^Administration/ })).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("menu")).toHaveCount(0);
  await page
    .getByRole("navigation", { name: "Administration" })
    .getByRole("link", { name: /^Administration/ })
    .click();

  await expect(page.getByRole("heading", { name: "Administration", exact: true })).toBeVisible();
  await expect(page.getByRole("region", { name: "People" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Storage" })).toBeVisible();

  const tabs = page.getByRole("navigation", { name: "Administration sections" });
  await tabs.getByRole("link", { name: "Libraries" }).click();
  await expect(page).toHaveURL(/\/admin\/libraries$/);
  await tabs.getByRole("link", { name: "Storage" }).click();
  await expect(page.getByText(/ stored/).first()).toBeVisible();
  await tabs.getByRole("link", { name: "Background work" }).click();
  await expect(page.getByText("Failed jobs")).toBeVisible();

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

test("a person is found, given a limit, and scheduled for deletion and spared", async ({ page }) => {
  await signIn(page, "/admin/people");
  await page.getByRole("button", { name: "Add person" }).click();
  const add = page.getByRole("dialog", { name: "Add a person" });
  await add.getByLabel("Name", { exact: true }).fill("temp");
  await add.getByLabel("Password", { exact: true }).fill("a temporary passphrase");
  await add.getByRole("button", { name: "Add person" }).click();
  await expect(page.getByText("temp can now sign in.")).toBeVisible();

  await page.getByLabel("Find").fill("tem");
  const rows = page.locator("ul.people > li");
  await expect(rows).toHaveCount(1);
  await rows.getByRole("button", { name: "Manage" }).click();
  const manage = page.getByRole("dialog", { name: "temp" });

  await manage.getByLabel("Limit in GB").fill("1");
  await manage.getByRole("button", { name: "Save limit" }).click();
  await expect(manage.getByText("The limit was saved.")).toBeVisible();
  await expect(manage.getByText("0 byte of 1 GB")).toBeVisible();

  await manage.getByRole("button", { name: "Delete account…" }).click();
  await expect(manage.getByText(/will be deleted/)).toBeVisible();
  const confirm = manage.getByRole("button", { name: "Delete account", exact: true });
  await expect(confirm).toBeDisabled();
  await manage.getByLabel("Type temp to confirm").fill("temp");
  await confirm.click();
  await expect(manage.getByText(/will be deleted with its files on/)).toBeVisible();
  await expect(rows.getByText(/Deleted on/)).toBeVisible();

  await manage.getByRole("button", { name: "Cancel deletion" }).click();
  await expect(manage.getByText(/The deletion was cancelled/)).toBeVisible();
  await expect(rows.getByText("Turned off")).toBeVisible();
});

test("on a phone administration is in the person's menu", async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: "en-US" });
  const page = await context.newPage();
  await signIn(page, "/files");
  await expect(page.getByRole("navigation", { name: "Administration" })).toBeHidden();
  await page.getByRole("button", { name: /your menu$/ }).click();
  await page.getByRole("menuitem", { name: /^Administration/ }).click();
  await expect(page.getByRole("heading", { name: "Administration", exact: true })).toBeVisible();
  await context.close();
});
