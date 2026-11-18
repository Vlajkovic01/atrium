import { describe, expect, it } from "vitest";

import { notificationFolder, notificationText, uploadReports, type Notification } from "../src/notifications.ts";
import type { QueuedUpload } from "../src/upload/queue.ts";

const FOLDER = "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";
const OTHER = "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c";

function upload(id: number, state: QueuedUpload["status"]["state"], parentId = FOLDER): QueuedUpload {
  const status =
    state === "completed"
      ? { state, nodeId: `n${id}` }
      : state === "failed"
        ? { state, code: "upload.too_large", params: {} }
        : state === "uploading" || state === "paused"
          ? { state, offset: 0 }
          : { state };
  return { id, name: `f${id}`, size: 10, lastModified: undefined, parentId, status, location: null } as QueuedUpload;
}

describe("telling the server about uploads", () => {
  it("waits until the whole batch is over", () => {
    expect(uploadReports([upload(1, "completed"), upload(2, "uploading")], new Set())).toEqual({
      reports: [],
      ids: [],
    });
    expect(uploadReports([upload(1, "completed"), upload(2, "paused")], new Set())).toEqual({ reports: [], ids: [] });
  });

  it("says how many arrived, leading to their folder when they share one", () => {
    expect(uploadReports([upload(1, "completed"), upload(2, "completed")], new Set())).toEqual({
      reports: [{ kind: "uploads.finished", values: { count: "2" }, link: `/files/${FOLDER}` }],
      ids: [1, 2],
    });
    expect(
      uploadReports([upload(1, "completed"), upload(2, "completed", OTHER)], new Set()).reports[0]?.link,
    ).toBeNull();
  });

  it("says how many failed, apart from those that arrived, and nothing about cancelled or skipped ones", () => {
    expect(
      uploadReports(
        [upload(1, "completed"), upload(2, "failed"), upload(3, "cancelled"), upload(4, "skipped")],
        new Set(),
      ).reports,
    ).toEqual([
      { kind: "uploads.finished", values: { count: "1" }, link: `/files/${FOLDER}` },
      { kind: "uploads.failed", values: { count: "1" }, link: null },
    ]);
  });

  it("tells about each upload once", () => {
    expect(uploadReports([upload(1, "completed"), upload(2, "completed")], new Set([1, 2]))).toEqual({
      reports: [],
      ids: [],
    });
    expect(uploadReports([upload(1, "completed"), upload(2, "completed")], new Set([1])).reports[0]?.values).toEqual({
      count: "1",
    });
  });
});

describe("notifications", () => {
  const t = (key: string, options: Record<string, unknown>) => `${String(key)}|${JSON.stringify(options ?? {})}`;
  const notification = (kind: string, params: Record<string, string>, link: string | null = null): Notification => ({
    id: "1",
    kind,
    params,
    createdAt: "2026-11-17T10:00:00Z",
    readAt: null,
    link,
  });

  it("words a notification by its kind, with its values", () => {
    expect(notificationText(t, notification("uploads.finished", { count: "3" }))).toContain(
      'notifications.kinds.uploads_finished|{"count":3,"name":"","work":""',
    );
    expect(notificationText(t, notification("library.offline", { name: "Archive" }))).toContain('"name":"Archive"');
  });

  it("leads to a folder only for a link the app knows", () => {
    expect(notificationFolder(`/files/${FOLDER}`)).toBe(FOLDER);
    expect(notificationFolder("/admin/background-work")).toBeNull();
    expect(notificationFolder(`/files/${FOLDER}/../x`)).toBeNull();
    expect(notificationFolder(null)).toBeNull();
  });
});
