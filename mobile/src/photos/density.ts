import { useSyncExternalStore } from "react";

import { settings } from "../platform/storage";

const KEY = "atrium.photos.density";

/** Row heights of the timeline, from many small pictures to few large ones. */
export const densities = [72, 112, 176] as const;
const DEFAULT = 1;

// One density for the whole app, remembered on this device.
let current = DEFAULT;
const listeners = new Set<() => void>();

export async function loadDensity(): Promise<void> {
  try {
    // Nothing stored yet reads as null, which Number() would turn into the smallest size.
    const text = await settings.get(KEY);
    const stored = text === null || text === "" ? Number.NaN : Number(text);
    current = Number.isInteger(stored) && stored >= 0 && stored < densities.length ? stored : DEFAULT;
  } catch {
    current = DEFAULT;
  }
}

/** One step larger (+1) or smaller (-1) pictures; stays within the levels. */
export function stepDensity(step: 1 | -1): void {
  const next = Math.min(densities.length - 1, Math.max(0, current + step));
  if (next === current) {
    return;
  }

  current = next;
  for (const listener of listeners) {
    listener();
  }

  void settings.set(KEY, String(next)).catch(() => undefined);
}

/** The index of the current level in `densities`. */
export function useDensity(): number {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => current,
    () => current,
  );
}
