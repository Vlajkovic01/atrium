import { Directory, File, Paths } from "expo-file-system";

/**
 * Where offline copies live: the app's own documents folder, which the
 * system does not clear by itself (unlike the cache) and no other app can
 * read. One folder per server, per offline item and per version, so a new
 * version never overwrites a whole copy before it is whole itself.
 */
const ROOT = "offline";

function versionFolder(serverId: string, rootId: string, nodeId: string, version: number): Directory {
  return new Directory(Paths.document, ROOT, serverId, rootId, `${nodeId}-${version}`);
}

/** The `file://` address a copy will have. The folder is made on the way. */
export function copyPath(serverId: string, rootId: string, nodeId: string, version: number, name: string): string {
  const folder = versionFolder(serverId, rootId, nodeId, version);
  folder.create({ intermediates: true, idempotent: true });
  const safe = [...name]
    .map((c) => (c.charCodeAt(0) < 32 || '\\/:*?"<>|'.includes(c) ? "_" : c))
    .join("")
    .trim();
  return new File(folder, (safe || "file").slice(-200)).uri;
}

/** Writes a part of a copy at its place; the first part starts the file afresh. */
export function writePart(path: string, start: number, bytes: Uint8Array): void {
  const file = new File(path);
  if (start === 0 || !file.exists) {
    file.create({ overwrite: true });
  }

  const handle = file.open();
  try {
    handle.offset = start;
    handle.writeBytes(bytes);
  } finally {
    handle.close();
  }
}

/** How many bytes of a copy are on the phone; 0 when it is not there. */
export function copySize(path: string | null): number {
  if (!path) {
    return 0;
  }

  try {
    const file = new File(path);
    return file.exists ? (file.size ?? 0) : 0;
  } catch {
    return 0;
  }
}

/** The address to show a whole copy from. */
export function copyUri(path: string): string {
  return path;
}

/** A copy's text, for the viewer. */
export function copyText(path: string): Promise<string> {
  return new File(path).text();
}

function remove(target: File | Directory): void {
  try {
    if (target.exists) {
      target.delete();
    }
  } catch {
    // Left behind, it is found and removed with its item or its server.
  }
}

export function deleteCopy(path: string | null): void {
  if (path) {
    remove(new File(path).parentDirectory);
  }
}

/** Removes every copy of one offline item. */
export function deleteRootCopies(serverId: string, rootId: string): void {
  remove(new Directory(Paths.document, ROOT, serverId, rootId));
}

/** Removes every copy kept for a server, when it is signed out of. */
export function deleteServerCopies(serverId: string): void {
  remove(new Directory(Paths.document, ROOT, serverId));
}
