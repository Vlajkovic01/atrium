import { defaultFileOrder, fileOrderKey, parseFileOrder, type FileOrder } from "@atrium/shared";
import { useSyncExternalStore } from "react";

import { settings } from "../platform/storage";

const KEY = "atrium.files.order";

// One order for every folder, remembered on this device. Every open
// folder screen follows it, so going back never shows another order.
let current: FileOrder = defaultFileOrder;
const listeners = new Set<() => void>();

/** Reads the remembered order once, when the app starts. */
export async function loadFileOrder(): Promise<void> {
  try {
    current = parseFileOrder(await settings.get(KEY));
  } catch {
    current = defaultFileOrder;
  }
}

export function setFileOrder(order: FileOrder): void {
  current = order;
  for (const listener of listeners) {
    listener();
  }

  // Not being able to remember the order is no reason to refuse it.
  void settings.set(KEY, fileOrderKey(order)).catch(() => undefined);
}

export function useFileOrder(): FileOrder {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => current,
    () => current,
  );
}
