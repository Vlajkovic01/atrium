import { describe, expect, it } from "vitest";

import {
  ApiError,
  ServerRegistry,
  ServerSession,
  compareVersions,
  probeServer,
  signInTo,
  type KeyValueStore,
  type SecretStore,
  type TokenResponse,
} from "../src/index.ts";

const json = (status: number, body: unknown): Response =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const problem = (status: number, code: string): Response =>
  new Response(JSON.stringify({ code, params: {} }), {
    status,
    headers: { "Content-Type": "application/problem+json" },
  });

function memory(): KeyValueStore & SecretStore & { readonly values: Map<string, string> } {
  const values = new Map<string, string>();
  return {
    values,
    get: (key) => Promise.resolve(values.get(key) ?? null),
    set: (key, value) => Promise.resolve(void values.set(key, value)),
    delete: (key) => Promise.resolve(void values.delete(key)),
  };
}

const tokens = (user: string, n: number): TokenResponse => ({
  accessToken: `access-${user}-${n}`,
  expiresInSeconds: 900,
  refreshToken: `refresh-${user}-${n}`,
  sessionId: `session-${user}`,
  user: { id: `id-${user}`, name: user, displayName: user, role: "user", locale: "en" },
});

/**
 * A stand-in for Atrium servers at several addresses. Each rotates refresh
 * tokens the way the real one does: a token works once.
 */
function fakeServers() {
  const valid = new Map<string, { user: string; n: number }>();
  const seen: { url: string; authorization: string | null; body: string }[] = [];
  let down = false;
  const fetch: typeof globalThis.fetch = async (input, init) => {
    const request = new Request(input, init);
    const url = new URL(request.url);
    const body = await request.text();
    seen.push({ url: request.url, authorization: request.headers.get("Authorization"), body });
    if (down) {
      throw new TypeError("Network request failed");
    }

    if (url.pathname.endsWith("/auth/refresh")) {
      const presented = (JSON.parse(body) as { refreshToken: string }).refreshToken;
      const key = `${url.origin}|${presented}`;
      const entry = valid.get(key);
      if (!entry) {
        return problem(401, "auth.invalid_refresh_token");
      }

      valid.delete(key);
      const next = tokens(entry.user, entry.n + 1);
      valid.set(`${url.origin}|${next.refreshToken}`, { user: entry.user, n: entry.n + 1 });
      return json(200, next);
    }

    if (url.pathname.endsWith("/auth/logout")) {
      return new Response(null, { status: 204 });
    }

    if (url.pathname.endsWith("/sessions")) {
      return /^Bearer access-\w+-[2-9]$/.test(request.headers.get("Authorization") ?? "")
        ? json(200, [])
        : problem(401, "auth.token_expired");
    }

    return problem(404, "resource.not_found");
  };

  return {
    fetch,
    seen,
    /** Signs a person in at an address, as the server would. */
    issue(origin: string, user: string): TokenResponse {
      const issued = tokens(user, 1);
      valid.set(`${origin}|${issued.refreshToken}`, { user, n: 1 });
      return issued;
    },
    revokeAll: () => valid.clear(),
    setDown: (value: boolean) => {
      down = value;
    },
  };
}

const HOME = "http://192.168.1.20:8080";
const CLOUD = "https://cloud.example.com";

describe("probing a server", () => {
  const info = { name: "Atrium", version: "0.4.0", minClientVersion: "0.1.0", setupComplete: true };

  it("accepts an Atrium server this app can use", async () => {
    const asked: string[] = [];
    const result = await probeServer(HOME, "0.1.0", (input) => {
      asked.push(String(input));
      return Promise.resolve(json(200, info));
    });

    expect(result).toEqual({ ok: true, info });
    expect(asked).toEqual([`${HOME}/api/v1/system/info`]);
  });

  it.each([
    ["a server that is not set up yet", json(200, { ...info, setupComplete: false }), "setup_incomplete"],
    ["a server that needs a newer app", json(200, { ...info, minClientVersion: "0.2.0" }), "client_too_old"],
    ["another product's JSON", json(200, { name: "Router", version: "1" }), "not_atrium"],
    ["a web page", new Response("<html></html>", { status: 200 }), "not_atrium"],
    ["an error page", new Response("nope", { status: 404 }), "not_atrium"],
  ])("refuses %s", async (_, response, reason) => {
    expect(await probeServer(HOME, "0.1.0", () => Promise.resolve(response))).toMatchObject({ ok: false, reason });
  });

  it("says when nothing answers", async () => {
    const result = await probeServer(HOME, "0.1.0", () => Promise.reject(new TypeError("Network request failed")));

    expect(result).toEqual({ ok: false, reason: "unreachable" });
  });

  it("gives up on a server that never answers", async () => {
    const never: typeof globalThis.fetch = (_, init) =>
      new Promise((_resolve, reject) => {
        init?.signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
      });

    expect(await probeServer(HOME, "0.1.0", never, 20)).toEqual({ ok: false, reason: "unreachable" });
  });

  it("compares versions number by number", () => {
    expect(compareVersions("0.10.0", "0.9.5")).toBe(1);
    expect(compareVersions("1.0", "1.0.0")).toBe(0);
    expect(compareVersions("1.2.3-beta+5", "1.2.3")).toBe(0);
    expect(compareVersions("0.1.0", "0.1.1")).toBe(-1);
  });
});

describe("a session", () => {
  it("keeps the refresh token in the secret store and refreshes when the access token is refused", async () => {
    const servers = fakeServers();
    const secrets = memory();
    const session = new ServerSession({ serverId: "a", baseUrl: HOME, secrets, fetch: servers.fetch });
    await session.adopt(servers.issue(HOME, "ana"));

    const { response } = await session.api.GET("/api/v1/sessions");

    expect(response.status).toBe(200);
    expect(session.getAccessToken()).toBe("access-ana-2");
    expect([...secrets.values]).toEqual([["atrium.refresh.a", "refresh-ana-2"]]);
  });

  it("calls fetch as a plain function, as a browser's own fetch demands", async () => {
    const servers = fakeServers();
    // A browser's fetch throws "Illegal invocation" when called as a method of another object.
    const strict = function (this: unknown, ...args: Parameters<typeof globalThis.fetch>) {
      if (this !== undefined && this !== globalThis) {
        throw new TypeError("Illegal invocation");
      }

      return servers.fetch(...args);
    } as typeof globalThis.fetch;
    const session = new ServerSession({ serverId: "a", baseUrl: HOME, secrets: memory(), fetch: strict });
    await session.adopt(servers.issue(HOME, "ana"));

    expect(await session.refresh()).toBe("access-ana-2");
    expect((await session.api.GET("/api/v1/sessions")).response.status).toBe(200);
  });

  it("uses a refresh token once, however many requests need a new access token", async () => {
    const servers = fakeServers();
    const session = new ServerSession({ serverId: "a", baseUrl: HOME, secrets: memory(), fetch: servers.fetch });
    await session.adopt(servers.issue(HOME, "ana"));

    const results = await Promise.all([session.refresh(), session.refresh(), session.refresh()]);

    expect(results).toEqual(["access-ana-2", "access-ana-2", "access-ana-2"]);
    expect(servers.seen.filter((s) => s.url.endsWith("/auth/refresh"))).toHaveLength(1);
  });

  it("ends when the server refuses the refresh token, and forgets it", async () => {
    const servers = fakeServers();
    const secrets = memory();
    let ended = 0;
    const session = new ServerSession({
      serverId: "a",
      baseUrl: HOME,
      secrets,
      fetch: servers.fetch,
      onEnded: () => ended++,
    });
    await session.adopt(servers.issue(HOME, "ana"));
    servers.revokeAll();

    expect(await session.refresh()).toBeNull();
    expect(ended).toBe(1);
    expect(secrets.values.size).toBe(0);
    expect(session.getAccessToken()).toBeNull();
  });

  it("stays signed in when the server cannot be reached", async () => {
    const servers = fakeServers();
    const secrets = memory();
    let ended = 0;
    const session = new ServerSession({
      serverId: "a",
      baseUrl: HOME,
      secrets,
      fetch: servers.fetch,
      onEnded: () => ended++,
    });
    await session.adopt(servers.issue(HOME, "ana"));
    servers.setDown(true);

    expect(await session.refresh()).toBeNull();
    expect(ended).toBe(0);
    expect(secrets.values.get("atrium.refresh.a")).toBe("refresh-ana-1");

    servers.setDown(false);
    expect(await session.refresh()).toBe("access-ana-2");
  });

  it("refuses tokens without a refresh token, which only a browser session gets", async () => {
    const session = new ServerSession({ serverId: "a", baseUrl: HOME, secrets: memory() });

    await expect(session.adopt({ ...tokens("ana", 1), refreshToken: null })).rejects.toThrow();
  });
});

describe("the servers an app knows", () => {
  async function setup() {
    const servers = fakeServers();
    const store = memory();
    const secrets = memory();
    let now = 1000;
    const options = { store, secrets, fetch: servers.fetch, now: () => now++ };
    return { servers, store, secrets, options, registry: await ServerRegistry.load(options) };
  }

  it("starts empty", async () => {
    const { registry } = await setup();

    expect(registry.servers).toEqual([]);
    expect(registry.active).toBeNull();
  });

  it("holds several servers, each with its own session, and remembers them", async () => {
    const { servers, options, registry, secrets } = await setup();
    const home = await registry.add(HOME, servers.issue(HOME, "ana"));
    const cloud = await registry.add(CLOUD, servers.issue(CLOUD, "marko"));

    expect(registry.active?.id).toBe(cloud.id);
    expect(secrets.values.size).toBe(2);
    await registry.session(home.id).api.GET("/api/v1/sessions");
    await registry.session(cloud.id).api.GET("/api/v1/sessions");

    // Each server saw only its own person's tokens.
    const sent = (origin: string) =>
      servers.seen.filter((s) => s.url.startsWith(origin)).map((s) => `${s.authorization ?? ""}${s.body}`);
    expect(sent(HOME).join()).not.toMatch(/marko/);
    expect(sent(CLOUD).join()).not.toMatch(/ana/);

    await registry.setActive(home.id);
    const again = await ServerRegistry.load(options);
    expect(again.servers.map((s) => s.url)).toEqual([HOME, CLOUD]);
    expect(again.active?.url).toBe(HOME);
    expect(await again.session(cloud.id).refresh()).toMatch(/^access-marko-/);
  });

  it("keeps no token in plain storage", async () => {
    const { servers, registry, store } = await setup();
    await registry.add(HOME, servers.issue(HOME, "ana"));

    expect([...store.values.values()].join()).not.toMatch(/refresh-|access-/);
  });

  it("replaces the entry when the same person signs in to the same server again", async () => {
    const { servers, registry } = await setup();
    const first = await registry.add(HOME, servers.issue(HOME, "ana"));
    const second = await registry.add(HOME, servers.issue(HOME, "ana"));
    await registry.add(HOME, servers.issue(HOME, "marko"));

    expect(second.id).toBe(first.id);
    expect(registry.servers.map((s) => s.user.name)).toEqual(["ana", "marko"]);
  });

  it("signs out of a removed server and brings another to the front", async () => {
    const { servers, registry, secrets } = await setup();
    const home = await registry.add(HOME, servers.issue(HOME, "ana"));
    const cloud = await registry.add(CLOUD, servers.issue(CLOUD, "marko"));
    let changes = 0;
    registry.onChange(() => changes++);

    await registry.remove(cloud.id);

    expect(servers.seen.some((s) => s.url === `${CLOUD}/api/v1/auth/logout`)).toBe(true);
    expect(registry.servers.map((s) => s.id)).toEqual([home.id]);
    expect(registry.active?.id).toBe(home.id);
    expect([...secrets.values.keys()]).toEqual([`atrium.refresh.${home.id}`]);
    expect(changes).toBe(1);
  });

  it("removes a server even when it cannot be reached", async () => {
    const { servers, registry, secrets } = await setup();
    const home = await registry.add(HOME, servers.issue(HOME, "ana"));
    servers.setDown(true);

    await registry.remove(home.id);

    expect(registry.servers).toEqual([]);
    expect(secrets.values.size).toBe(0);
  });

  it("drops a server whose session the server ended", async () => {
    const { servers, registry } = await setup();
    const home = await registry.add(HOME, servers.issue(HOME, "ana"));
    servers.revokeAll();

    expect(await registry.session(home.id).refresh()).toBeNull();
    await Promise.resolve();

    expect(registry.servers).toEqual([]);
    expect(registry.active).toBeNull();
  });

  it("treats unreadable settings as no servers", async () => {
    const store = memory();
    store.values.set("atrium.servers", "{not json");

    expect((await ServerRegistry.load({ store, secrets: memory() })).servers).toEqual([]);
  });
});

describe("signing in to a new server", () => {
  const device = { name: "Phone", kind: "mobile", platform: "android", appVersion: "0.1.0" } as const;

  function server(handler: (path: string, body: Record<string, unknown>) => Response) {
    const fetch: typeof globalThis.fetch = async (input, init) => {
      const request = new Request(input, init);
      return handler(new URL(request.url).pathname, JSON.parse(await request.text()) as Record<string, unknown>);
    };
    return fetch;
  }

  it("exchanges a scanned code for a session, saying which device this is", async () => {
    let sent: Record<string, unknown> = {};
    const fetch = server((path, body) => {
      sent = { path, ...body };
      return json(200, tokens("ana", 1));
    });

    const result = await signInTo(HOME, device, fetch).withLinkCode("one-time");

    expect(result.user.name).toBe("ana");
    expect(sent).toEqual({ path: "/api/v1/auth/link-codes/redeem", code: "one-time", device });
  });

  it("signs in with a password, and with a second step when the account has one", async () => {
    const fetch = server((path, body) => {
      if (path.endsWith("/login")) {
        return body["name"] === "ana"
          ? json(200, { status: "totpRequired", tokens: null, totpChallenge: "challenge-1" })
          : json(200, { status: "signedIn", tokens: tokens("marko", 1), totpChallenge: null });
      }

      return body["challenge"] === "challenge-1" && body["code"] === "123456"
        ? json(200, { status: "signedIn", tokens: tokens("ana", 1), totpChallenge: null })
        : problem(401, "auth.invalid_credentials");
    });
    const signIn = signInTo(HOME, device, fetch);

    expect(await signIn.withPassword("marko", "pw")).toMatchObject({ status: "signedIn" });
    expect(await signIn.withPassword("ana", "pw")).toEqual({ status: "totpRequired", challenge: "challenge-1" });
    expect((await signIn.withTotp("challenge-1", "123456")).user.name).toBe("ana");
    await expect(signIn.withTotp("challenge-1", "000000")).rejects.toMatchObject({
      problem: { code: "auth.invalid_credentials", status: 401 },
    });
  });

  it("reports the server's error code, or that the server did not answer", async () => {
    const refused = signInTo(HOME, device, () => Promise.resolve(problem(401, "auth.invalid_credentials")));
    const offline = signInTo(HOME, device, () => Promise.reject(new TypeError("Network request failed")));

    await expect(refused.withLinkCode("used")).rejects.toBeInstanceOf(ApiError);
    await expect(offline.withPassword("ana", "pw")).rejects.toMatchObject({ problem: { code: "network" } });
  });
});
