import { ServerRegistry } from "@atrium/shared";

import { secrets, settings } from "./platform/storage";

let loading: Promise<ServerRegistry> | null = null;

/**
 * The one list of servers and sessions for the whole app: the screens and
 * camera backup's background job use the same one. Two would each renew
 * the same sign-in, and the server, which replaces a refresh token on each
 * use, would then refuse one of them and sign the phone out.
 */
export function loadRegistry(): Promise<ServerRegistry> {
  loading ??= ServerRegistry.load({ store: settings, secrets }).catch((e: unknown) => {
    loading = null;
    throw e;
  });
  return loading;
}
