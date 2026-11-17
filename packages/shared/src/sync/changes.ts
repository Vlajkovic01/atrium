import { ApiError, call } from "../api/call.ts";
import type { ApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";

export type ChangeInfo = components["schemas"]["ChangeInfo"];

/** What a pull of the change feed found. */
export interface ChangePull {
  /** Items to look at again, each once, as they are now (D-047). */
  readonly items: readonly ChangeInfo[];
  /** Where the next pull starts. Keep it only once `items` have been applied. */
  readonly cursor: string;
  /**
   * Nothing is known about what changed before `cursor`: the first pull
   * of a device, or a device away longer than the feed is kept. Everything
   * kept on the device must be read again.
   */
  readonly reset: boolean;
}

/** More than this many pages in one pull means something is wrong; the next pull goes on from here. */
const MAX_PAGES = 50;

/**
 * Reads the change feed from a cursor to now, page by page. Without a
 * cursor, or when the server no longer knows it (410
 * `changes.cursor_expired`), it starts from now and says so with `reset`.
 * Throws an `ApiError` for anything else, and leaves the cursor as it was.
 */
export async function pullChanges(api: ApiClient, cursor: string | null, limit = 500): Promise<ChangePull> {
  const page = (from: string | null) =>
    call(api.GET("/api/v1/changes", { params: { query: { limit, ...(from ? { cursor: from } : {}) } } }));

  if (cursor === null) {
    return { items: [], cursor: (await page(null)).cursor, reset: true };
  }

  const items: ChangeInfo[] = [];
  let at = cursor;
  try {
    for (let pages = 0; pages < MAX_PAGES; pages++) {
      const next = await page(at);
      items.push(...next.items);
      at = next.cursor;
      if (!next.hasMore) {
        break;
      }
    }
  } catch (e) {
    if (e instanceof ApiError && e.problem.code === "changes.cursor_expired") {
      return { items: [], cursor: (await page(null)).cursor, reset: true };
    }

    throw e;
  }

  return { items: latestOnly(items), cursor: at, reset: false };
}

/** An item named more than once in a pull counts as it was last seen. */
function latestOnly(items: readonly ChangeInfo[]): ChangeInfo[] {
  const byNode = new Map<string, ChangeInfo>();
  for (const item of items) {
    byNode.delete(item.nodeId);
    byNode.set(item.nodeId, item);
  }

  return [...byNode.values()];
}

/** What a client should look at again after a pull. */
export interface ChangeEffects {
  /** Folders whose contents changed: the parents of what changed, and folders that changed themselves. */
  readonly folders: ReadonlySet<string>;
  /** Items that are still there, as they are now. */
  readonly changed: ReadonlyMap<string, components["schemas"]["NodeInfo"]>;
  /** Items gone for this person: removed, in the trash, or no longer readable. With a folder, everything below it. */
  readonly gone: ReadonlySet<string>;
  /** Whether photos or videos may have come or gone, so the timeline is read again. */
  readonly media: boolean;
}

/** Sorts the items of a pull into what a client looks at again. */
export function changeEffects(items: readonly ChangeInfo[]): ChangeEffects {
  const folders = new Set<string>();
  const changed = new Map<string, components["schemas"]["NodeInfo"]>();
  const gone = new Set<string>();
  let media = false;
  for (const item of items) {
    const node = item.node;
    if (item.removed || item.inTrash || !node) {
      gone.add(item.nodeId);
      // Without the node, the folder it was in is not known: what was gone is found when the folder is read.
      if (node?.parentId) {
        folders.add(node.parentId);
      }

      media = true;
      continue;
    }

    changed.set(node.id, node);
    if (node.parentId) {
      folders.add(node.parentId);
    }

    if (node.kind === "folder") {
      folders.add(node.id);
    }

    const type = (node.mediaType ?? "").toLowerCase();
    if (type.startsWith("image/") || type.startsWith("video/")) {
      media = true;
    }
  }

  return { folders, changed, gone, media };
}
