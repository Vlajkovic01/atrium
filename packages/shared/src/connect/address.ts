/**
 * The address of a server as a person types it or a QR code carries it,
 * and the rule for plain HTTP: allowed only to an address on the local
 * network, where it never leaves the building. Anywhere else a password
 * or a token sent over HTTP can be read on the way, so the apps refuse.
 *
 * Parsed by hand: React Native's URL class does not implement everything
 * a browser's does, and all clients must agree on what an address means.
 */

export type ServerAddress =
  | {
      readonly ok: true;
      /** Scheme, host, optional port and path, without a trailing slash. */
      readonly url: string;
      readonly secure: boolean;
      /** The host is on the local network or this device. */
      readonly local: boolean;
    }
  | {
      readonly ok: false;
      /**
       * `insecure`: plain HTTP to an address that is not local. The app
       * explains why it cannot connect and what to set up instead.
       */
      readonly reason: "empty" | "invalid" | "insecure";
    };

const ADDRESS =
  /^(?:([a-z][a-z0-9+.-]*):\/\/)?(\[[0-9a-f:.]+\]|[^\s/:?#@[\]]+)(?::(\d{1,5}))?(\/[^\s?#]*)?(?:[?#]\S*)?$/i;

/**
 * Reads a server address. Without a scheme, a local address is taken as
 * HTTP, which is how a home server answers out of the box, and anything
 * else as HTTPS.
 */
export function parseServerAddress(input: string): ServerAddress {
  const text = input.trim();
  if (text.length === 0) {
    return { ok: false, reason: "empty" };
  }

  const match = ADDRESS.exec(text);
  if (!match) {
    return { ok: false, reason: "invalid" };
  }

  const host = match[2]!.toLowerCase();
  const port = match[3] === undefined ? null : Number(match[3]);
  const local = isLocalHost(host);
  const scheme = (match[1] ?? (local ? "http" : "https")).toLowerCase();
  if ((scheme !== "http" && scheme !== "https") || !isHost(host) || (port !== null && (port < 1 || port > 65535))) {
    return { ok: false, reason: "invalid" };
  }

  if (scheme === "http" && !local) {
    return { ok: false, reason: "insecure" };
  }

  const standard = scheme === "http" ? 80 : 443;
  const path = (match[4] ?? "").replace(/\/+$/, "");
  return {
    ok: true,
    url: `${scheme}://${host}${port === null || port === standard ? "" : `:${port}`}${path}`,
    secure: scheme === "https",
    local,
  };
}

/**
 * Whether what is sent to this address can be read on the way: plain HTTP
 * to anything but this device itself. A local address is allowed over
 * HTTP (see {@link parseServerAddress}), but it is still unencrypted, and
 * every client marks it so for as long as it is connected (DESIGN.md 12,
 * risk 6). Loopback never leaves the device, so it is not marked.
 */
export function isUnencrypted(url: string): boolean {
  const match = /^http:\/\/(\[[^\]]*\]|[^/:?#]+)/i.exec(url.trim());
  if (!match) {
    return false;
  }

  return !isLoopbackHost(match[1]!.toLowerCase());
}

function isLoopbackHost(host: string): boolean {
  if (host === "localhost" || host.endsWith(".localhost") || host === "[::1]") {
    return true;
  }

  const ipv4 = readIpv4(host);
  return ipv4 !== null && ipv4[0] === 127;
}

/**
 * Whether a host can only be on the local network or this device: private,
 * loopback and link-local addresses, the range carriers and mesh VPNs use
 * between their own devices (100.64.0.0/10), names that never resolve on
 * the internet (`.local`, `.lan`, `.home.arpa`, `.internal`, `localhost`),
 * and names without a dot, which only a local resolver can answer.
 */
export function isLocalHost(host: string): boolean {
  const name = host.toLowerCase();
  if (name.startsWith("[")) {
    return isLocalIpv6(name.slice(1, -1));
  }

  const ipv4 = readIpv4(name);
  if (ipv4) {
    const [a, b] = ipv4 as [number, number, number, number];
    return (
      a === 10 ||
      a === 127 ||
      (a === 172 && b >= 16 && b <= 31) ||
      (a === 192 && b === 168) ||
      (a === 169 && b === 254) ||
      (a === 100 && b >= 64 && b <= 127)
    );
  }

  if (/^[\d.]+$/.test(name)) {
    return false;
  }

  return (
    !name.includes(".") ||
    name === "localhost" ||
    [".localhost", ".local", ".lan", ".home.arpa", ".internal"].some((suffix) => name.endsWith(suffix))
  );
}

function isLocalIpv6(address: string): boolean {
  if (address === "::1") {
    return true;
  }

  // An IPv4 address written inside an IPv6 one follows the IPv4 rules.
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/.exec(address);
  if (mapped) {
    return isLocalHost(mapped[1]!);
  }

  const first = parseInt(address.split(":")[0] || "0", 16);
  // fc00::/7 is the private range, fe80::/10 the link-local one.
  return (first & 0xfe00) === 0xfc00 || (first & 0xffc0) === 0xfe80;
}

function readIpv4(name: string): number[] | null {
  const parts = name.split(".");
  if (parts.length !== 4 || !parts.every((part) => /^\d{1,3}$/.test(part))) {
    return null;
  }

  const numbers = parts.map(Number);
  return numbers.every((n) => n <= 255) ? numbers : null;
}

function isHost(host: string): boolean {
  if (host.startsWith("[")) {
    return /^\[[0-9a-f:.]{2,45}\]$/.test(host);
  }

  if (/^[\d.]+$/.test(host)) {
    return readIpv4(host) !== null;
  }

  return host.length <= 253 && host.split(".").every((label) => /^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$/.test(label));
}
