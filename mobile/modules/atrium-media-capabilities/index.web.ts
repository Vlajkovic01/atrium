import type { AndroidMediaCapabilities } from "@atrium/shared";

// Only for looking at the app's screens in a browser while developing
// (mobile/preview): there is no Android to ask, and the browser describes
// itself instead.
export function readMediaCapabilities(): AndroidMediaCapabilities | null {
  return null;
}
