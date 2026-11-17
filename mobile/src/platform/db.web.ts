import type { LocalDb } from "./db.ts";

// Only for looking at the app's screens in a browser while developing
// (mobile/preview): kept in memory, gone with the page.

export type { LocalDb } from "./db.ts";

const cursors = new Map<string, string>();

export const localDb: LocalDb = {
  cursor: (serverId) => Promise.resolve(cursors.get(serverId) ?? null),
  setCursor: (serverId, cursor) => {
    cursors.set(serverId, cursor);
    return Promise.resolve();
  },
  forgetServer: (serverId) => {
    cursors.delete(serverId);
    return Promise.resolve();
  },
};
