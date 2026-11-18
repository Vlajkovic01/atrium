import { describe, expect, it } from "vitest";

import {
  backupCounts,
  backupFolderNames,
  isChosen,
  planBackup,
  type BackupChoice,
  type BackupRecord,
  type DeviceAsset,
} from "../src/backup/plan.ts";

const asset = (
  id: string,
  kind: "photo" | "video" = "photo",
  albums = ["Camera"],
  size = 100,
  modifiedAt = 1,
): DeviceAsset => ({
  id,
  name: `${id}.jpg`,
  kind,
  size,
  modifiedAt,
  albums,
});

const record = (assetId: string, extra: Partial<BackupRecord> = {}): BackupRecord => ({
  assetId,
  size: 100,
  modifiedAt: 1,
  sha256: "aa",
  done: false,
  error: null,
  ...extra,
});

const all: BackupChoice = { photos: true, videos: true, albums: null };

describe("what the person chose", () => {
  it("takes photos, videos, or both", () => {
    expect(isChosen(asset("a", "video"), { ...all, videos: false })).toBe(false);
    expect(isChosen(asset("a", "photo"), { ...all, photos: false })).toBe(false);
    expect(isChosen(asset("a", "video"), all)).toBe(true);
  });

  it("takes only the chosen device albums, or all of them", () => {
    const choice = { ...all, albums: ["Camera"] };
    expect(isChosen(asset("a", "photo", ["Camera"]), choice)).toBe(true);
    expect(isChosen(asset("b", "photo", ["Screenshots"]), choice)).toBe(false);
    expect(isChosen(asset("c", "photo", ["Screenshots", "Camera"]), choice)).toBe(true);
  });
});

describe("planning a backup run", () => {
  it("hashes what is new, sends what is hashed and not yet on the server, and leaves what is done", () => {
    const plan = planBackup(
      [asset("new"), asset("hashed"), asset("done"), asset("failed")],
      [record("hashed"), record("done", { done: true }), record("failed", { error: "upload.too_large" })],
      all,
    );

    expect(plan.toHash.map((a) => a.id)).toEqual(["new"]);
    expect(plan.toSend.map((a) => a.id)).toEqual(["hashed"]);
  });

  it("treats a photo edited since as new content", () => {
    const plan = planBackup([asset("a", "photo", ["Camera"], 120, 2)], [record("a", { done: true })], all);
    expect(plan.toHash.map((a) => a.id)).toEqual(["a"]);
  });

  it("tries a failed one again once it changes", () => {
    const plan = planBackup([asset("a", "photo", ["Camera"], 100, 9)], [record("a", { error: "x" })], all);
    expect(plan.toHash.map((a) => a.id)).toEqual(["a"]);
  });

  it("leaves out what was not chosen", () => {
    const plan = planBackup([asset("v", "video"), asset("p")], [], { ...all, videos: false });
    expect(plan.toHash.map((a) => a.id)).toEqual(["p"]);
  });
});

describe("the numbers shown", () => {
  it("counts what is on the server, what waits and what failed, with the bytes still to send", () => {
    expect(
      backupCounts(
        [asset("a"), asset("b", "photo", ["Camera"], 300), asset("c"), asset("d", "video")],
        [record("a", { done: true }), record("c", { error: "x" })],
        { ...all, videos: false },
      ),
    ).toEqual({ chosen: 3, done: 1, waiting: 1, failed: 1, bytesWaiting: 300 });
  });
});

describe("where a backup goes", () => {
  it("is a folder named after the device, cleaned of what names cannot hold", () => {
    expect(backupFolderNames("Pixel 9 / Ana", "Camera backup")).toEqual(["Camera backup", "Pixel 9 Ana"]);
    expect(backupFolderNames("  ", "Camera backup")).toEqual(["Camera backup", "Phone"]);
  });
});
