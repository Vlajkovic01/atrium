import { crc32, deflateSync } from "node:zlib";

import { expect, test, type Page } from "@playwright/test";

/**
 * Photos on the server the earlier stories set up: an uploaded picture
 * appears on the timeline with a real thumbnail, opens in the viewer, goes
 * into an album and is found by search. Nothing breaks the content policy.
 */
test.describe.configure({ mode: "serial" });

const OWNER = "owner";
const PASSWORD = "correct horse battery";

/** A real PNG of the given size in one colour, made here so the test carries no binary file. */
function png(width: number, height: number, rgb: [number, number, number]): Buffer {
  const chunk = (type: string, data: Buffer): Buffer => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8);
  const row = Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: width }, () => rgb).flat())]);
  const pixels = Buffer.concat(Array.from({ length: height }, () => row));
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", header),
    chunk("IDAT", deflateSync(pixels)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

const PNG = png(64, 48, [230, 120, 40]);

function watchForPolicyViolations(page: Page): string[] {
  const violations: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error" && /Content Security Policy|Refused to/i.test(message.text())) {
      violations.push(message.text());
    }
  });
  return violations;
}

async function signIn(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("Name", { exact: true }).fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
}

test("an uploaded picture appears on the timeline with its thumbnail and opens in the viewer", async ({ page }) => {
  const violations = watchForPolicyViolations(page);
  await signIn(page, "/files");
  await expect(page.getByRole("heading", { name: "My files" })).toBeVisible();
  await page.getByTestId("upload-files").setInputFiles([{ name: "sunset.png", mimeType: "image/png", buffer: PNG }]);
  await expect(page.getByRole("region", { name: "Uploads" }).getByText("Uploads finished")).toBeVisible();

  await page.getByRole("link", { name: "Photos" }).click();
  await expect(page.getByRole("heading", { name: "Photos" })).toBeVisible();
  const tile = page.getByRole("button", { name: "sunset.png", exact: true });
  await expect(tile).toBeVisible();

  // The thumbnail really loaded: made by the server, through a signed URL.
  await expect
    .poll(async () => tile.locator("img").evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0))
    .toBe(true);

  // The scrubber offers the month.
  await expect(page.getByRole("navigation", { name: "Jump to a month" })).toBeVisible();

  await tile.click();
  const viewer = page.getByRole("dialog", { name: "sunset.png" });
  await expect(viewer).toBeVisible();
  await viewer.getByRole("button", { name: "Information" }).click();
  await expect(viewer.getByText("Size in pixels")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(viewer).toHaveCount(0);
  expect(violations).toEqual([]);
});

test("selected photos go into a new album", async ({ page }) => {
  const violations = watchForPolicyViolations(page);
  await signIn(page, "/photos");
  await page.getByRole("button", { name: "Select sunset.png" }).click();
  await expect(page.getByText("1 item selected")).toBeVisible();
  await page.getByRole("toolbar").getByRole("button", { name: "Add to album" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Album name").fill("Summer");
  await dialog.getByRole("button", { name: "Create album" }).click();
  await expect(page.getByText("1 item added to Summer.")).toBeVisible();

  await page.getByRole("link", { name: "Albums" }).click();
  await page.getByRole("link", { name: /Summer/ }).click();
  await expect(page.getByRole("heading", { name: "Summer" })).toBeVisible();
  await expect(page.getByRole("button", { name: "sunset.png", exact: true })).toBeVisible();
  expect(violations).toEqual([]);
});

test("search finds the picture by name and by type", async ({ page }) => {
  await signIn(page, "/search");
  await page.getByRole("searchbox").fill("SUNS");
  await expect(page.getByRole("button", { name: "sunset.png", exact: true })).toBeVisible();

  await page.getByRole("searchbox").fill("");
  await page.getByRole("button", { name: "Documents" }).click();
  await expect(page.getByRole("button", { name: "notes.txt" })).toBeVisible();
  await expect(page.getByRole("button", { name: "sunset.png", exact: true })).toHaveCount(0);
});
