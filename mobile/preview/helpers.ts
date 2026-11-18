import { expect, type APIRequestContext, type Page } from "@playwright/test";
import { createHash } from "node:crypto";
import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";

// What the preview scenarios share. Scenarios run in the order of their
// file names on one server: 01 sets it up, the others add what they need.

export const SERVER = "http://127.0.0.1:5192";
export const PASSWORD = "correct horse battery";
const OUT = resolve("../screenshots/mobile");

mkdirSync(OUT, { recursive: true });

export async function shot(page: Page, name: string): Promise<void> {
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(OUT, `${name}.png`) });
}

/** A text in the scenario's language. */
export type Say = (en: string, sr: string) => string;

/** Signs in to the preview server by typing its address, as a person would. */
export async function signIn(page: Page, baseURL: string, say: Say): Promise<void> {
  await page.goto("/");
  await page.getByRole("button", { name: say("Type the server's address", "Upiši adresu servera") }).click();
  await page.getByLabel(say("Address", "Adresa")).fill(new URL(baseURL).host);
  await page.getByRole("button", { name: say("Continue", "Nastavi") }).click();
  await page.getByLabel(say("Name", "Ime")).fill("ana");
  await page.getByLabel(say("Password", "Lozinka")).fill(PASSWORD);
  await page.getByRole("button", { name: say("Sign in", "Prijavi se") }).click();
  // The app opens on Photos; the scenarios that use this work in Files.
  await expect(page.getByRole("heading", { name: say("Photos", "Fotografije") })).toBeVisible();
  await page.getByRole("tab", { name: say("Files", "Fajlovi") }).click();
  await expect(page.getByRole("heading", { name: say("My files", "Moji fajlovi") })).toBeVisible();
}

/** Signs out from the Account tab, leaving the app as the next scenario expects it. */
export async function signOut(page: Page, say: Say): Promise<void> {
  await page.getByRole("tab", { name: say("Account", "Nalog") }).click();
  const button = page.getByRole("button", { name: say("Sign out of this server", "Odjavi se sa ovog servera") });
  await button.click();
  await button.click();
  await expect(
    page
      .getByRole("heading", { name: say("Your files, on your own server", "Vaši fajlovi, na vašem serveru") })
      .filter({ visible: true }),
  ).toBeVisible();
}

/** The admin "ana" through the API, for putting files in place before a scenario. */
export async function asAna(request: APIRequestContext) {
  const login = await request.post(`${SERVER}/api/v1/auth/login`, {
    data: {
      name: "ana",
      password: PASSWORD,
      device: { name: "Preview seed", kind: "other", platform: null, appVersion: null },
    },
  });
  expect(login.ok(), await login.text()).toBe(true);
  const tokens = ((await login.json()) as { tokens: { accessToken: string; user: { id: string } } }).tokens;
  const headers = { Authorization: `Bearer ${tokens.accessToken}` };

  const libraries = (await (await request.get(`${SERVER}/api/v1/libraries`, { headers })).json()) as {
    kind: string;
    rootNodeId: string;
  }[];
  const root = libraries.find((l) => l.kind === "personal")!.rootNodeId;

  const children = async (parentId: string): Promise<{ id: string; name: string }[]> =>
    (
      (await (await request.get(`${SERVER}/api/v1/nodes/${parentId}/children`, { headers })).json()) as {
        items: { id: string; name: string }[];
      }
    ).items;

  const folder = async (parentId: string, name: string): Promise<string> => {
    const response = await request.post(`${SERVER}/api/v1/nodes`, { headers, data: { parentId, name } });
    expect(response.ok(), await response.text()).toBe(true);
    return ((await response.json()) as { id: string }).id;
  };

  // The upload protocol by hand (tus): create, then send the content in the server's chunk size.
  // A picture without a recorded time is dated by its file time, which `modifiedAt` sets.
  const upload = async (parentId: string, name: string, content: Buffer, modifiedAt?: Date): Promise<void> => {
    const tus = { ...headers, "Tus-Resumable": "1.0.0" };
    const metadata = Object.entries({
      parentId,
      name,
      onConflict: "fail",
      ...(modifiedAt ? { modifiedAt: String(modifiedAt.getTime()) } : {}),
    })
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

  return { headers, userId: tokens.user.id, root, children, folder, upload };
}
