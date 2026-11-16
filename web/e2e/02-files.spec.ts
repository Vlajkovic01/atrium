import { expect, test, type Page } from "@playwright/test";

/**
 * Working with files, on the server the first-run story set up: folders,
 * uploads by button and by dropping, name conflicts, rename, move,
 * download, the trash and earlier versions.
 */
test.describe.configure({ mode: "serial" });

const OWNER = "owner";
const PASSWORD = "correct horse battery";

function watchForPolicyViolations(page: Page): string[] {
  const violations: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error" && /Content Security Policy|Refused to/i.test(message.text())) {
      violations.push(message.text());
    }
  });
  return violations;
}

async function openFiles(page: Page): Promise<void> {
  await page.goto("/files");
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("Name", { exact: true }).fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "My files" })).toBeVisible();
}

const row = (page: Page, name: string) =>
  page.getByRole("row").filter({ has: page.getByRole("button", { name, exact: true }) });

async function upload(page: Page, files: { name: string; text: string }[]): Promise<void> {
  await page
    .getByTestId("upload-files")
    .setInputFiles(files.map((f) => ({ name: f.name, mimeType: "text/plain", buffer: Buffer.from(f.text) })));
}

test("an empty library invites the first upload, and a folder is made and opened", async ({ page }) => {
  const violations = watchForPolicyViolations(page);
  await openFiles(page);
  await expect(page.getByText("This folder is empty. Drop files here or use Upload files.")).toBeVisible();

  await page.getByRole("button", { name: "New folder" }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("Holiday");
  await page.getByRole("dialog").getByRole("button", { name: "Create" }).click();
  await expect(row(page, "Holiday")).toBeVisible();

  // A name that differs only by case is refused, and the dialog says why.
  await page.getByRole("button", { name: "New folder" }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("HOLIDAY");
  await page.getByRole("dialog").getByRole("button", { name: "Create" }).click();
  await expect(page.getByRole("dialog").getByText("Something with that name is already in this folder.")).toBeVisible();
  await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();

  await page.getByRole("button", { name: "Holiday", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Holiday" })).toBeVisible();
  await expect(
    page.getByRole("navigation", { name: "Location" }).getByRole("link", { name: "My files" }),
  ).toBeVisible();
  expect(violations).toEqual([]);
});

test("files are uploaded, and a taken name asks what to do", async ({ page }) => {
  const violations = watchForPolicyViolations(page);
  await openFiles(page);

  await upload(page, [
    { name: "notes.txt", text: "first notes" },
    { name: "plan.txt", text: "the plan" },
  ]);
  await expect(page.getByRole("region", { name: "Uploads" }).getByText("Uploads finished")).toBeVisible();
  await expect(row(page, "notes.txt")).toBeVisible();
  await expect(row(page, "plan.txt")).toBeVisible();

  // The same name again: keep both.
  await upload(page, [{ name: "notes.txt", text: "other notes" }]);
  const dialog = page.getByRole("dialog", { name: "This name is already taken" });
  await expect(dialog.getByText("Something called “notes.txt” is already in this folder.")).toBeVisible();
  await dialog.getByRole("button", { name: "Keep both" }).click();
  await expect(row(page, "notes (2).txt")).toBeVisible();

  // And once more: replace, which keeps the old content as an earlier version.
  await upload(page, [{ name: "plan.txt", text: "the better plan" }]);
  await page
    .getByRole("dialog", { name: "This name is already taken" })
    .getByRole("button", { name: "Replace" })
    .click();
  await expect(page.getByRole("region", { name: "Uploads" }).getByText("Uploads finished")).toBeVisible();
  // Opening a file shows it; its details are one click away.
  await page.getByRole("button", { name: "plan.txt", exact: true }).click();
  const preview = page.getByRole("dialog", { name: "plan.txt" });
  await expect(preview.getByText("the better plan")).toBeVisible();
  await preview.getByRole("button", { name: "Details" }).click();
  const details = page.getByRole("complementary", { name: "Details" });
  await expect(details.getByRole("heading", { name: "Earlier versions" })).toBeVisible();
  await expect(details.getByRole("button", { name: "Make current" })).toHaveCount(1);
  expect(violations).toEqual([]);
});

test("files dropped onto the page are uploaded into the open folder", async ({ page }) => {
  await openFiles(page);
  await page.getByRole("button", { name: "Holiday", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Holiday" })).toBeVisible();

  const transfer = await page.evaluateHandle(() => {
    const data = new DataTransfer();
    data.items.add(new File(["sunny"], "beach.txt", { type: "text/plain" }));
    return data;
  });
  const target = page.locator(".browser");
  await target.dispatchEvent("dragover", { dataTransfer: transfer });
  await expect(page.getByText("Drop to upload into this folder")).toBeVisible();
  await target.dispatchEvent("drop", { dataTransfer: transfer });

  await expect(row(page, "beach.txt")).toBeVisible();
});

test("a file is renamed, moved and downloaded", async ({ page }) => {
  await openFiles(page);

  await row(page, "notes (2).txt").getByRole("checkbox").check();
  await page.getByRole("toolbar").getByRole("button", { name: "Rename" }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("Notes from Ana.txt");
  await page.getByRole("dialog").getByRole("button", { name: "Rename" }).click();
  await expect(row(page, "Notes from Ana.txt")).toBeVisible();

  await row(page, "Notes from Ana.txt").getByRole("checkbox").check();
  await page.getByRole("toolbar").getByRole("button", { name: "Move", exact: true }).click();
  const picker = page.getByRole("dialog");
  await picker.getByRole("button", { name: "Holiday" }).click();
  await picker.getByRole("button", { name: "Move here" }).click();
  await expect(row(page, "Notes from Ana.txt")).toHaveCount(0);

  await page.getByRole("button", { name: "Holiday", exact: true }).click();
  await row(page, "Notes from Ana.txt").getByRole("checkbox").check();
  const download = page.waitForEvent("download");
  await page.getByRole("toolbar").getByRole("button", { name: "Download" }).click();
  expect((await download).suggestedFilename()).toBe("Notes from Ana.txt");
});

test("deleting goes to the trash, can be undone, restored, and only then deleted for good", async ({ page }) => {
  await openFiles(page);

  await row(page, "plan.txt").getByRole("checkbox").check();
  await page.getByRole("toolbar").getByRole("button", { name: "Move to trash" }).click();
  await expect(page.getByText("1 item moved to the trash.")).toBeVisible();
  await expect(row(page, "plan.txt")).toHaveCount(0);
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(row(page, "plan.txt")).toBeVisible();

  await row(page, "plan.txt").getByRole("checkbox").check();
  await page.keyboard.press("Delete");
  await expect(row(page, "plan.txt")).toHaveCount(0);

  await page.getByRole("link", { name: "Trash" }).click();
  const trashed = page.getByRole("listitem").filter({ hasText: "plan.txt" });
  await expect(trashed.getByText("removed in 30 days")).toBeVisible();
  // Where it came from, with a link back to that folder.
  await expect(trashed.getByText("From", { exact: true })).toBeVisible();
  await expect(trashed.getByRole("link", { name: "My files" })).toBeVisible();
  await trashed.getByRole("button", { name: "Restore" }).click();
  await expect(page.getByText("The trash is empty.")).toBeVisible();

  await page.getByRole("link", { name: "Files" }).click();
  await row(page, "plan.txt").getByRole("checkbox").check();
  await page.keyboard.press("Delete");
  await page.getByRole("link", { name: "Trash" }).click();
  await page.getByRole("button", { name: "Empty trash" }).click();
  const confirm = page.getByRole("dialog", { name: "Empty the trash?" });
  await expect(
    confirm.getByText(/^1 item \(15 .+\) will be deleted permanently\. This cannot be undone\.$/),
  ).toBeVisible();
  await confirm.getByRole("button", { name: "Delete permanently" }).click();
  await expect(page.getByText("The trash is empty.")).toBeVisible();
});

test("the account page shows the space used", async ({ page }) => {
  await openFiles(page);
  await page.getByRole("link", { name: "Account" }).click();
  await expect(page.getByRole("heading", { name: "Storage" })).toBeVisible();
  await expect(page.getByText(/^You are using/)).toBeVisible();
});
