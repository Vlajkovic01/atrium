import type { FilesApi, NodeInfo } from "@atrium/shared";

/** What is needed of a file to fetch it: one in a folder, or a photo of the timeline. */
export type Viewable = Pick<NodeInfo, "id" | "name" | "mediaType" | "size"> & {
  readonly version?: NodeInfo["version"];
};
import { Directory, File, Paths } from "expo-file-system";

/**
 * Copies of files kept on the phone for as long as they are being looked
 * at: a PDF the app shows, or a file handed to another app. They live in
 * the app's own cache, which no other app can read, under the server they
 * came from (D-052).
 *
 * Handing a file to another app gives every app that opens that kind of
 * file permission to read that one copy, until the phone restarts. The
 * copies are therefore removed when the app starts again and when the
 * server is signed out of; without the file the permission reads nothing.
 */
const ROOT = "opened";

function serverFolder(serverId: string): Directory {
  return new Directory(Paths.cache, ROOT, serverId);
}

/** A signed address as the server gives it ("/api/v1/...") made absolute. */
export function absoluteUrl(serverUrl: string, url: string): string {
  return /^https?:\/\//.test(url) ? url : `${serverUrl}${url}`;
}

/** A name the file system accepts; the server already refuses slashes, this also covers what Android refuses. */
function safeName(name: string): string {
  const cleaned = [...name]
    .map((c) => (c.charCodeAt(0) < 32 || '\\/:*?"<>|'.includes(c) ? "_" : c))
    .join("")
    .trim();
  return (cleaned || "file").slice(-200);
}

/**
 * The file's content on the phone, downloaded once per version. Returns
 * its `file://` address.
 */
export async function localCopy(
  files: FilesApi,
  server: { readonly id: string; readonly url: string },
  node: Viewable,
): Promise<string> {
  const folder = new Directory(serverFolder(server.id), `${node.id}-${node.version ?? 0}`);
  folder.create({ intermediates: true, idempotent: true });
  const file = new File(folder, safeName(node.name));
  if (file.exists && file.size === Number(node.size)) {
    return file.uri;
  }

  const signed = await files.contentUrl(node.id, { inline: false });
  const downloaded = await File.downloadFileAsync(absoluteUrl(server.url, signed.url), file, { idempotent: true });
  return downloaded.uri;
}

/** What happened when a file was handed to another app. */
export type OpenElsewhere = "opened" | "noApp";

/**
 * Hands the file to an app of the person's choice. "noApp" when nothing
 * on the phone opens this kind of file.
 */
export async function openElsewhere(
  files: FilesApi,
  server: { readonly id: string; readonly url: string },
  node: Viewable,
  /** A whole copy already on the phone (an offline copy), handed over instead of fetching again. */
  kept: string | null = null,
): Promise<OpenElsewhere> {
  const file = new File(kept ?? (await localCopy(files, server, node)));
  const options = { mimeType: node.mediaType ?? "application/octet-stream", title: node.name };
  if (!(await file.canPreview(options))) {
    return "noApp";
  }

  await file.preview(options);
  return "opened";
}

/** Removes the copies of one server, or of all servers. Never fails: a copy left behind goes with the cache. */
export function forgetLocalCopies(serverId?: string): void {
  try {
    const folder = serverId ? serverFolder(serverId) : new Directory(Paths.cache, ROOT);
    if (folder.exists) {
      folder.delete();
    }
  } catch {
    // The system clears the cache by itself when space runs short.
  }
}
