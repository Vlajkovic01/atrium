import { describe, expect, it } from "vitest";

import { createApiClient } from "../src/api/client.ts";
import { devicesApi, normalizePairingCode } from "../src/connect/devices.ts";

function server() {
  const seen: { method: string; path: string; body: unknown }[] = [];
  const api = createApiClient({
    baseUrl: "http://atrium.test",
    fetch: async (input, init) => {
      const request = new Request(input, init);
      const text = await request.text();
      seen.push({ method: request.method, path: new URL(request.url).pathname, body: text ? JSON.parse(text) : null });
      return new Response(JSON.stringify([]), { headers: { "Content-Type": "application/json" } });
    },
    getAccessToken: () => "token",
    refresh: () => Promise.resolve(null),
  });
  return { devices: devicesApi(api), seen };
}

describe("devices", () => {
  it("lists sessions and signs one out", async () => {
    const { devices, seen } = server();

    await devices.sessions();
    await devices.signOut("s1");

    expect(seen.map((s) => [s.method, s.path])).toEqual([
      ["GET", "/api/v1/sessions"],
      ["DELETE", "/api/v1/sessions/s1"],
    ]);
  });

  it("looks a waiting device up, then approves it with what it may do, or denies it", async () => {
    const { devices, seen } = server();

    await devices.pairing("ABCD-1234");
    await devices.approve("ABCD-1234", { readOnly: true, hidePersonalLibrary: false });
    await devices.deny("WXYZ-9876");

    expect(seen).toEqual([
      { method: "GET", path: "/api/v1/auth/pairing/ABCD-1234", body: null },
      {
        method: "POST",
        path: "/api/v1/auth/pairing/ABCD-1234/approve",
        body: { readOnly: true, hidePersonalLibrary: false },
      },
      { method: "POST", path: "/api/v1/auth/pairing/WXYZ-9876/deny", body: null },
    ]);
  });

  it.each([
    ["abcd1234", "ABCD-1234"],
    [" ABCD-1234 ", "ABCD-1234"],
    ["abcd 1234", "ABCD-1234"],
    ["ABC-123", null],
    ["ABCD-12345", null],
    ["ABCD-12!4", null],
  ])("reads the typed code %j as %j", (typed, expected) => {
    expect(normalizePairingCode(typed)).toBe(expected);
  });
});
