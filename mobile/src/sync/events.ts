import type { ChangeEffects } from "@atrium/shared";

/** What a pull of the change feed found: changes, or that everything must be read again. */
export type SyncEvent =
  | { readonly serverId: string; readonly kind: "changes"; readonly effects: ChangeEffects }
  | { readonly serverId: string; readonly kind: "reset" };

const listeners = new Set<(event: SyncEvent) => void>();

/** Lets other parts of the app follow the change feed, such as the offline copies. */
export function onSync(listener: (event: SyncEvent) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function emitSync(event: SyncEvent): void {
  for (const listener of listeners) {
    listener(event);
  }
}
