import { filesApi } from "@atrium/shared";

import { api } from "../api.ts";

export type { BatchItem, LibraryInfo, NodeInfo } from "@atrium/shared";

// The calls themselves are shared with the mobile app; only starting a
// download is the browser's own.
const files = filesApi(api);

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
  const signed = await files.contentUrl(nodeId, { inline: false, ...(versionId ? { versionId } : {}) });
  startDownload(signed.url);
}

/** Several items, or a folder: one ZIP, streamed by the server. */
export async function downloadArchive(ids: readonly string[]): Promise<void> {
  startDownload((await files.archiveUrl(ids)).url);
}

export const batch = files.batch;
export const restore = files.restore;
