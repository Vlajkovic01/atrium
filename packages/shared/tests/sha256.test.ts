import { describe, expect, it } from "vitest";

import { fingerprint, sha256 } from "../src/upload/sha256.ts";

const hex = (bytes: Uint8Array) => [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");

describe("sha256", () => {
  it("matches the published test vectors", () => {
    expect(hex(sha256(new Uint8Array(0)))).toBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    expect(hex(sha256(new TextEncoder().encode("abc")))).toBe(
      "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
    );
    expect(hex(sha256(new TextEncoder().encode("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")))).toBe(
      "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
    );
  });

  it("agrees with the platform's implementation at every block boundary", async () => {
    for (const size of [1, 55, 56, 63, 64, 65, 119, 120, 128, 1000, 100_003]) {
      const data = new Uint8Array(size).map((_, i) => (i * 31 + size) & 0xff);
      const expected = new Uint8Array(await crypto.subtle.digest("SHA-256", data));
      expect(hex(sha256(data)), `size ${size}`).toBe(hex(expected));
    }
  });

  it("hashes a slice of a larger buffer, not the whole buffer", async () => {
    const whole = new Uint8Array(200).map((_, i) => i);
    const part = whole.subarray(37, 150);
    expect(hex(sha256(part))).toBe(hex(new Uint8Array(await crypto.subtle.digest("SHA-256", part.slice()))));
  });
});

describe("fingerprint", () => {
  const of = (bytes: Uint8Array) => fingerprint(bytes.length, (s, e) => Promise.resolve(bytes.slice(s, e)));

  it("tells apart files of the same size", async () => {
    const a = new Uint8Array(3_000_000).fill(1);
    const b = new Uint8Array(3_000_000).fill(1);
    b[2_999_999] = 2;
    expect(await of(a)).not.toBe(await of(b));
    expect(await of(a)).toBe(await of(a.slice()));
  });
});
