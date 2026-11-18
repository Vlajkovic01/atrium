import { describe, expect, it } from "vitest";

import { ApiError } from "../src/api/call.ts";
import { createApiClient } from "../src/api/client.ts";
import { changeEffects, pullChanges, type ChangeInfo } from "../src/sync/changes.ts";

const json = (status: number, body: unknown, type = "application/json"): Response =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": type } });

/** A stand-in feed: answers each request in turn, and records the cursors asked for. */
function feed(answers: Response[]) {
  const asked: (string | null)[] = [];
  const api = createApiClient({
    baseUrl: "http://atrium.test",
    fetch: async (input, init) => {
      asked.push(new URL(new Request(input, init).url).searchParams.get("cursor"));
      const next = answers.shift();
      if (!next) {
        throw new Error("no more answers");
      }

      return next;
    },
    getAccessToken: () => "token",
    refresh: () => Promise.resolve(null),
  });
  return { api, asked };
}

function node(id: string, parentId: string | null, kind: "file" | "folder" = "file", mediaType: string | null = null) {
  return {
    id,
    libraryId: "l1",
    parentId,
    name: id,
    kind,
    size: 1,
    mediaType,
    modifiedAt: "2026-11-17T10:00:00Z",
    createdAt: "2026-11-17T10:00:00Z",
    updatedAt: "2026-11-17T10:00:00Z",
    trashedAt: null,
    version: 1,
    canWrite: true,
  };
}

const change = (n: ReturnType<typeof node>, extra: Partial<ChangeInfo> = {}): ChangeInfo => ({
  nodeId: n.id,
  libraryId: "l1",
  removed: false,
  inTrash: false,
  node: n,
  ...extra,
});

describe("pulling the change feed", () => {
  it("starts from now on a first pull, and says everything must be read", async () => {
    const { api, asked } = feed([json(200, { items: [], cursor: "c1", hasMore: false })]);

    expect(await pullChanges(api, null)).toEqual({ items: [], cursor: "c1", reset: true });
    expect(asked).toEqual([null]);
  });

  it("reads page after page until it has caught up", async () => {
    const { api, asked } = feed([
      json(200, { items: [change(node("a", "f"))], cursor: "c2", hasMore: true }),
      json(200, { items: [change(node("b", "f"))], cursor: "c3", hasMore: false }),
    ]);

    const pull = await pullChanges(api, "c1");

    expect(pull.items.map((i) => i.nodeId)).toEqual(["a", "b"]);
    expect(pull).toMatchObject({ cursor: "c3", reset: false });
    expect(asked).toEqual(["c1", "c2"]);
  });

  it("counts an item named twice as it was last seen", async () => {
    const { api } = feed([
      json(200, { items: [change(node("a", "f")), change(node("b", "f"))], cursor: "c2", hasMore: true }),
      json(200, { items: [change(node("a", "f"), { inTrash: true })], cursor: "c3", hasMore: false }),
    ]);

    const pull = await pullChanges(api, "c1");

    expect(pull.items.map((i) => [i.nodeId, i.inTrash])).toEqual([
      ["b", false],
      ["a", true],
    ]);
  });

  it("starts over from now when the server no longer knows the cursor", async () => {
    const { api, asked } = feed([
      json(410, { code: "changes.cursor_expired", status: 410 }, "application/problem+json"),
      json(200, { items: [], cursor: "fresh", hasMore: false }),
    ]);

    expect(await pullChanges(api, "old")).toEqual({ items: [], cursor: "fresh", reset: true });
    expect(asked).toEqual(["old", null]);
  });

  it("keeps the cursor when the server cannot be reached, by throwing", async () => {
    const api = createApiClient({
      baseUrl: "http://atrium.test",
      fetch: () => Promise.reject(new TypeError("offline")),
      getAccessToken: () => "token",
      refresh: () => Promise.resolve(null),
    });

    await expect(pullChanges(api, "c1")).rejects.toBeInstanceOf(ApiError);
  });
});

describe("what a pull means for a client", () => {
  it("names the folders to read again: parents of what changed, and changed folders themselves", () => {
    const effects = changeEffects([change(node("a", "f1")), change(node("sub", "f1", "folder"))]);

    expect([...effects.folders].sort()).toEqual(["f1", "sub"]);
    expect([...effects.changed.keys()].sort()).toEqual(["a", "sub"]);
    expect(effects.gone.size).toBe(0);
  });

  it("counts removed, trashed and unreadable items as gone", () => {
    const effects = changeEffects([
      change(node("a", "f1"), { removed: true }),
      change(node("b", "f2"), { inTrash: true }),
      { nodeId: "c", libraryId: "l1", removed: true, inTrash: false, node: null },
    ]);

    expect([...effects.gone].sort()).toEqual(["a", "b", "c"]);
    expect([...effects.folders].sort()).toEqual(["f1", "f2"]);
    expect(effects.changed.size).toBe(0);
  });

  it("reads the timeline again only when photos or videos may have changed", () => {
    expect(changeEffects([change(node("doc", "f", "file", "application/pdf"))]).media).toBe(false);
    expect(changeEffects([change(node("pic", "f", "file", "image/jpeg"))]).media).toBe(true);
    expect(changeEffects([change(node("x", "f"), { removed: true })]).media).toBe(true);
  });
});
