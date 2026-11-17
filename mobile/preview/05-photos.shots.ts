import { expect, test, type APIRequestContext } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

import { SERVER, asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 04-open. Puts pictures of several shapes on three days in
// two months, and a video, then looks at the timeline.

/** A picture made by FFmpeg: a test pattern or a plain colour, in the given size. */
function picture(name: string, source: string, size: string): Buffer {
  const folder = join(process.env.ATRIUM_E2E_ROOT!, "photos");
  mkdirSync(folder, { recursive: true });
  const file = join(folder, name);
  execFileSync("ffmpeg", [
    "-nostdin",
    "-v",
    "error",
    "-y",
    "-f",
    "lavfi",
    "-i",
    `${source}${source.includes("=") ? ":" : "="}size=${size}`,
    "-frames:v",
    "1",
    file,
  ]);
  return readFileSync(file);
}

function video(name: string): Buffer {
  const folder = join(process.env.ATRIUM_E2E_ROOT!, "photos");
  const file = join(folder, name);
  execFileSync("ffmpeg", [
    ...["-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=duration=65:size=320x240:rate=5"],
    ...["-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", file],
  ]);
  return readFileSync(file);
}

/** Waits until the server has read every picture and made its thumbnail; that is background work. */
async function untilProcessed(
  request: APIRequestContext,
  headers: Record<string, string>,
  count: number,
): Promise<void> {
  await expect
    .poll(
      async () => {
        const page = (await (await request.get(`${SERVER}/api/v1/timeline?limit=500`, { headers })).json()) as {
          items: { id: string }[];
        };
        if (page.items.length < count) {
          return false;
        }

        for (const item of page.items) {
          const info = (await (
            await request.get(`${SERVER}/api/v1/nodes/${item.id}/media-info`, { headers })
          ).json()) as {
            thumbnailState: string;
          };
          if (info.thumbnailState === "pending") {
            return false;
          }
        }

        return true;
      },
      { timeout: 120_000, intervals: [1000] },
    )
    .toBe(true);
}

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`photos, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("the timeline by day, sizes, a month, and opening a picture", async ({ page, request, baseURL }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const ana = await asAna(request);
      // Put in place once; the second language finds the folder already there.
      if (!(await ana.children(ana.root)).some((item) => item.name === "Camera")) {
        const folder = await ana.folder(ana.root, "Camera");
        const day = (iso: string) => new Date(iso);
        const shapes: [string, string, string, string][] = [
          ["beach.jpg", "testsrc", "640x480", "2026-10-14T10:00:00"],
          ["dunes.jpg", "testsrc2", "1200x400", "2026-10-14T11:00:00"],
          ["tower.jpg", "smptebars", "480x720", "2026-10-14T12:00:00"],
          ["sky.jpg", "color=c=0x3a7bd5", "800x600", "2026-10-14T13:00:00"],
          ["market.jpg", "testsrc", "720x480", "2026-10-15T09:00:00"],
          ["street.jpg", "smptehdbars", "960x540", "2026-10-15T09:30:00"],
          ["garden.jpg", "color=c=0x2e8b57", "600x800", "2026-10-15T10:00:00"],
          ["winter.jpg", "testsrc2", "640x640", "2026-09-02T08:00:00"],
          ["lake.jpg", "color=c=0x1c6b58", "1024x576", "2026-09-02T08:30:00"],
        ];
        for (const [name, source, size, when] of shapes) {
          await ana.upload(folder, name, picture(name, source, size), day(when));
        }

        await ana.upload(folder, "walk.mp4", video("walk.mp4"), day("2026-10-15T18:00:00"));
      }

      // At least the ten put in place here; 04-open may have added more.
      await untilProcessed(request, ana.headers, 10);

      await signIn(page, baseURL!, say);
      await page.getByRole("tab", { name: say("Photos", "Fotografije") }).click();
      await expect(page.getByRole("heading", { name: /2026/ }).first()).toBeVisible();
      await shot(page, `${language}-70-timeline-first`);
      await expect(page.getByRole("button", { name: "beach.jpg" })).toBeVisible();
      // A video shows its length.
      await expect(page.getByText("1:05", { exact: true })).toBeVisible();
      await shot(page, `${language}-70-timeline`);

      await page.getByRole("button", { name: say("Larger pictures", "Veće slike") }).click();
      await shot(page, `${language}-71-larger`);
      await page.getByRole("button", { name: say("Smaller pictures", "Manje slike") }).click();
      await page.getByRole("button", { name: say("Smaller pictures", "Manje slike") }).click();
      await expect(page.getByRole("button", { name: say("Smaller pictures", "Manje slike") })).toBeDisabled();
      await shot(page, `${language}-72-smaller`);
      await page.getByRole("button", { name: say("Larger pictures", "Veće slike") }).click();

      // Straight to an older month.
      await page.getByRole("button", { name: say("Jump to a month", "Skok na mesec") }).click();
      await shot(page, `${language}-73-months`);
      await page.getByRole("button", { name: new RegExp(say("September 2026", "septembar 2026"), "i") }).click();
      await expect(page.getByRole("button", { name: "winter.jpg" })).toBeVisible();
      await expect(page.getByRole("button", { name: "beach.jpg" })).toHaveCount(0);
      await shot(page, `${language}-74-month`);
      await page.getByRole("button", { name: say("Back to the newest", "Nazad na najnovije") }).click();
      await expect(page.getByRole("button", { name: "beach.jpg" })).toBeVisible();

      // A picture opens in the viewer, with what is known of it.
      await page.getByRole("button", { name: "market.jpg" }).click();
      const viewer = page.getByRole("dialog").last();
      await expect(viewer.getByRole("img", { name: "market.jpg" })).toBeVisible();
      await shot(page, `${language}-75-viewer`);
      await viewer.getByRole("button", { name: say("Details", "Detalji"), exact: true }).click();
      await expect(page.getByText(say("Taken", "Snimljeno"), { exact: true })).toBeVisible();
      await expect(page.getByText("720 × 480", { exact: true })).toBeVisible();
      await shot(page, `${language}-76-info`);
      await page
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .last()
        .click();
      await page
        .getByRole("dialog")
        .last()
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .first()
        .click();

      // A video plays in the same viewer.
      await page.getByRole("button", { name: "walk.mp4" }).click();
      await expect(page.getByRole("dialog").last().locator("video")).toBeVisible();
      await page
        .getByRole("dialog")
        .last()
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .first()
        .click();

      await signOut(page, say);
    });
  });
}
