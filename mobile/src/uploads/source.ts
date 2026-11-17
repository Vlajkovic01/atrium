import type { UploadSource } from "@atrium/shared";
import { File } from "expo-file-system";

/** A file chosen on the phone, as the pickers describe it. */
export interface Picked {
  /** A `file://` copy in the app's own cache, which the upload reads and then removes. */
  readonly uri: string;
  readonly name: string;
  readonly size: number;
  readonly lastModified?: number;
}

/**
 * The upload's view of a file on the phone: it reads only the part being
 * sent, so a large video is never held in memory whole.
 */
export function fileSource(picked: Picked): UploadSource {
  return {
    name: picked.name,
    size: picked.size,
    ...(picked.lastModified !== undefined ? { lastModified: picked.lastModified } : {}),
    slice: async (start, end) => {
      const handle = new File(picked.uri).open();
      try {
        handle.offset = start;
        return await handle.readBytes(end - start);
      } finally {
        handle.close();
      }
    },
  };
}

/** Whether the copy is still there to resume from (the system may clear the cache). */
export function stillThere(picked: Picked): boolean {
  try {
    const file = new File(picked.uri);
    return file.exists && file.size === picked.size;
  } catch {
    return false;
  }
}

/** Removes the copy once it is on the server, or no longer wanted. */
export function removeCopy(picked: Picked): void {
  try {
    const file = new File(picked.uri);
    if (file.exists) {
      file.delete();
    }
  } catch {
    // The cache is the system's to clear; a copy left behind goes with it.
  }
}

/** The size of a copy, when a picker did not say it. The second argument is for the browser twin. */
export function sizeOf(uri: string, ...rest: unknown[]): number | undefined {
  void rest;
  try {
    const file = new File(uri);
    return file.exists ? (file.size ?? undefined) : undefined;
  } catch {
    return undefined;
  }
}
