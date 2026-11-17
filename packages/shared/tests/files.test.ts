import { describe, expect, it } from "vitest";

import { ApiError } from "../src/api/call.ts";
import { createApiClient } from "../src/api/client.ts";
import {
  batchFailure,
  defaultFileOrder,
  fileOrderKey,
  fileOrderLabelKey,
  fileSorts,
  filesApi,
  homeLibrary,
  libraryRootName,
  parseFileOrder,
  trashLocation,
  trashTotals,
  type LibraryInfo,
} from "../src/files/api.ts";
import { daysUntil, formatBytes, formatDateTime, formatDay } from "../src/files/format.ts";
import { TEXT_PREVIEW_LIMIT, appViewKind, fileCategory, fileExtension, previewKind } from "../src/files/kind.ts";
import { messages } from "../src/i18n/index.ts";

const file = (name: string, mediaType: string | null = null, size = 100) => ({ name, mediaType, size });

describe("what a file is", () => {
  it("reads the ending of a name", () => {
    expect(fileExtension("Report.final.PDF")).toBe("pdf");
    expect(fileExtension(".gitignore")).toBe("gitignore");
    expect(fileExtension("Makefile")).toBe("makefile");
    expect(fileExtension("archive.tar.gz")).toBe("gz");
  });

  it.each([
    ["holiday.jpg", "image/jpeg", "image"],
    ["raw.cr2", "image/x-canon-cr2", "image"],
    ["clip.mkv", "video/x-matroska", "video"],
    ["song.opus", "audio/opus", "audio"],
    ["scan.pdf", null, "pdf"],
    ["scan", "application/pdf", "pdf"],
    ["budget.xlsx", "application/octet-stream", "document"],
    ["letter.odt", null, "document"],
    ["backup.tar.gz", null, "archive"],
    ["photos.zip", "application/zip", "archive"],
    ["notes.md", null, "text"],
    ["data.bin", "application/json", "text"],
    ["page", "text/plain", "text"],
    ["program.exe", "application/octet-stream", "other"],
    ["no ending", null, "other"],
  ] as const)("%s (%s) is %s", (name, type, expected) => {
    expect(fileCategory(file(name, type))).toBe(expected);
  });

  it.each([
    ["holiday.jpg", "image/jpeg", "image"],
    // A browser cannot draw a camera's raw picture: it is offered, not shown.
    ["raw.cr2", "image/x-canon-cr2", "none"],
    ["clip.mp4", "video/mp4", "video"],
    ["clip.mkv", "video/x-matroska", "none"],
    ["song.mp3", "audio/mpeg", "audio"],
    ["scan.pdf", null, "pdf"],
    ["budget.xlsx", null, "office"],
    ["notes.md", null, "text"],
    ["program.exe", null, "none"],
  ] as const)("a browser shows %s (%s) as %s", (name, type, expected) => {
    expect(previewKind(file(name, type))).toBe(expected);
  });

  it.each([
    ["holiday.jpg", "image/jpeg", "image"],
    // What a browser cannot play, the phone's player tries.
    ["clip.mkv", "video/x-matroska", "video"],
    ["clip.mp4", "video/mp4", "video"],
    ["song.opus", "audio/opus", "audio"],
    ["scan.pdf", null, "pdf"],
    ["notes.md", null, "text"],
    // Pictures every phone cannot draw, and Office files, open in another app.
    ["raw.cr2", "image/x-canon-cr2", "other"],
    ["budget.xlsx", null, "other"],
    ["letter.rtf", "text/rtf", "other"],
    ["program.exe", null, "other"],
  ] as const)("an app shows %s (%s) as %s", (name, type, expected) => {
    expect(appViewKind(file(name, type))).toBe(expected);
  });

  it("hands text that is too large to another app", () => {
    expect(appViewKind(file("server.log", "text/plain", TEXT_PREVIEW_LIMIT + 1))).toBe("other");
  });

  it("does not show text that is too large", () => {
    expect(previewKind(file("server.log", "text/plain", TEXT_PREVIEW_LIMIT))).toBe("text");
    expect(previewKind(file("server.log", "text/plain", TEXT_PREVIEW_LIMIT + 1))).toBe("none");
    // The size arrives as text when it is beyond what a number holds exactly.
    expect(previewKind({ name: "a.txt", mediaType: null, size: "12" })).toBe("text");
  });
});

describe("order of a folder", () => {
  it("survives being written down and read again", () => {
    for (const sort of fileSorts) {
      for (const descending of [false, true]) {
        expect(parseFileOrder(fileOrderKey({ sort, descending }))).toEqual({ sort, descending });
      }
    }
  });

  it.each([null, undefined, "", "name", "name:up", "colour:asc", "size:desc:extra"])(
    "falls back to the default for %j",
    (key) => {
      expect(parseFileOrder(key)).toEqual(
        key === "size:desc:extra" ? { sort: "size", descending: true } : defaultFileOrder,
      );
    },
  );

  it("has a name in every language", () => {
    for (const sort of fileSorts) {
      for (const descending of [false, true]) {
        const key = fileOrderLabelKey({ sort, descending }).split(".");
        for (const tree of Object.values(messages)) {
          const text = key.reduce<unknown>((at, part) => (at as Record<string, unknown> | undefined)?.[part], tree);
          expect(typeof text, key.join(".")).toBe("string");
        }
      }
    }
  });
});

describe("where files start", () => {
  const library = (id: string, kind: LibraryInfo["kind"]): LibraryInfo => ({
    id,
    name: id,
    kind,
    mode: kind === "personal" ? "managed" : "external",
    rootNodeId: `root-${id}`,
    readOnly: false,
    state: "online",
    changesAllowed: true,
    firstReading: false,
  });

  it("is the person's own library, wherever it is in the list", () => {
    expect(homeLibrary([library("a", "folder"), library("b", "personal")])?.id).toBe("b");
  });

  it("is the first library of someone who has none of their own", () => {
    expect(homeLibrary([library("a", "folder"), library("b", "photos")])?.id).toBe("a");
    expect(homeLibrary([])).toBeUndefined();
  });
});

describe("sizes and dates", () => {
  it("writes sizes in the language's own way", () => {
    expect(formatBytes(0, "en")).toBe("0 byte");
    expect(formatBytes(1023, "en")).toBe("1,023 byte");
    expect(formatBytes(1536, "en")).toBe("1.5 kB");
    expect(formatBytes(1536, "sr-Latn")).toBe("1,5 kB");
    expect(formatBytes(150 * 1024 * 1024, "en")).toBe("150 MB");
    expect(formatBytes(5 * 1024 ** 4, "en")).toBe("5 TB");
    expect(formatBytes(5000 * 1024 ** 4, "en")).toBe("5,000 TB");
    expect(formatBytes(-5, "en")).toBe("0 byte");
  });

  it("writes dates in the language's own way", () => {
    const moment = "2026-11-10T14:32:00";
    expect(formatDay(moment, "en-GB")).toBe("10 Nov 2026");
    expect(formatDateTime(moment, "en-GB")).toContain("14:32");
    expect(formatDay(moment, "sr-Latn")).toContain("2026");
  });

  it("counts whole days until a date, never below zero", () => {
    const now = Date.parse("2026-11-10T12:00:00Z");
    expect(daysUntil("2026-11-10T12:00:01Z", now)).toBe(1);
    expect(daysUntil("2026-11-13T12:00:00Z", now)).toBe(3);
    expect(daysUntil("2026-11-01T00:00:00Z", now)).toBe(0);
  });
});

describe("file calls", () => {
  interface Seen {
    readonly method: string;
    readonly path: string;
    readonly query: Record<string, string>;
    readonly body: unknown;
    readonly ifMatch?: string;
  }

  function server(answer: (seen: Seen) => Response) {
    const requests: Seen[] = [];
    const fetch: typeof globalThis.fetch = async (input, init) => {
      const request = new Request(input, init);
      const url = new URL(request.url);
      const text = await request.text();
      const query: Record<string, string> = {};
      url.searchParams.forEach((value, key) => {
        query[key] = value;
      });
      const seen: Seen = {
        method: request.method,
        path: url.pathname,
        query,
        body: text ? JSON.parse(text) : null,
        ...(request.headers.has("If-Match") ? { ifMatch: request.headers.get("If-Match")! } : {}),
      };
      requests.push(seen);
      return answer(seen);
    };
    const api = createApiClient({
      baseUrl: "http://atrium.test",
      fetch,
      getAccessToken: () => "token",
      refresh: () => Promise.resolve(null),
    });
    return { files: filesApi(api), requests };
  }

  const json = (status: number, body: unknown): Response =>
    new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

  it("asks for a folder's contents in the chosen order, page by page", async () => {
    const { files, requests } = server(() => json(200, { items: [], nextCursor: null, total: 0 }));

    await files.children("folder-1", { sort: "modified", descending: true, limit: 200 });
    await files.children("folder-1", { sort: "name", descending: false, cursor: "next", kind: "folder" });

    expect(requests).toEqual([
      {
        method: "GET",
        path: "/api/v1/nodes/folder-1/children",
        query: { sort: "modified", descending: "true", limit: "200" },
        body: null,
      },
      {
        method: "GET",
        path: "/api/v1/nodes/folder-1/children",
        query: { sort: "name", descending: "false", kind: "folder", cursor: "next" },
        body: null,
      },
    ]);
  });

  it("sends several items in one request and returns each item's outcome", async () => {
    const items = [
      { id: "a", ok: true, code: null, params: null, node: null },
      { id: "b", ok: false, code: "nodes.name_taken", params: null, node: null },
    ];
    const { files, requests } = server(() => json(200, { items }));

    expect(await files.batch("move", ["a", "b"], "target")).toEqual(items);
    await files.batch("trash", ["a"]);

    expect(requests.map((r) => r.body)).toEqual([
      { operation: "move", ids: ["a", "b"], parentId: "target" },
      { operation: "trash", ids: ["a"], parentId: null },
    ]);
  });

  it("restores to where the item was, under another name if needed", async () => {
    const { files, requests } = server(() => json(200, {}));

    await files.restore("a");

    expect(requests).toEqual([
      { method: "POST", path: "/api/v1/trash/a/restore", query: {}, body: { parentId: null, onConflict: "rename" } },
    ]);
  });

  it("asks for a signed address to show or to save", async () => {
    const { files, requests } = server(() => json(200, { url: "/signed", expiresAt: "2026-11-10T00:00:00Z" }));

    expect((await files.contentUrl("a", { inline: true })).url).toBe("/signed");
    await files.contentUrl("a", { inline: false, versionId: "v1" });
    await files.archiveUrl(["a", "b"]);

    expect(requests.map((r) => [r.path, r.body])).toEqual([
      ["/api/v1/nodes/a/content-url", { inline: true, versionId: null }],
      ["/api/v1/nodes/a/content-url", { inline: false, versionId: "v1" }],
      ["/api/v1/downloads/archive", { ids: ["a", "b"] }],
    ]);
  });

  it("creates a folder", async () => {
    const { files, requests } = server(() => json(201, { id: "new" }));

    await files.createFolder("parent", "Invoices");

    expect(requests[0]).toMatchObject({
      method: "POST",
      path: "/api/v1/nodes",
      body: { parentId: "parent", name: "Invoices" },
    });
  });

  it("renames with the version the person was looking at", async () => {
    const { files, requests } = server(() => json(200, { id: "a" }));

    await files.rename({ id: "a", version: 7 }, "Invoices 2026");

    expect(requests[0]).toMatchObject({
      method: "PATCH",
      path: "/api/v1/nodes/a",
      body: { name: "Invoices 2026", parentId: null },
      ifMatch: '"7"',
    });
  });

  it("restores into another folder when asked", async () => {
    const { files, requests } = server(() => json(200, {}));

    await files.restore("a", "folder-2");

    expect(requests[0]?.body).toEqual({ parentId: "folder-2", onConflict: "rename" });
  });

  it("removes one item of the trash, or all of it, for good", async () => {
    const { files, requests } = server(() => new Response(null, { status: 204 }));

    await files.deleteForever("a");
    await files.emptyTrash();

    expect(requests.map((r) => [r.method, r.path])).toEqual([
      ["DELETE", "/api/v1/trash/a"],
      ["DELETE", "/api/v1/trash"],
    ]);
  });

  it("throws the server's error code", async () => {
    const { files } = server(
      () =>
        new Response(JSON.stringify({ code: "nodes.not_found", status: 404 }), {
          status: 404,
          headers: { "Content-Type": "application/problem+json" },
        }),
    );

    await expect(files.node("gone")).rejects.toBeInstanceOf(ApiError);
    await expect(files.node("gone")).rejects.toMatchObject({ problem: { code: "nodes.not_found" } });
  });
});

describe("outcomes of several items at once", () => {
  const item = (id: string, ok: boolean, code: string | null = null) => ({ id, ok, code, params: null, node: null });

  it("is no error when every item went through", () => {
    expect(batchFailure([item("a", true), item("b", true)])).toBeNull();
    expect(batchFailure([])).toBeNull();
  });

  it("is the first item's error otherwise", () => {
    const failure = batchFailure([
      item("a", true),
      item("b", false, "node.name_taken"),
      item("c", false, "node.read_only"),
    ]);
    expect(failure?.problem).toEqual({ code: "node.name_taken", params: {}, status: 0 });
    expect(batchFailure([item("a", false)])?.problem.code).toBe("unknown");
  });

  it("adds up what the trash holds, folders counted with their contents", () => {
    const entry = (itemCount: number | string, bytes: number | string) =>
      ({ itemCount, bytes }) as unknown as Parameters<typeof trashTotals>[0][number];
    expect(trashTotals([entry(1, 10), entry("41", "5000")])).toEqual({ count: 42, bytes: 5010 });
    expect(trashTotals([])).toEqual({ count: 0, bytes: 0 });
  });
});

describe("where a trashed item was", () => {
  const libraries: LibraryInfo[] = [
    {
      id: "own",
      name: "Personal",
      kind: "personal",
      mode: "managed",
      rootNodeId: "own-root",
      readOnly: false,
      state: "online",
      changesAllowed: true,
      firstReading: false,
    },
    {
      id: "ext",
      name: "Family archive",
      kind: "folder",
      mode: "external",
      rootNodeId: "ext-root",
      readOnly: false,
      state: "online",
      changesAllowed: true,
      firstReading: false,
    },
  ];
  const item = (libraryId: string, location: [string, string][]) =>
    ({
      node: { libraryId },
      location: location.map(([id, name]) => ({ id, name, inTrash: false })),
    }) as unknown as Parameters<typeof trashLocation>[0];

  it("calls the person's own library by the words of their language", () => {
    expect(
      trashLocation(
        item("own", [
          ["own-root", "Personal"],
          ["f1", "Taxes"],
        ]),
        libraries,
        "Moji fajlovi",
      ),
    ).toEqual(["Moji fajlovi", "Taxes"]);
  });

  it("calls any other library by its name, never by the person's own", () => {
    expect(
      trashLocation(
        item("ext", [
          ["ext-root", "archive"],
          ["f2", "Scans"],
        ]),
        libraries,
        "My files",
      ),
    ).toEqual(["Family archive", "Scans"]);
  });

  it("keeps the stored names when the library is no longer known", () => {
    expect(trashLocation(item("gone", [["r", "root"]]), libraries, "My files")).toEqual(["root"]);
    expect(libraryRootName(libraries[1]!, "My files")).toBe("Family archive");
  });
});
