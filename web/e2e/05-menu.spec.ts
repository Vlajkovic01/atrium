import { expect, test, type Page } from "@playwright/test";

/**
 * Each person arranges their own menu: hides and renames entries, and
 * makes a view of files chosen by rules, which shows them wherever they are,
 * pins a folder to it and makes it compact.
 * Runs after 04-folders.spec.ts, whose shared folder holds a PDF.
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

const menu = (page: Page) => page.getByRole("navigation", { name: "Main" });

test("the menu is arranged: an entry hidden, another renamed, and back to the default", async ({ page }) => {
  await signIn(page, "/menu");
  await expect(page.getByRole("heading", { name: "Customize menu" })).toBeVisible();

  const photos = page.getByRole("listitem").filter({ has: page.getByRole("button", { name: "Move Photos up" }) });
  await photos.getByLabel("Show").uncheck();
  const files = page.getByRole("listitem").filter({ has: page.getByRole("button", { name: "Move Files down" }) });
  await files.getByLabel("Your name for it").fill("Documents");
  await page.getByRole("button", { name: "Save" }).click();
  await expect(page.getByText("Your menu was saved.")).toBeVisible();

  await expect(menu(page).getByRole("link", { name: "Documents" })).toBeVisible();
  await expect(menu(page).getByRole("link", { name: "Photos" })).toHaveCount(0);

  await page.getByRole("button", { name: "Back to the default" }).click();
  await expect(menu(page).getByRole("link", { name: "Photos" })).toBeVisible();
  await expect(menu(page).getByRole("link", { name: "Files" })).toBeVisible();
});

test("a view made of rules shows matching files from every library", async ({ page }) => {
  await signIn(page, "/menu");
  await page.getByRole("button", { name: "New view" }).click();
  const editor = page.getByRole("dialog", { name: "New view" });
  await editor.getByLabel("Name", { exact: true }).fill("PDFs");
  await editor.getByLabel("File endings").fill("pdf");
  await expect(editor.getByText("1 file")).toBeVisible();
  await expect(editor.getByText("lease.pdf")).toBeVisible();
  await editor.getByRole("button", { name: "Save" }).click();

  await menu(page).getByRole("link", { name: "PDFs" }).click();
  await expect(page.getByRole("heading", { name: "PDFs" })).toBeVisible();
  await expect(page.getByRole("link", { name: "lease.pdf" })).toBeVisible();
});

test("a folder pinned to the menu, the menu made compact, and its settings reached from the footer", async ({
  page,
}) => {
  await signIn(page, "/files");
  await page.getByRole("button", { name: "New folder" }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("Taxes");
  await page.getByRole("dialog").getByRole("button", { name: "Create" }).click();
  await page.getByRole("button", { name: "Taxes", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Taxes" })).toBeVisible();

  await page.getByRole("button", { name: "Pin to menu" }).click();
  await expect(page.getByRole("button", { name: "Unpin from menu" })).toBeVisible();
  await expect(menu(page).getByRole("link", { name: "Taxes" })).toBeVisible();

  // Customizing lives in the footer and on the Account page, not in the menu.
  await expect(menu(page).getByRole("link", { name: "Customize menu" })).toHaveCount(0);
  await page.getByRole("link", { name: "Customize menu" }).click();
  await page.getByRole("combobox", { name: /^Menu size/ }).click();
  await page.getByRole("option", { name: "Icons only" }).click();
  await page.getByRole("button", { name: "Add separator line" }).click();
  await page.getByRole("button", { name: "Save" }).click();
  await expect(page.getByText("Your menu was saved.")).toBeVisible();

  // Names stay for screen readers and on hover.
  await expect(menu(page).getByRole("link", { name: "Taxes" })).toHaveAttribute("title", "Taxes");
  await expect(menu(page).getByRole("separator")).toHaveCount(1);

  await page.getByRole("button", { name: "Back to the default" }).click();
  await expect(menu(page).getByRole("link", { name: "Taxes" })).toHaveCount(0);
});
