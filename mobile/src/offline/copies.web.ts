// Only for looking at the app's screens in a browser while developing
// (mobile/preview): copies are kept in memory and shown through blob:
// addresses, so "offline" can be tried by cutting the network.

const parts = new Map<string, Uint8Array>();

export function copyPath(serverId: string, rootId: string, nodeId: string, version: number, name: string): string {
  return `memory://${serverId}/${rootId}/${nodeId}-${version}/${name}`;
}

export function writePart(path: string, start: number, bytes: Uint8Array): void {
  const before = start === 0 ? new Uint8Array(0) : (parts.get(path) ?? new Uint8Array(0));
  const next = new Uint8Array(start + bytes.length);
  next.set(before.subarray(0, start));
  next.set(bytes, start);
  parts.set(path, next);
}

export function copySize(path: string | null): number {
  return path ? (parts.get(path)?.length ?? 0) : 0;
}

const urls = new Map<string, string>();

export function copyUri(path: string): string {
  let url = urls.get(path);
  if (!url) {
    url = URL.createObjectURL(new Blob([new Uint8Array(parts.get(path) ?? new Uint8Array(0))]));
    urls.set(path, url);
  }

  return url;
}

export function copyText(path: string): Promise<string> {
  return Promise.resolve(new TextDecoder().decode(parts.get(path) ?? new Uint8Array(0)));
}

function forget(prefix: string): void {
  for (const key of [...parts.keys()]) {
    if (key.startsWith(prefix)) {
      parts.delete(key);
    }
  }
}

export function deleteCopy(path: string | null): void {
  if (path) {
    parts.delete(path);
  }
}

export function deleteRootCopies(serverId: string, rootId: string): void {
  forget(`memory://${serverId}/${rootId}/`);
}

export function deleteServerCopies(serverId: string): void {
  forget(`memory://${serverId}/`);
}
