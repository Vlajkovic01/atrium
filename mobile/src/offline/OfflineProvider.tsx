import { ApiError, nextRange, planOffline, type NodeInfo } from "@atrium/shared";
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { AppState } from "react-native";

import { useActiveServer } from "../api.ts";
import { absoluteUrl } from "../files/local";
import { localDb, type OfflineFile, type OfflineRoot } from "../platform/db";
import { onSync } from "../sync/events.ts";
import { copyPath, copySize, deleteCopy, deleteRootCopies, deleteServerCopies, writePart } from "./copies";

/** One request fetches this much of a file; a lost connection costs at most one part. */
const PART = 4 * 1024 * 1024;
/** Progress shown on the Downloads screen is read again at most this often. */
const SHOW_MS = 500;

interface OfflineValue {
  readonly roots: readonly OfflineRoot[];
  readonly files: readonly OfflineFile[];
  /** Whether something is being fetched now. */
  readonly busy: boolean;
  readonly isOffline: (nodeId: string) => boolean;
  /** The whole copy of a file, if one is kept for any offline item. */
  readonly copyOf: (nodeId: string) => OfflineFile | null;
  readonly add: (node: Pick<NodeInfo, "id" | "kind" | "name">) => Promise<void>;
  readonly remove: (nodeId: string) => Promise<void>;
}

const OfflineContext = createContext<OfflineValue | null>(null);

/** The offline copies, where they are kept: under the tabs. Elsewhere, null. */
export function useOfflineMaybe(): OfflineValue | null {
  return useContext(OfflineContext);
}

export function useOffline(): OfflineValue {
  const value = useContext(OfflineContext);
  if (!value) {
    throw new Error("useOffline must be used inside OfflineProvider");
  }

  return value;
}

/**
 * The person's offline items on the server in front (docs/UX.md, Mobile,
 * Offline; D-056). A file, or every file below a folder, is fetched to
 * the phone a part at a time with Range requests, so a lost connection
 * or a closed app costs at most one part: fetching goes on where it
 * stopped. What changes on the server reaches the copies through the
 * change feed: new versions are fetched again, new files below a folder
 * are added, and what went is removed from the phone too.
 */
export function OfflineProvider({ children }: { readonly children: ReactNode }) {
  const { server, files: api } = useActiveServer();
  const [roots, setRoots] = useState<readonly OfflineRoot[]>([]);
  const [files, setFiles] = useState<readonly OfflineFile[]>([]);
  const [busy, setBusy] = useState(false);
  const running = useRef(false);
  const again = useRef(false);
  const stopped = useRef(false);
  /** The folders below each folder item, from its last reading, to know which changes concern it. */
  const foldersOf = useRef(new Map<string, Set<string>>());
  const shownAt = useRef(0);

  const show = useCallback(
    async (force = false): Promise<void> => {
      if (!force && Date.now() - shownAt.current < SHOW_MS) {
        return;
      }

      shownAt.current = Date.now();
      setRoots(await localDb.roots(server.id));
      setFiles(await localDb.files(server.id));
    },
    [server.id],
  );

  /** Lists what the server holds under an item now: the file itself, or every file below the folder. */
  const list = useCallback(
    async (root: OfflineRoot): Promise<NodeInfo[] | null> => {
      try {
        const top = await api.node(root.nodeId);
        if (top.trashedAt) {
          return null;
        }

        if (top.kind === "file") {
          return [top];
        }

        const out: NodeInfo[] = [];
        const folders = new Set<string>([top.id]);
        const queue = [top.id];
        while (queue.length > 0) {
          const folderId = queue.shift()!;
          let cursor: string | null = null;
          do {
            const page = await api.children(folderId, { sort: "name", descending: false, limit: 500, cursor });
            for (const item of page.items) {
              if (item.kind === "folder") {
                folders.add(item.id);
                queue.push(item.id);
              } else {
                out.push(item);
              }
            }

            cursor = page.nextCursor;
          } while (cursor);
        }

        foldersOf.current.set(root.nodeId, folders);
        return out;
      } catch (e) {
        // Gone, or no longer readable for this person: the item goes from the phone too.
        if (e instanceof ApiError && (e.problem.status === 404 || e.problem.status === 403)) {
          return null;
        }

        throw e;
      }
    },
    [api],
  );

  /** Brings one item's copies in line with the server: what to fetch, what to drop. */
  const reconcile = useCallback(
    async (root: OfflineRoot): Promise<void> => {
      const listed = await list(root);
      if (listed === null) {
        deleteRootCopies(server.id, root.nodeId);
        await localDb.removeRoot(server.id, root.nodeId);
        return;
      }

      const kept = await localDb.files(server.id, root.nodeId);
      const plan = planOffline(
        listed,
        kept.map((k) => ({ nodeId: k.nodeId, version: k.version, bytes: k.bytes, size: k.size })),
      );
      for (const nodeId of plan.drop) {
        deleteCopy(kept.find((k) => k.nodeId === nodeId)?.path ?? null);
        await localDb.removeFile(server.id, root.nodeId, nodeId);
      }

      for (const node of plan.fetch) {
        // A new version replaces the old copy; the server keeps the file either way.
        deleteCopy(kept.find((k) => k.nodeId === node.id)?.path ?? null);
        await localDb.putFile(server.id, {
          rootId: root.nodeId,
          nodeId: node.id,
          parentId: node.parentId,
          name: node.name,
          mediaType: node.mediaType,
          version: Number(node.version),
          size: Number(node.size),
          bytes: 0,
          path: null,
          error: null,
        });
      }
    },
    [list, server.id],
  );

  /** Fetches one file from where it stopped, a part at a time. */
  const fetchFile = useCallback(
    async (file: OfflineFile): Promise<void> => {
      const path = file.path ?? copyPath(server.id, file.rootId, file.nodeId, file.version, file.name);
      // What is really on the phone counts, not what was last written down.
      let bytes = Math.min(copySize(file.path ? path : null), file.size);
      let address = absoluteUrl(server.url, (await api.contentUrl(file.nodeId, { inline: false })).url);
      let range = nextRange(bytes, file.size, PART);
      if (!range && file.size === 0) {
        writePart(path, 0, new Uint8Array(0));
      }

      while (range && !stopped.current) {
        let response = await fetch(address, { headers: { Range: `bytes=${range.start}-${range.end}` } });
        if (response.status === 401 || response.status === 403) {
          // The signed address ran out: ask for a new one once.
          address = absoluteUrl(server.url, (await api.contentUrl(file.nodeId, { inline: false })).url);
          response = await fetch(address, { headers: { Range: `bytes=${range.start}-${range.end}` } });
        }

        if (response.status !== 206 && !(response.status === 200 && range.start === 0)) {
          throw new ApiError({
            code: response.status === 404 ? "resource.not_found" : "network",
            params: {},
            status: response.status,
          });
        }

        const part = new Uint8Array(await response.arrayBuffer());
        // A server that ignores Range sends the whole file from the start; only what was asked is kept.
        const wanted = range.end - range.start + 1;
        writePart(path, range.start, part.subarray(0, wanted));
        bytes = range.start + Math.min(part.length, wanted);
        await localDb.putFile(server.id, { ...file, path, bytes, error: null });
        void show();
        range = nextRange(bytes, file.size, PART);
      }
    },
    [api, server.id, server.url, show],
  );

  /** One pass: every item checked against the server, then every unfinished file fetched. */
  const run = useCallback(
    async (check: readonly string[] | "all" | "none"): Promise<void> => {
      if (running.current) {
        again.current = true;
        return;
      }

      running.current = true;
      setBusy(true);
      try {
        const all = await localDb.roots(server.id);
        for (const root of all) {
          if (check === "all" || (check !== "none" && check.includes(root.nodeId))) {
            await reconcile(root);
          }
        }

        await show(true);
        for (const file of await localDb.files(server.id)) {
          if (stopped.current) {
            break;
          }

          if (file.bytes < file.size || file.path === null) {
            try {
              await fetchFile(file);
            } catch (e) {
              const code = e instanceof ApiError ? e.problem.code : "network";
              // Not reachable: tried again on the next pass. Anything else is shown on the file.
              if (code !== "network") {
                await localDb.putFile(server.id, { ...file, error: code });
              } else {
                break;
              }
            }
          }
        }
      } catch {
        // Not reachable: the next pass tries again.
      } finally {
        running.current = false;
        setBusy(false);
        await show(true);
        if (again.current && !stopped.current) {
          again.current = false;
          void run("all");
        }
      }
    },
    [fetchFile, reconcile, server.id, show],
  );

  // On opening: check every item (the phone may have been away), then fetch what is missing.
  useEffect(() => {
    stopped.current = false;
    void show(true).then(() => run("all"));
    const listener = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        void run("none");
      }
    });
    return () => {
      stopped.current = true;
      listener.remove();
    };
  }, [run, show]);

  // What the change feed found: the items it concerns are checked again.
  useEffect(
    () =>
      onSync((event) => {
        if (event.serverId !== server.id) {
          return;
        }

        if (event.kind === "reset") {
          void run("all");
          return;
        }

        const { effects } = event;
        const touched = new Set([...effects.folders, ...effects.changed.keys(), ...effects.gone]);
        void localDb.roots(server.id).then(async (all) => {
          const kept = await localDb.files(server.id);
          const concerned = all
            .filter(
              (root) =>
                touched.has(root.nodeId) ||
                [...(foldersOf.current.get(root.nodeId) ?? [])].some((id) => touched.has(id)) ||
                kept.some((k) => k.rootId === root.nodeId && touched.has(k.nodeId)),
            )
            .map((root) => root.nodeId);
          if (concerned.length > 0) {
            void run(concerned);
          }
        });
      }),
    [run, server.id],
  );

  const value: OfflineValue = {
    roots,
    files,
    busy,
    isOffline: (nodeId) => roots.some((r) => r.nodeId === nodeId),
    copyOf: (nodeId) => files.find((f) => f.nodeId === nodeId && f.path !== null && f.bytes >= f.size) ?? null,
    add: async (node) => {
      await localDb.addRoot(server.id, { nodeId: node.id, kind: node.kind, name: node.name, addedAt: Date.now() });
      await show(true);
      void run([node.id]);
    },
    remove: async (nodeId) => {
      deleteRootCopies(server.id, nodeId);
      await localDb.removeRoot(server.id, nodeId);
      await show(true);
    },
  };

  return <OfflineContext.Provider value={value}>{children}</OfflineContext.Provider>;
}

/** Removes everything kept for a server, when it is signed out of. */
export async function forgetOffline(serverId: string): Promise<void> {
  deleteServerCopies(serverId);
  await localDb.forgetServer(serverId);
}
