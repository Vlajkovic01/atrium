import { describe, expect, it } from "vitest";

import { nextRange, offlineTotals, planOffline, type KeptFile } from "../src/offline/plan.ts";

function node(id: string, version = 1, kind: "file" | "folder" = "file") {
  return {
    id,
    libraryId: "l1",
    parentId: "p",
    name: id,
    kind,
    size: 100,
    mediaType: null,
    modifiedAt: "2026-11-17T10:00:00Z",
    createdAt: "2026-11-17T10:00:00Z",
    updatedAt: "2026-11-17T10:00:00Z",
    trashedAt: null,
    version,
    canWrite: true,
  };
}

const kept = (nodeId: string, version: number, bytes = 100, size = 100): KeptFile => ({ nodeId, version, bytes, size });

describe("keeping an offline item as it is on the server", () => {
  it("fetches files not kept yet, and nothing for folders", () => {
    expect(planOffline([node("a"), node("sub", 1, "folder")], []).fetch.map((n) => n.id)).toEqual(["a"]);
  });

  it("keeps a copy of the same version, even one half fetched", () => {
    expect(planOffline([node("a", 2)], [kept("a", 2, 40)])).toEqual({ fetch: [], drop: [] });
  });

  it("fetches a new version of a kept file", () => {
    expect(planOffline([node("a", 3)], [kept("a", 2)]).fetch.map((n) => n.id)).toEqual(["a"]);
  });

  it("drops what is no longer listed: removed, moved away or no longer readable", () => {
    expect(planOffline([node("a")], [kept("a", 1), kept("b", 1)])).toEqual({ fetch: [], drop: ["b"] });
    expect(planOffline([], [kept("a", 1)]).drop).toEqual(["a"]);
  });

  it("reads the version whether the server sends it as a number or as text", () => {
    expect(planOffline([{ ...node("a"), version: "2" }], [kept("a", 2)]).fetch).toEqual([]);
  });
});

describe("fetching in parts", () => {
  it("asks for the next part, the last one shorter", () => {
    expect(nextRange(0, 10, 4)).toEqual({ start: 0, end: 3 });
    expect(nextRange(8, 10, 4)).toEqual({ start: 8, end: 9 });
    expect(nextRange(10, 10, 4)).toBeNull();
  });

  it("has nothing to fetch for an empty file", () => {
    expect(nextRange(0, 0, 4)).toBeNull();
  });

  it("adds up progress and space", () => {
    expect(offlineTotals([kept("a", 1, 100, 100), kept("b", 1, 30, 200), kept("c", 1, 0, 0)])).toEqual({
      bytes: 130,
      size: 300,
      whole: 2,
      files: 3,
    });
  });
});
