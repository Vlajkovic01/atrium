import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { createHash } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

// Runs after connect.shots.ts (file names are run in order), which sets
// the server up and leaves the admin "ana" with an empty library.
const SERVER = "http://127.0.0.1:5192";
const OUT = resolve("../screenshots/mobile");
const PASSWORD = "correct horse battery";

mkdirSync(OUT, { recursive: true });

async function shot(page: Page, name: string): Promise<void> {
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(OUT, `${name}.png`) });
}

interface Node {
  readonly id: string;
}

/** Files of many kinds, nested folders, a folder with more than one page, and a folder of the server shown as it is. */
async function seed(request: APIRequestContext): Promise<void> {
  const login = await request.post(`${SERVER}/api/v1/auth/login`, {
    data: {
      name: "ana",
      password: PASSWORD,
      device: { name: "Preview seed", kind: "other", platform: null, appVersion: null },
    },
  });
  const tokens = ((await login.json()) as { tokens: { accessToken: string; user: { id: string } } }).tokens;
  const token = tokens.accessToken;
  const headers = { Authorization: `Bearer ${token}` };

  const libraries = (await (await request.get(`${SERVER}/api/v1/libraries`, { headers })).json()) as {
    kind: string;
    rootNodeId: string;
  }[];
  const root = libraries.find((l) => l.kind === "personal")!.rootNodeId;
  const existing = (await (await request.get(`${SERVER}/api/v1/nodes/${root}/children`, { headers })).json()) as {
    items: unknown[];
  };
  if (existing.items.length > 0) {
    return;
  }

  const folder = async (parentId: string, name: string): Promise<string> => {
    const response = await request.post(`${SERVER}/api/v1/nodes`, { headers, data: { parentId, name } });
    expect(response.ok(), await response.text()).toBe(true);
    return ((await response.json()) as Node).id;
  };

  // The upload protocol by hand (tus): create, then send the content in the server's chunk size.
  const upload = async (parentId: string, name: string, content: Buffer): Promise<void> => {
    const tus = { ...headers, "Tus-Resumable": "1.0.0" };
    const metadata = Object.entries({ parentId, name, onConflict: "fail" })
      .map(([key, value]) => `${key} ${Buffer.from(value).toString("base64")}`)
      .join(",");
    const created = await request.post(`${SERVER}/api/v1/uploads`, {
      headers: { ...tus, "Upload-Length": String(content.length), "Upload-Metadata": metadata },
    });
    expect(created.status(), `${name}: ${await created.text()}`).toBeLessThan(300);
    const location = created.headers()["location"]!;
    const chunkSize = Number(created.headers()["atrium-chunk-size"]) || content.length;
    for (let offset = 0; offset < content.length; offset += chunkSize) {
      const chunk = content.subarray(offset, offset + chunkSize);
      const sent = await request.patch(location.startsWith("http") ? location : `${SERVER}${location}`, {
        headers: {
          ...tus,
          "Content-Type": "application/offset+octet-stream",
          "Upload-Offset": String(offset),
          "Upload-Checksum": `sha256 ${createHash("sha256").update(chunk).digest("base64")}`,
        },
        data: chunk,
      });
      expect(sent.status(), `${name}: ${await sent.text()}`).toBeLessThan(300);
    }
  };

  const documents = await folder(root, "Documents");
  await folder(root, "Holiday pictures");
  const taxes = await folder(documents, "Taxes");
  const year = await folder(taxes, "2026 with a rather long folder name that does not fit");
  await upload(year, "summary.txt", Buffer.from("Nothing owed."));

  await upload(root, "Rental agreement.pdf", Buffer.from("%PDF-1.4\n%%EOF\n"));
  await upload(root, "Budget.xlsx", Buffer.alloc(48_000, 1));
  await upload(root, "notes.md", Buffer.from("# Notes\n"));
  await upload(root, "backup.tar.gz", Buffer.alloc(3_500_000, 2));
  await upload(root, "voice memo.mp3", Buffer.alloc(900_000, 3));
  await upload(
    root,
    "A file whose name is long enough to need a second line on a phone screen, and then some more.txt",
    Buffer.from("x"),
  );
  await upload(root, "program.bin", Buffer.alloc(10, 4));

  // More than one page (the app asks for 200 at a time).
  const many = await folder(root, "Many files");
  for (let i = 1; i <= 230; i++) {
    await upload(many, `file ${String(i).padStart(3, "0")}.txt`, Buffer.from(String(i)));
  }

  // A folder that already exists on the server: shown as it is, nothing in it can be changed.
  const shared = join(process.env.ATRIUM_E2E_ROOT!, "existing");
  mkdirSync(join(shared, "Scans"), { recursive: true });
  writeFileSync(join(shared, "Scans", "passport.txt"), "scan");
  writeFileSync(join(shared, "readme.txt"), "hello");
  const library = await request.post(`${SERVER}/api/v1/admin/libraries`, {
    headers,
    data: { name: "Family archive", path: shared, userIds: [tokens.user.id], showInTimeline: false },
  });
  expect(library.ok(), await library.text()).toBe(true);
}

for (const [language, locale, scheme] of [
  ["en", "en-US", "light"],
  ["sr", "sr-Latn-RS", "dark"],
] as const) {
  test.describe(`files, ${language}, ${scheme}`, () => {
    test.use({ locale, colorScheme: scheme });

    test("browsing folders, libraries and orders", async ({ page, request, baseURL }) => {
      await seed(request);
      const text = (en: string, sr: string) => (language === "en" ? en : sr);

      // Sign in by typing the address.
      await page.goto("/");
      await page.getByRole("button", { name: text("Type the server's address", "Upiši adresu servera") }).click();
      await page.getByLabel(text("Address", "Adresa")).fill(new URL(baseURL!).host);
      await page.getByRole("button", { name: text("Continue", "Nastavi") }).click();
      await page.getByLabel(text("Name", "Ime")).fill("ana");
      await page.getByLabel(text("Password", "Lozinka")).fill(PASSWORD);
      await page.getByRole("button", { name: text("Sign in", "Prijavi se") }).click();

      // The app opens on Files, at the person's own files.
      await expect(page.getByRole("heading", { name: text("My files", "Moji fajlovi") })).toBeVisible();
      await expect(page.getByText("Rental agreement.pdf")).toBeVisible();
      await shot(page, `${language}-20-files`);

      // Folders come first, then names A to Z.
      const names = page.getByRole("button").filter({ hasText: /Documents|Budget|notes/ });
      await expect(names.first()).toContainText("Documents");

      // Another order: asked of the server, remembered by the app.
      await page
        .getByRole("button", { name: new RegExp(text("^Sort by", "^Poređaj po")) })
        .filter({ visible: true })
        .click();
      await shot(page, `${language}-21-sort`);
      await page.getByRole("button", { name: text("Largest first", "Najveće prvo") }).click();
      await expect(
        page
          .getByRole("button")
          .filter({ hasText: /\.(pdf|xlsx|gz|mp3|md|bin|txt)/ })
          .first(),
      ).toContainText("backup.tar.gz");
      await shot(page, `${language}-22-sorted-by-size`);

      // What a file is.
      await page.getByText("backup.tar.gz").click();
      await expect(page.getByText(text("Archive", "Arhiva"), { exact: true })).toBeVisible();
      await shot(page, `${language}-23-details`);
      await page
        .getByRole("button", { name: text("Close", "Zatvori") })
        .last()
        .click();

      // Down three folders; the way back up is shown.
      await page.getByText("Documents").click();
      await page.getByText("Taxes").click();
      await page.getByText(/2026 with a rather long/).click();
      await expect(page.getByText("summary.txt")).toBeVisible();
      await shot(page, `${language}-24-nested`);

      // Up by the path, two folders at once.
      await page.getByRole("link", { name: "Documents" }).click();
      await expect(page.getByRole("heading", { name: "Documents" }).filter({ visible: true })).toBeVisible();
      await expect(page.getByText("Taxes", { exact: true }).filter({ visible: true })).toHaveCount(1);
      await expect(page.getByText("summary.txt").filter({ visible: true })).toHaveCount(0);

      // Up by the back button.
      await page
        .getByRole("button", { name: text("Back", "Nazad") })
        .filter({ visible: true })
        .click();
      await expect(
        page.getByRole("heading", { name: text("My files", "Moji fajlovi") }).filter({ visible: true }),
      ).toBeVisible();

      // An empty folder says so.
      await page.getByText("Holiday pictures").filter({ visible: true }).click();
      await expect(page.getByText(text("This folder is empty.", "Ovaj folder je prazan."))).toBeVisible();
      await shot(page, `${language}-25-empty`);
      await page
        .getByRole("button", { name: text("Back", "Nazad") })
        .filter({ visible: true })
        .click();

      // A folder with more files than one page: the rest arrives while scrolling.
      await page
        .getByRole("button", { name: new RegExp(text("^Sort by", "^Poređaj po")) })
        .filter({ visible: true })
        .click();
      await page.getByRole("button", { name: text("Name, A to Z", "Ime, od A do Ž") }).click();
      await page.getByText("Many files").filter({ visible: true }).click();
      await expect(page.getByText("file 001.txt")).toBeVisible();
      await page.getByText("file 001.txt").hover();
      for (let i = 0; i < 40 && !(await page.getByText("file 230.txt").isVisible()); i++) {
        await page.mouse.wheel(0, 4000);
        await page.waitForTimeout(150);
      }

      await expect(page.getByText("file 230.txt")).toBeVisible();
      await expect(page.getByText(text("230 items", "230 stavki"))).toBeVisible();
      await shot(page, `${language}-26-many-end`);
      await page
        .getByRole("button", { name: text("Back", "Nazad") })
        .filter({ visible: true })
        .click();

      // The other library: a folder of the server, read only, with the reason.
      await page.getByRole("button", { name: "Family archive" }).click();
      await expect(page.getByText("readme.txt")).toBeVisible();
      await expect(page.getByText(text("nothing in it can be changed here", "u njemu ništa ne može"))).toBeVisible();
      await shot(page, `${language}-27-library-read-only`);
      await page.getByText("Scans").click();
      await expect(page.getByText("passport.txt")).toBeVisible();
      await shot(page, `${language}-28-library-folder`);

      // The other tabs.
      await page.getByRole("tab", { name: text("Photos", "Fotografije") }).click();
      await shot(page, `${language}-29-photos`);
      await page.getByRole("tab", { name: text("Account", "Nalog") }).click();
      await expect(page.getByText(text("ana on", "ana na"))).toBeVisible();
      await shot(page, `${language}-30-account`);

      // After a restart the app is still signed in, opens on Files and remembers the order.
      await page.goto("/");
      await expect(page.getByRole("heading", { name: text("My files", "Moji fajlovi") })).toBeVisible();
      await expect(page.getByText("Rental agreement.pdf")).toBeVisible();

      // When the server cannot be reached, the screen says so and offers to try again.
      await page.route("**/api/v1/nodes/**", (route) => route.abort());
      await page.getByText("Documents").filter({ visible: true }).click();
      await expect(page.getByRole("alert")).toBeVisible({ timeout: 30_000 });
      await shot(page, `${language}-31-unreachable`);
      await page.unroute("**/api/v1/nodes/**");
      await page.getByRole("button", { name: text("Try again", "Pokušaj ponovo") }).click();
      await expect(page.getByText("Taxes", { exact: true }).filter({ visible: true })).toHaveCount(1);

      // Leave the server as the next run expects it.
      await page.getByRole("tab", { name: text("Account", "Nalog") }).click();
      await page.getByRole("button", { name: text("Sign out of this server", "Odjavi se sa ovog servera") }).click();
      await page.getByRole("button", { name: text("Sign out of this server", "Odjavi se sa ovog servera") }).click();
      await expect(
        page.getByText(text("Your files, on your own server", "Vaši fajlovi, na vašem serveru")),
      ).toBeVisible();
    });
  });
}
