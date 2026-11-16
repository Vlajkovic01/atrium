import {
  parsePhoneLink,
  parseServerAddress,
  probeServer,
  signInTo,
  type ProbeResult,
  type ServerRegistry,
} from "@atrium/shared";

import { appVersion, thisDevice } from "./device.ts";

/** What stands between a typed or scanned address and a server the app can sign in to. */
export type AddressCheck =
  | { readonly ok: true; readonly url: string }
  | { readonly ok: false; readonly reason: "empty" | "invalid" }
  /** Plain HTTP to an address that is not local: explained on its own screen. */
  | { readonly ok: false; readonly reason: "insecure"; readonly typed: string }
  | { readonly ok: false; readonly reason: Extract<ProbeResult, { ok: false }>["reason"] };

/** Reads an address, refuses plain HTTP outside the local network (D-049), then asks whether an Atrium server answers there. */
export async function checkAddress(typed: string): Promise<AddressCheck> {
  const address = parseServerAddress(typed);
  if (!address.ok) {
    return address.reason === "insecure"
      ? { ok: false, reason: "insecure", typed: typed.trim() }
      : { ok: false, reason: address.reason };
  }

  const probe = await probeServer(address.url, appVersion);
  return probe.ok ? { ok: true, url: address.url } : { ok: false, reason: probe.reason };
}

export type LinkOutcome =
  | { readonly ok: true }
  | { readonly ok: false; readonly reason: "not_a_code" }
  | { readonly ok: false; readonly reason: "insecure"; readonly typed: string }
  | { readonly ok: false; readonly reason: Extract<ProbeResult, { ok: false }>["reason"] };

/**
 * Signs in with the text of a scanned "connect a phone" code, or of the
 * same link opened from the system camera. Throws an `ApiError` when the
 * server refuses the code, for example because its two minutes are over.
 */
export async function connectWithLink(registry: ServerRegistry, text: string): Promise<LinkOutcome> {
  const link = parsePhoneLink(text);
  if (!link) {
    return { ok: false, reason: "not_a_code" };
  }

  if (!link.server.ok) {
    if (link.server.reason !== "insecure") {
      return { ok: false, reason: "not_a_code" };
    }

    // The address as the code carries it, to show which server was refused.
    const raw = /[?&]server=([^&]*)/.exec(text)?.[1] ?? "";
    return { ok: false, reason: "insecure", typed: safeDecode(raw) };
  }

  const probe = await probeServer(link.server.url, appVersion);
  if (!probe.ok) {
    return { ok: false, reason: probe.reason };
  }

  const tokens = await signInTo(link.server.url, thisDevice()).withLinkCode(link.code);
  await registry.add(link.server.url, tokens);
  return { ok: true };
}

function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

/** The address without its scheme, for showing: "cloud.example.com". */
export function displayAddress(url: string): string {
  return url.replace(/^https?:\/\//, "");
}
