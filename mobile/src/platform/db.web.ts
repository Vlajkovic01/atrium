import type { LocalDb, OfflineFile, OfflineRoot } from "./db.ts";

// Only for looking at the app's screens in a browser while developing
// (mobile/preview): kept in memory, gone with the page.

export type { LocalDb, OfflineFile, OfflineRoot } from "./db.ts";

const cursors = new Map<string, string>();
const roots = new Map<string, OfflineRoot[]>();
const files = new Map<string, OfflineFile[]>();

export const localDb: LocalDb = {
  cursor: (serverId) => Promise.resolve(cursors.get(serverId) ?? null),
  setCursor: (serverId, cursor) => {
    cursors.set(serverId, cursor);
    return Promise.resolve();
  },
  roots: (serverId) => Promise.resolve([...(roots.get(serverId) ?? [])].sort((a, b) => b.addedAt - a.addedAt)),
  addRoot: (serverId, root) => {
    roots.set(serverId, [...(roots.get(serverId) ?? []).filter((r) => r.nodeId !== root.nodeId), root]);
    return Promise.resolve();
  },
  removeRoot: (serverId, nodeId) => {
    roots.set(
      serverId,
      (roots.get(serverId) ?? []).filter((r) => r.nodeId !== nodeId),
    );
    files.set(
      serverId,
      (files.get(serverId) ?? []).filter((f) => f.rootId !== nodeId),
    );
    return Promise.resolve();
  },
  files: (serverId, rootId) =>
    Promise.resolve(
      (files.get(serverId) ?? [])
        .filter((f) => !rootId || f.rootId === rootId)
        .sort((a, b) => a.name.localeCompare(b.name)),
    ),
  putFile: (serverId, file) => {
    files.set(serverId, [
      ...(files.get(serverId) ?? []).filter((f) => !(f.rootId === file.rootId && f.nodeId === file.nodeId)),
      file,
    ]);
    return Promise.resolve();
  },
  removeFile: (serverId, rootId, nodeId) => {
    files.set(
      serverId,
      (files.get(serverId) ?? []).filter((f) => !(f.rootId === rootId && f.nodeId === nodeId)),
    );
    return Promise.resolve();
  },
  forgetServer: (serverId) => {
    cursors.delete(serverId);
    roots.delete(serverId);
    files.delete(serverId);
    return Promise.resolve();
  },
};
