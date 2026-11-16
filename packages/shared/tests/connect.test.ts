import { describe, expect, it } from "vitest";

import { buildPhoneLink, isLocalHost, parsePairingLink, parsePhoneLink, parseServerAddress } from "../src/index.ts";

describe("server address", () => {
  it.each([
    ["192.168.1.20:8080", "http://192.168.1.20:8080"],
    ["  nas.local  ", "http://nas.local"],
    ["homeserver:5080/", "http://homeserver:5080"],
    ["localhost:5080", "http://localhost:5080"],
    ["http://10.0.0.5", "http://10.0.0.5"],
    ["http://10.0.0.5:80/", "http://10.0.0.5"],
    ["100.101.102.103:8080", "http://100.101.102.103:8080"],
    ["[fd12:3456::1]:8080", "http://[fd12:3456::1]:8080"],
    ["HTTP://NAS.LAN/atrium/", "http://nas.lan/atrium"],
  ])("takes the local address %s as plain HTTP", (input, url) => {
    expect(parseServerAddress(input)).toEqual({ ok: true, url, secure: false, local: true });
  });

  it.each([
    ["cloud.example.com", "https://cloud.example.com"],
    ["https://cloud.example.com:443/", "https://cloud.example.com"],
    ["https://cloud.example.com:8443/files/?x=1#top", "https://cloud.example.com:8443/files"],
    ["93.184.216.34", "https://93.184.216.34"],
  ])("takes the public address %s as HTTPS", (input, url) => {
    expect(parseServerAddress(input)).toEqual({ ok: true, url, secure: true, local: false });
  });

  it("keeps HTTPS on a local address when asked for", () => {
    expect(parseServerAddress("https://192.168.1.20")).toEqual({
      ok: true,
      url: "https://192.168.1.20",
      secure: true,
      local: true,
    });
  });

  it.each(["http://cloud.example.com", "http://93.184.216.34:8080", "http://172.32.0.1", "http://100.128.0.1"])(
    "refuses plain HTTP to %s, which is not local",
    (input) => {
      expect(parseServerAddress(input)).toEqual({ ok: false, reason: "insecure" });
    },
  );

  it.each(["ftp://nas.local", "http://", "exa mple.com", "user@example.com", "example.com:99999", "999.1.1.1", "a..b"])(
    "refuses %s as not an address",
    (input) => {
      expect(parseServerAddress(input)).toEqual({ ok: false, reason: "invalid" });
    },
  );

  it("says when nothing was typed", () => {
    expect(parseServerAddress("   ")).toEqual({ ok: false, reason: "empty" });
  });

  it("knows which hosts can only be local", () => {
    for (const host of [
      "127.0.0.1",
      "169.254.3.4",
      "172.16.0.1",
      "172.31.255.255",
      "[::1]",
      "[fe80::1]",
      "pc.home.arpa",
    ]) {
      expect(isLocalHost(host), host).toBe(true);
    }

    for (const host of [
      "172.15.0.1",
      "192.169.0.1",
      "11.0.0.1",
      "[2001:db8::1]",
      "local.example.com",
      "[::ffff:8.8.8.8]",
    ]) {
      expect(isLocalHost(host), host).toBe(false);
    }

    expect(isLocalHost("[::ffff:192.168.1.1]")).toBe(true);
  });
});

describe("QR codes", () => {
  it("reads back the phone link it writes", () => {
    const text = buildPhoneLink("https://cloud.example.com:8443", "a b&c=d/é");

    expect(parsePhoneLink(text)).toEqual({
      server: { ok: true, url: "https://cloud.example.com:8443", secure: true, local: false },
      code: "a b&c=d/é",
    });
  });

  it("recognizes a phone link to a server it must refuse", () => {
    const link = parsePhoneLink(buildPhoneLink("http://cloud.example.com", "code"));

    expect(link?.server).toEqual({ ok: false, reason: "insecure" });
  });

  it.each([
    "https://example.com",
    "atrium://link?server=x",
    "atrium://link?code=x",
    "atrium://other?server=a&code=b",
    "",
  ])("does not take %s for a phone link", (text) => {
    expect(parsePhoneLink(text)).toBeNull();
  });

  it("reads a TV's pairing link, with the code as the TV shows it", () => {
    expect(parsePairingLink("https://cloud.example.com/pair?code=abcd-2345")).toEqual({
      server: { ok: true, url: "https://cloud.example.com", secure: true, local: false },
      userCode: "ABCD-2345",
    });
    expect(parsePairingLink("http://192.168.1.20:8080/pair?code=ABCD2345")?.userCode).toBe("ABCD-2345");
    expect(parsePairingLink("http://192.168.1.20:8080/atrium/pair?code=ABCD2345")?.server).toMatchObject({
      url: "http://192.168.1.20:8080/atrium",
    });
  });

  it.each([
    "https://cloud.example.com/pair",
    "https://cloud.example.com/pair?code=abc",
    "https://cloud.example.com/?code=ABCD-2345",
  ])("does not take %s for a pairing link", (text) => {
    expect(parsePairingLink(text)).toBeNull();
  });
});
