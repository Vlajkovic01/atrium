import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { expect, test, type Page } from "@playwright/test";

/**
 * An existing folder on the server, of any kind of files: everything in
 * it shows in Files, its photos and videos join the timeline, and once an
 * admin allows changes for a person, that person changes the real files.
 */
test.describe.configure({ mode: "serial" });

const OWNER = "owner";
const PASSWORD = "correct horse battery";
const folder = join(process.env.ATRIUM_E2E_ROOT!, "shared-folder");

test.beforeAll(() => {
  mkdirSync(join(folder, "Contracts", "2024"), { recursive: true });
  writeFileSync(join(folder, "Contracts", "2024", "lease.pdf"), "%PDF-1.4 a lease");
  writeFileSync(join(folder, "budget.xlsx"), "not really a spreadsheet");
  writeFileSync(join(folder, "notes.txt"), "plain notes");
  // A real two-second video, made with FFmpeg, as the server itself needs it.
  const made = spawnSync("ffmpeg", [
    "-nostdin",
    "-v",
    "error",
    "-f",
    "lavfi",
    "-i",
    "testsrc=duration=2:size=160x120:rate=10",
    "-c:v",
    "libx264",
    "-pix_fmt",
    "yuv420p",
    "-y",
    join(folder, "clip.mp4"),
  ]);
  expect(made.status, String(made.stderr)).toBe(0);
});

async function signIn(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("Name", { exact: true }).fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
}

test("an admin adds an existing folder and every file in it is there to browse", async ({ page }) => {
  await signIn(page, "/libraries");
  const card = page.locator(".card").filter({ hasText: "Add an existing folder" });
  await card.getByLabel("Name", { exact: true }).fill("Shared");
  await card.getByLabel("Folder on the server").fill(folder);
  await card.getByRole("button", { name: "Check folder" }).click();
  await expect(card.getByText(/The folder can be read\. 4 items/)).toBeVisible();
  await expect(card.getByLabel("Show its photos and videos in the timeline")).toBeChecked();
  await card.getByLabel(new RegExp(OWNER)).check();
  await card.getByRole("button", { name: "Add" }).click();
  await expect(page.getByText("The folder was added and is being read.")).toBeVisible();

  // From the menu, under Files.
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Shared" }).click();
  await expect(page.getByRole("heading", { name: "Shared" })).toBeVisible();
  for (const name of ["Contracts", "budget.xlsx", "clip.mp4", "notes.txt"]) {
    await expect(page.getByRole("button", { name, exact: true })).toBeVisible();
  }

  await page.getByRole("button", { name: "Contracts", exact: true }).click();
  await page.getByRole("button", { name: "2024", exact: true }).click();
  await expect(page.getByRole("button", { name: "lease.pdf", exact: true })).toBeVisible();

  // Nothing can be changed yet, and the page says why and where.
  await expect(page.getByText(/nothing in it can be changed here yet/)).toBeVisible();
  await expect(page.getByRole("link", { name: "Open Libraries" })).toBeVisible();
});

test("its video joins the timeline", async ({ page }) => {
  await signIn(page, "/photos");
  await expect(page.getByRole("button", { name: "clip.mp4", exact: true })).toBeVisible();
});

test("allowing changes chooses who makes them, and a rename reaches the real file", async ({ page }) => {
  await signIn(page, "/libraries");
  const row = page.getByRole("listitem").filter({ hasText: "Shared" });
  await row.getByRole("button", { name: "Allow changes" }).click();
  const dialog = page.getByRole("dialog", { name: "Allow changes to Shared?" });
  await expect(dialog.getByLabel(new RegExp(OWNER))).toBeChecked();
  await dialog.getByLabel("Type Shared to confirm").fill("Shared");
  await dialog.getByRole("button", { name: "Allow changes" }).click();
  await expect(row.getByText("1 person may change files")).toBeVisible();

  await row.getByRole("link", { name: "Open" }).click();
  await expect(page.getByRole("button", { name: "New folder" })).toBeVisible();
  const notes = page.getByRole("row").filter({ has: page.getByRole("button", { name: "notes.txt", exact: true }) });
  await notes.getByRole("checkbox").check();
  await page.getByRole("toolbar").getByRole("button", { name: "Rename" }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("meeting notes.txt");
  await page.getByRole("dialog").getByRole("button", { name: "Rename" }).click();
  await expect(page.getByRole("button", { name: "meeting notes.txt", exact: true })).toBeVisible();

  expect(existsSync(join(folder, "meeting notes.txt"))).toBe(true);
  expect(existsSync(join(folder, "notes.txt"))).toBe(false);
});
