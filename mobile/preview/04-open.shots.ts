import { expect, test, type Page } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { mkdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

import { asAna, shot, signIn, signOut, type Say } from "./helpers";

// Runs after 03-actions. Opens files of every kind the app shows itself,
// one it cannot play after all, and one it hands to another app.

/** Real media made by FFmpeg, as the server's tests make theirs. */
function media(): { readonly photo: Buffer; readonly clip: Buffer } {
  const folder = join(process.env.ATRIUM_E2E_ROOT!, "media");
  mkdirSync(folder, { recursive: true });
  const photo = join(folder, "photo.jpg");
  const clip = join(folder, "clip.mp4");
  const run = (args: string[]) => execFileSync("ffmpeg", ["-nostdin", "-v", "error", "-y", ...args]);
  run(["-f", "lavfi", "-i", "testsrc=size=640x480", "-frames:v", "1", photo]);
  run([
    ...["-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=15"],
    ...["-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", clip],
  ]);
  return { photo: readFileSync(photo), clip: readFileSync(clip) };
}

/** A small but valid PDF with one page of text. */
function pdf(): Buffer {
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
    "<< /Length 44 >>\nstream\nBT /F1 24 Tf 72 760 Td (Rental agreement) Tj ET\nendstream",
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
  ];
  let body = "%PDF-1.4\n";
  const offsets: number[] = [];
  objects.forEach((object, i) => {
    offsets.push(Buffer.byteLength(body));
    body += `${i + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = Buffer.byteLength(body);
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  body += offsets.map((o) => `${String(o).padStart(10, "0")} 00000 n \n`).join("");
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(body);
}

/** The viewer in front of the folder. */
const viewer = (page: Page) => page.getByRole("dialog").last();

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`opening files, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("pictures, video, text and PDF inside the app, the rest in another app", async ({
      page,
      request,
      baseURL,
    }) => {
      const say: Say = (en, sr) => (language === "en" ? en : sr);
      const folderName = `Open ${language}`;

      const ana = await asAna(request);
      const folder = await ana.folder(ana.root, folderName);
      const { photo, clip } = media();
      // In the order the folder shows them: by name.
      await ana.upload(folder, "broken.mp4", Buffer.from("not really a video"));
      await ana.upload(folder, "budget.xlsx", Buffer.alloc(2048, 7));
      await ana.upload(folder, "clip.mp4", clip);
      await ana.upload(folder, "notes.txt", Buffer.from("Line one\nLine two: café, šećer\n"));
      await ana.upload(folder, "photo.jpg", photo);
      await ana.upload(folder, "scan.pdf", pdf());

      await signIn(page, baseURL!, say);
      await page.getByText(folderName, { exact: true }).click();
      await expect(page.getByText("scan.pdf", { exact: true }).filter({ visible: true })).toBeVisible();

      // A video the player cannot play: said so, and offered to another app.
      await page.getByText("broken.mp4", { exact: true }).click();
      await expect(viewer(page).getByText(say("1 of 6", "1 od 6"), { exact: false })).toBeVisible();
      await expect(
        viewer(page).getByText(say("This file cannot be shown here.", "Ovaj fajl ne može da se prikaže ovde."), {
          exact: false,
        }),
      ).toBeVisible({ timeout: 30_000 });
      await shot(page, `${language}-60-cannot-play`);

      // A kind Atrium does not show: another app, here a new tab with the file.
      await viewer(page)
        .getByRole("button", { name: say("Next file", "Sledeći fajl") })
        .click();
      await expect(viewer(page).getByText(say("Document", "Dokument"), { exact: true })).toBeVisible();
      await shot(page, `${language}-61-other`);
      // The new tab fetches the file's signed address, as another app on a phone would get the file.
      const fetched = page
        .context()
        .waitForEvent("request", (r) => r.url().includes("/api/v1/nodes/") && r.url().includes("/content"));
      await viewer(page)
        .getByRole("button", { name: say("Open in another app", "Otvori u drugoj aplikaciji"), exact: true })
        .last()
        .click();
      await fetched;
      for (const other of page
        .context()
        .pages()
        .filter((p) => p !== page)) {
        await other.close();
      }

      // Video in the player.
      await viewer(page)
        .getByRole("button", { name: say("Next file", "Sledeći fajl") })
        .click();
      const video = viewer(page).locator("video");
      await expect(video).toBeVisible();
      await expect
        .poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { timeout: 30_000 })
        .toBeGreaterThan(1);
      await shot(page, `${language}-62-video`);

      // Text, as text.
      await viewer(page)
        .getByRole("button", { name: say("Next file", "Sledeći fajl") })
        .click();
      await expect(viewer(page).getByText("Line two: café, šećer", { exact: false })).toBeVisible();
      await shot(page, `${language}-63-text`);

      // A picture.
      await viewer(page)
        .getByRole("button", { name: say("Next file", "Sledeći fajl") })
        .click();
      const picture = viewer(page).getByRole("img", { name: "photo.jpg" });
      await expect(picture).toBeVisible();
      await shot(page, `${language}-64-picture`);

      // PDF, the last file: there is no next.
      await viewer(page)
        .getByRole("button", { name: say("Next file", "Sledeći fajl") })
        .click();
      await expect(viewer(page).locator("iframe")).toBeVisible();
      await expect(viewer(page).getByRole("button", { name: say("Next file", "Sledeći fajl") })).toBeDisabled();
      await shot(page, `${language}-65-pdf`);

      // Details stay one touch away.
      await viewer(page)
        .getByRole("button", { name: say("Details", "Detalji"), exact: true })
        .click();
      await expect(page.getByText(say("PDF document", "PDF dokument"), { exact: true })).toBeVisible();
      await shot(page, `${language}-66-details`);
      await page
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .last()
        .click();

      // Back to the folder.
      await viewer(page)
        .getByRole("button", { name: say("Close", "Zatvori"), exact: true })
        .first()
        .click();
      await expect(page.getByRole("dialog")).toHaveCount(0);
      await expect(page.getByRole("heading", { name: folderName }).filter({ visible: true })).toBeVisible();

      await signOut(page, say);
    });
  });
}
