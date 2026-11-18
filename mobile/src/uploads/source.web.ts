import type { UploadSource } from "@atrium/shared";

// Only for looking at the app's screens in a browser while developing
// (mobile/preview): the pickers there hand over blob: or data: addresses,
// read here with fetch. Nothing survives a reload, so nothing resumes.

import type { Picked } from "./source.ts";

export type { Picked } from "./source.ts";

export function fileSource(picked: Picked): UploadSource {
  let blob: Promise<Blob> | null = null;
  return {
    name: picked.name,
    size: picked.size,
    ...(picked.lastModified !== undefined ? { lastModified: picked.lastModified } : {}),
    slice: async (start, end) => {
      blob ??= fetch(picked.uri).then((response) => response.blob());
      return new Uint8Array(await (await blob).slice(start, end).arrayBuffer());
    },
  };
}

export function stillThere(): boolean {
  return false;
}

export function removeCopy(): void {
  // Nothing was copied.
}

export function sizeOf(_uri: string, browserFile?: unknown): number | undefined {
  return browserFile instanceof Blob ? browserFile.size : undefined;
}
