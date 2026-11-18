import { requireOptionalNativeModule } from "expo";
import type { AndroidMediaCapabilities } from "@atrium/shared";

interface Native {
  read(): AndroidMediaCapabilities;
}

// Present in the Android app; absent elsewhere.
const native = requireOptionalNativeModule<Native>("AtriumMediaCapabilities");

/** What this phone decodes and shows, as Android reports it; null where Android is not there to ask. */
export function readMediaCapabilities(): AndroidMediaCapabilities | null {
  return native?.read() ?? null;
}
