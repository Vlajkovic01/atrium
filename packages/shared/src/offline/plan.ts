import type { components } from "../api/schema.d.ts";

type NodeInfo = components["schemas"]["NodeInfo"];

/** A file kept on the device, as the device remembers it. */
export interface KeptFile {
  readonly nodeId: string;
  readonly version: number;
  /** Bytes already on the device; equal to `size` when the copy is whole. */
  readonly bytes: number;
  readonly size: number;
}

/** What to do so the device holds the files of an offline item as they are on the server now. */
export interface OfflinePlan {
  /** Files to fetch: new ones, and new versions of kept ones (fetched beside the old copy, which goes after). */
  readonly fetch: readonly NodeInfo[];
  /** Files kept on the device that are no longer under the item, or no longer readable. */
  readonly drop: readonly string[];
}

/**
 * Compares what the server lists under an offline item (the item itself
 * when it is a file, every file below it when it is a folder) with what
 * the device keeps. A copy of the same version stays, even half fetched:
 * fetching goes on where it stopped.
 */
export function planOffline(listed: readonly NodeInfo[], kept: readonly KeptFile[]): OfflinePlan {
  const keptById = new Map(kept.map((k) => [k.nodeId, k]));
  const files = listed.filter((n) => n.kind === "file");
  const listedIds = new Set(files.map((n) => n.id));
  return {
    fetch: files.filter((n) => keptById.get(n.id)?.version !== Number(n.version)),
    drop: kept.filter((k) => !listedIds.has(k.nodeId)).map((k) => k.nodeId),
  };
}

/** The next part of a file to fetch, or null when it is whole. */
export function nextRange(
  bytes: number,
  size: number,
  chunk: number,
): { readonly start: number; readonly end: number } | null {
  if (bytes >= size) {
    return null;
  }

  return { start: bytes, end: Math.min(size, bytes + chunk) - 1 };
}

/** Totals for showing progress and space: bytes on the device of bytes wanted, and how many files are whole. */
export function offlineTotals(kept: readonly KeptFile[]): {
  readonly bytes: number;
  readonly size: number;
  readonly whole: number;
  readonly files: number;
} {
  return {
    bytes: kept.reduce((n, k) => n + Math.min(k.bytes, k.size), 0),
    size: kept.reduce((n, k) => n + k.size, 0),
    whole: kept.filter((k) => k.bytes >= k.size).length,
    files: kept.length,
  };
}
