import { call } from "../api/call.ts";
import type { ApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";

export type NodeInfo = components["schemas"]["NodeInfo"];
export type NodePage = components["schemas"]["NodePage"];
export type LibraryInfo = components["schemas"]["LibraryInfo"];
export type BatchItem = components["schemas"]["BatchItemResult"];
export type SignedUrl = components["schemas"]["SignedUrlResponse"];

/** What a folder's contents can be ordered by; the server does the ordering. */
export const fileSorts = ["name", "modified", "size", "type"] as const;
export type FileSort = (typeof fileSorts)[number];

export interface FileOrder {
  readonly sort: FileSort;
  readonly descending: boolean;
}

export const defaultFileOrder: FileOrder = { sort: "name", descending: false };

/** An order as one word, "modified:desc", for a setting or a list of choices. */
export function fileOrderKey(order: FileOrder): string {
  return `${order.sort}:${order.descending ? "desc" : "asc"}`;
}

/** Reads what `fileOrderKey` wrote. Anything else gives the default, so a damaged setting never breaks a folder. */
export function parseFileOrder(key: string | null | undefined): FileOrder {
  const [sort, direction] = (key ?? "").split(":");
  return fileSorts.includes(sort as FileSort) && (direction === "asc" || direction === "desc")
    ? { sort: sort as FileSort, descending: direction === "desc" }
    : defaultFileOrder;
}

/** The translation key of an order's name: "files.sort.modifiedDesc". */
export function fileOrderLabelKey(order: FileOrder): string {
  return `files.sort.${order.sort}${order.descending ? "Desc" : "Asc"}`;
}

/**
 * The library a person's files start in: their own, or the first they
 * were given when they have none.
 */
export function homeLibrary(libraries: readonly LibraryInfo[]): LibraryInfo | undefined {
  return libraries.find((l) => l.kind === "personal") ?? libraries[0];
}

/**
 * The calls a file browser makes, the same for every client. Each throws
 * an `ApiError` with the server's code, or "network".
 */
export function filesApi(api: ApiClient) {
  return {
    libraries: (): Promise<LibraryInfo[]> => call(api.GET("/api/v1/libraries")),

    node: (id: string): Promise<NodeInfo> => call(api.GET("/api/v1/nodes/{id}", { params: { path: { id } } })),

    /** The folders above an item, from the library's root down. */
    path: (id: string): Promise<NodeInfo[]> => call(api.GET("/api/v1/nodes/{id}/path", { params: { path: { id } } })),

    children: (
      id: string,
      options: FileOrder & {
        readonly cursor?: string | null;
        readonly limit?: number;
        readonly kind?: NodeInfo["kind"];
      },
    ): Promise<NodePage> =>
      call(
        api.GET("/api/v1/nodes/{id}/children", {
          params: {
            path: { id },
            query: {
              sort: options.sort,
              descending: options.descending,
              ...(options.limit ? { limit: options.limit } : {}),
              ...(options.kind ? { kind: options.kind } : {}),
              ...(options.cursor ? { cursor: options.cursor } : {}),
            },
          },
        }),
      ),

    createFolder: (parentId: string, name: string): Promise<NodeInfo> =>
      call(api.POST("/api/v1/nodes", { body: { parentId, name } })),

    /** Several items at once. The answer says, item by item, what went through. */
    batch: async (
      operation: "move" | "copy" | "trash",
      ids: readonly string[],
      parentId?: string,
    ): Promise<BatchItem[]> =>
      (await call(api.POST("/api/v1/nodes/batch", { body: { operation, ids: [...ids], parentId: parentId ?? null } })))
        .items,

    /** Out of the trash, back where it was; under another name if that one is taken meanwhile. */
    restore: async (id: string): Promise<void> => {
      await call(
        api.POST("/api/v1/trash/{id}/restore", {
          params: { path: { id } },
          body: { parentId: null, onConflict: "rename" },
        }),
      );
    },

    /** A short-lived signed address of a file's content: to show it (`inline`) or to save it. */
    contentUrl: (id: string, options: { readonly inline: boolean; readonly versionId?: string }): Promise<SignedUrl> =>
      call(
        api.POST("/api/v1/nodes/{id}/content-url", {
          params: { path: { id } },
          body: { inline: options.inline, versionId: options.versionId ?? null },
        }),
      ),

    /** Several items, or a folder: one ZIP, streamed by the server. */
    archiveUrl: (ids: readonly string[]): Promise<SignedUrl> =>
      call(api.POST("/api/v1/downloads/archive", { body: { ids: [...ids] } })),
  };
}

export type FilesApi = ReturnType<typeof filesApi>;
