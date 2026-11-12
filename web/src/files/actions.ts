import type { components } from "@atrium/shared";

import { api, call } from "../api.ts";

export type NodeInfo = components["schemas"]["NodeInfo"];
export type LibraryInfo = components["schemas"]["LibraryInfo"];
export type BatchItem = components["schemas"]["BatchItemResult"];

/** Opens a download without loading the file into the page: the browser fetches a short-lived signed URL itself. */
function startDownload(url: string): void {
  const link = document.createElement("a");
  link.href = url;
  link.rel = "noopener";
  link.download = "";
  document.body.append(link);
  link.click();
  link.remove();
}

export async function downloadFile(nodeId: string, versionId?: string): Promise<void> {
  const signed = await call(
    api.POST("/api/v1/nodes/{id}/content-url", {
      params: { path: { id: nodeId } },
      body: { inline: false, versionId: versionId ?? null },
    }),
  );
  startDownload(signed.url);
}

/** Several items, or a folder: one ZIP, streamed by the server. */
export async function downloadArchive(ids: readonly string[]): Promise<void> {
  const signed = await call(api.POST("/api/v1/downloads/archive", { body: { ids: [...ids] } }));
  startDownload(signed.url);
}

export async function batch(
  operation: "move" | "copy" | "trash",
  ids: readonly string[],
  parentId?: string,
): Promise<BatchItem[]> {
  const result = await call(
    api.POST("/api/v1/nodes/batch", { body: { operation, ids: [...ids], parentId: parentId ?? null } }),
  );
  return result.items;
}

export async function restore(id: string): Promise<void> {
  await call(
    api.POST("/api/v1/trash/{id}/restore", {
      params: { path: { id } },
      body: { parentId: null, onConflict: "rename" },
    }),
  );
}
