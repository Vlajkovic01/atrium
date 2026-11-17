import type { FilesApi } from "@atrium/shared";

import type { Viewable } from "./local.ts";

// Only for looking at the app's screens in a browser while developing
// (mobile/preview). A browser keeps no copies: it is given the signed
// address itself, and "another app" is a new tab.

export function absoluteUrl(serverUrl: string, url: string): string {
  return /^https?:\/\//.test(url) ? url : `${serverUrl}${url}`;
}

export async function localCopy(
  files: FilesApi,
  server: { readonly id: string; readonly url: string },
  node: Viewable,
): Promise<string> {
  const signed = await files.contentUrl(node.id, { inline: true });
  return absoluteUrl(server.url, signed.url);
}

export type OpenElsewhere = "opened" | "noApp";

export async function openElsewhere(
  files: FilesApi,
  server: { readonly id: string; readonly url: string },
  node: Viewable,
  kept: string | null = null,
): Promise<OpenElsewhere> {
  if (kept) {
    window.open(kept, "_blank", "noopener");
    return "opened";
  }

  const signed = await files.contentUrl(node.id, { inline: false });
  window.open(absoluteUrl(server.url, signed.url), "_blank", "noopener");
  return "opened";
}

/** Nothing is kept, so there is nothing to forget. */
export function forgetLocalCopies(): void {}
