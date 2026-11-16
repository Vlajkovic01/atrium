import type { components } from "../api/schema.d.ts";

export type SystemInfo = components["schemas"]["SystemInfo"];

export type ProbeResult =
  | { readonly ok: true; readonly info: SystemInfo }
  | {
      readonly ok: false;
      /**
       * `unreachable`: nothing answered. `not_atrium`: something answered
       * that is not an Atrium server. `setup_incomplete`: the server has
       * not been set up in a browser yet. `client_too_old`: the server
       * needs a newer app.
       */
      readonly reason: "unreachable" | "not_atrium" | "setup_incomplete" | "client_too_old";
      readonly info?: SystemInfo;
    };

/**
 * Asks an address whether it is an Atrium server this app can use, before
 * anyone types a password into it.
 */
export async function probeServer(
  url: string,
  clientVersion: string,
  fetch: typeof globalThis.fetch = globalThis.fetch,
  timeoutMs = 10_000,
): Promise<ProbeResult> {
  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), timeoutMs);
  let body: unknown;
  try {
    const response = await fetch(`${url}/api/v1/system/info`, {
      headers: { Accept: "application/json" },
      signal: abort.signal,
    });
    if (!response.ok) {
      return { ok: false, reason: "not_atrium" };
    }

    body = await response.json();
  } catch (error) {
    // Something answered, but not with JSON: a router's page, another product.
    return { ok: false, reason: error instanceof SyntaxError ? "not_atrium" : "unreachable" };
  } finally {
    clearTimeout(timer);
  }

  if (!isSystemInfo(body)) {
    return { ok: false, reason: "not_atrium" };
  }

  if (!body.setupComplete) {
    return { ok: false, reason: "setup_incomplete", info: body };
  }

  if (compareVersions(clientVersion, body.minClientVersion) < 0) {
    return { ok: false, reason: "client_too_old", info: body };
  }

  return { ok: true, info: body };
}

function isSystemInfo(value: unknown): value is SystemInfo {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const info = value as Partial<Record<keyof SystemInfo, unknown>>;
  return (
    info.name === "Atrium" &&
    typeof info.version === "string" &&
    typeof info.minClientVersion === "string" &&
    typeof info.setupComplete === "boolean"
  );
}

/** Compares versions such as "1.4.0" number by number; anything after "-" or "+" is ignored. */
export function compareVersions(a: string, b: string): number {
  const parts = (version: string) =>
    version
      .split(/[-+]/)[0]!
      .split(".")
      .map((part) => Number.parseInt(part, 10) || 0);
  const left = parts(a);
  const right = parts(b);
  for (let i = 0; i < Math.max(left.length, right.length); i++) {
    const difference = (left[i] ?? 0) - (right[i] ?? 0);
    if (difference !== 0) {
      return Math.sign(difference);
    }
  }

  return 0;
}
