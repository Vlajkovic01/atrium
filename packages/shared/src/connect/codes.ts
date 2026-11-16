import { parseServerAddress, type ServerAddress } from "./address.ts";

/**
 * What Atrium's two QR codes carry. Written and read by the same code in
 * every client, so the web app that shows a code and the phone that scans
 * it cannot disagree about its shape.
 */

/** The code a signed-in web session shows to sign a phone in (DESIGN.md 6.5). */
export interface PhoneLink {
  readonly server: ServerAddress;
  /** Single-use, valid for two minutes. */
  readonly code: string;
}

/** The content of the "connect a phone" QR code. */
export function buildPhoneLink(serverUrl: string, code: string): string {
  return `atrium://link?server=${encodeURIComponent(serverUrl)}&code=${encodeURIComponent(code)}`;
}

/**
 * Reads a scanned "connect a phone" code. Null when the text is something
 * else. The server address is returned as parsed: a code that points at a
 * plain HTTP server outside the local network is recognized, and refused
 * with the same explanation as a typed address.
 */
export function parsePhoneLink(text: string): PhoneLink | null {
  const match = /^atrium:\/\/link\/?\?(.*)$/i.exec(text.trim());
  if (!match) {
    return null;
  }

  const query = readQuery(match[1]!);
  const server = query.get("server");
  const code = query.get("code");
  if (!server || !code || code.length > 256) {
    return null;
  }

  return { server: parseServerAddress(server), code };
}

/** The code a TV shows to be approved from a signed-in phone (DESIGN.md 6.4). */
export interface PairingLink {
  readonly server: ServerAddress;
  /** As shown on the TV: `XXXX-XXXX`. */
  readonly userCode: string;
}

/** Reads the link in a TV's pairing QR code: `https://<server>/pair?code=XXXX-XXXX`. Null for anything else. */
export function parsePairingLink(text: string): PairingLink | null {
  const match = /^(https?:\/\/[^\s?#]+)\/pair\/?\?([^\s#]*)$/i.exec(text.trim());
  if (!match) {
    return null;
  }

  const userCode = readQuery(match[2]!).get("code")?.toUpperCase();
  if (!userCode || !/^[A-Z0-9]{4}-?[A-Z0-9]{4}$/.test(userCode)) {
    return null;
  }

  return {
    server: parseServerAddress(match[1]!),
    userCode: userCode.includes("-") ? userCode : `${userCode.slice(0, 4)}-${userCode.slice(4)}`,
  };
}

function readQuery(query: string): Map<string, string> {
  const result = new Map<string, string>();
  for (const pair of query.split("&")) {
    const separator = pair.indexOf("=");
    if (separator <= 0) {
      continue;
    }

    try {
      result.set(
        decodeURIComponent(pair.slice(0, separator)),
        decodeURIComponent(pair.slice(separator + 1).replace(/\+/g, " ")),
      );
    } catch {
      // A badly escaped value is ignored; the code is then incomplete.
    }
  }

  return result;
}
