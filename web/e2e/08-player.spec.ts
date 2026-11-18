import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { expect, test, type Page } from "@playwright/test";

/**
 * The web player on the server the earlier stories set up (DESIGN.md 7.3,
 * 7.4): an MPEG-TS film, which browsers do not open as a file, is remuxed into HLS by a real
 * FFmpeg and plays; the info panel says why; a quality the original does
 * not fit is converted and plays from where it was; the keyboard seeks.
 * Needs a browser that plays H.264 and AAC: Edge locally, Chrome in CI.
 */
test.describe.configure({ mode: "serial" });

const OWNER = "owner";
const PASSWORD = "correct horse battery";

/** Thirty seconds of picture and tone in MPEG-TS, made here so the test carries no binary file. */
function film(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), "atrium-e2e-"));
  try {
    const path = join(dir, "film.m2ts");
    execFileSync("ffmpeg", [
      "-nostdin",
      "-v",
      "error",
      "-f",
      "lavfi",
      "-i",
      "testsrc=duration=30:size=640x360:rate=24",
      "-f",
      "lavfi",
      "-i",
      "sine=frequency=440:duration=30",
      "-c:v",
      "libx264",
      "-preset",
      "ultrafast",
      "-g",
      "48",
      "-keyint_min",
      "48",
      "-sc_threshold",
      "0",
      "-pix_fmt",
      "yuv420p",
      // A steady 4 Mbit/s, so a low quality cannot carry the original.
      ...["-b:v", "4M", "-minrate", "4M", "-maxrate", "4M", "-bufsize", "4M", "-x264-params", "nal-hrd=cbr"],
      "-c:a",
      "aac",
      "-shortest",
      "-y",
      path,
    ]);
    return readFileSync(path);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

async function signIn(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("Name", { exact: true }).fill(OWNER);
  await page.getByLabel("Password", { exact: true }).fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
}

async function playsOn(page: Page): Promise<void> {
  const video = page.getByTestId("player-video");
  await expect
    .poll(async () => video.evaluate((v: HTMLVideoElement) => v.currentTime), { timeout: 60_000 })
    .toBeGreaterThan(1);
}

test("an MPEG-TS film is remuxed, plays, says why, changes quality and seeks with the keyboard", async ({ page }) => {
  await signIn(page, "/files");
  await expect(page.getByRole("heading", { name: "My files" })).toBeVisible();
  await page.getByTestId("upload-files").setInputFiles([{ name: "film.m2ts", mimeType: "video/mp2t", buffer: film() }]);
  await expect(page.getByRole("region", { name: "Uploads" }).getByText("Uploads finished")).toBeVisible();

  await page.getByRole("button", { name: "film.m2ts", exact: true }).click();
  const player = page.getByRole("region", { name: "Video player: film.m2ts" });
  await expect(player).toBeVisible();

  // The server reads the video first; the player waits, then plays a stream.
  await playsOn(page);
  await expect(page.getByTestId("player-video")).toHaveAttribute("src", /^blob:|\/stream\//);

  await player.getByRole("button", { name: "Playback info" }).click();
  const info = player.getByRole("status", { name: "Playback info" });
  await expect(info).toContainText("Streaming the original video and sound in another container");
  await expect(info).toContainText("This device does not open the file's format.");
  await page.screenshot({ path: "test-results/player-remux.png" });

  // A low quality the original does not fit: converted, from where it was.
  const before = await page.getByTestId("player-video").evaluate((v: HTMLVideoElement) => v.currentTime);
  await player.getByLabel("Quality").selectOption({ label: "360p" });
  await expect(info).toContainText("Streaming the video converted", { timeout: 30_000 });
  await expect(info).toContainText("The video needs a faster connection than the quality chosen allows.");
  await expect
    .poll(async () => page.getByTestId("player-video").evaluate((v: HTMLVideoElement) => v.currentTime), {
      timeout: 60_000,
    })
    .toBeGreaterThan(before);

  // The arrow keys skip ten seconds.
  await player.focus();
  const now = await page.getByTestId("player-video").evaluate((v: HTMLVideoElement) => v.currentTime);
  await page.keyboard.press("ArrowRight");
  await expect
    .poll(async () => page.getByTestId("player-video").evaluate((v: HTMLVideoElement) => v.currentTime), {
      timeout: 30_000,
    })
    .toBeGreaterThan(now + 8);
});
