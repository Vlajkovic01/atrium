// Only for looking at the app's screens in a browser while developing
// (mobile/preview): there is no fingerprint here, so the lock can be
// turned on and an unlock always succeeds once asked for.

export function canLock(): Promise<boolean> {
  return Promise.resolve(true);
}

/** Succeeds after a moment, so the lock screen can be seen. */
export function unlock(): Promise<boolean> {
  return new Promise((resolve) => setTimeout(() => resolve(true), 1500));
}

export function hideFromSwitcher(): Promise<void> {
  return Promise.resolve();
}
