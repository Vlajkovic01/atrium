import { describe, expect, it } from "vitest";

import { createApiClient, errorMessageKeys, toApiProblem } from "../src/api/client.ts";

interface Seen {
  readonly url: string;
  readonly authorization: string | null;
  readonly body: string;
}

/** A stand-in server: answers each request with whatever the handler says, and records it. */
function fakeServer(handler: (seen: Seen, count: number) => Response | Promise<Response>) {
  const requests: Seen[] = [];
  const fetch: typeof globalThis.fetch = async (input, init) => {
    const request = new Request(input, init);
    const seen: Seen = {
      url: new URL(request.url).pathname,
      authorization: request.headers.get("Authorization"),
      body: await request.text(),
    };
    requests.push(seen);
    return handler(seen, requests.length);
  };

  return { fetch, requests };
}

const json = (status: number, body: unknown): Response =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const BASE = "http://atrium.test";

describe("API client", () => {
  it("sends the access token with each request", async () => {
    const server = fakeServer(() => json(200, []));
    const client = createApiClient({
      baseUrl: BASE,
      fetch: server.fetch,
      getAccessToken: () => "token-1",
      refresh: () => Promise.resolve(null),
    });

    await client.GET("/api/v1/sessions");

    expect(server.requests).toEqual([{ url: "/api/v1/sessions", authorization: "Bearer token-1", body: "" }]);
  });

  it("sends no Authorization header when signed out", async () => {
    const server = fakeServer(() => json(200, {}));
    const client = createApiClient({
      baseUrl: BASE,
      fetch: server.fetch,
      getAccessToken: () => null,
      refresh: () => Promise.resolve(null),
    });

    await client.GET("/api/v1/system/info");

    expect(server.requests[0]?.authorization).toBeNull();
  });

  it("refreshes an expired token once and repeats the request with its body", async () => {
    let token = "expired";
    let refreshes = 0;
    const server = fakeServer((seen) =>
      seen.authorization === "Bearer fresh" ? json(200, { id: "1" }) : json(401, {}),
    );
    const client = createApiClient({
      baseUrl: BASE,
      fetch: server.fetch,
      getAccessToken: () => token,
      refresh: () => {
        refreshes++;
        token = "fresh";
        return Promise.resolve(token);
      },
    });

    const { response } = await client.PATCH("/api/v1/me", { body: { displayName: "Ana", locale: null } });

    expect(response.status).toBe(200);
    expect(refreshes).toBe(1);
    expect(server.requests.map((r) => r.authorization)).toEqual(["Bearer expired", "Bearer fresh"]);
    // The repeated request carries the same body as the first.
    expect(server.requests[1]?.body).toBe(server.requests[0]?.body);
    expect(server.requests[1]?.body).toContain("Ana");
  });

  it("shares one refresh between requests that fail at the same moment", async () => {
    let token = "expired";
    let refreshes = 0;
    const server = fakeServer((seen) => (seen.authorization === "Bearer fresh" ? json(200, []) : json(401, {})));
    const client = createApiClient({
      baseUrl: BASE,
      fetch: server.fetch,
      getAccessToken: () => token,
      refresh: async () => {
        refreshes++;
        await new Promise((resolve) => setTimeout(resolve, 10));
        token = "fresh";
        return token;
      },
    });

    const results = await Promise.all([
      client.GET("/api/v1/sessions"),
      client.GET("/api/v1/sessions"),
      client.GET("/api/v1/sessions"),
    ]);

    // A refresh token works only once, so three refreshes would sign the user out.
    expect(refreshes).toBe(1);
    expect(results.map((r) => r.response.status)).toEqual([200, 200, 200]);
  });

  it("gives up after one retry instead of looping", async () => {
    let refreshes = 0;
    const server = fakeServer(() => json(401, {}));
    const client = createApiClient({
      baseUrl: BASE,
      fetch: server.fetch,
      getAccessToken: () => "still-bad",
      refresh: () => {
        refreshes++;
        return Promise.resolve("still-bad");
      },
    });

    const { response } = await client.GET("/api/v1/sessions");

    expect(response.status).toBe(401);
    expect(refreshes).toBe(1);
    expect(server.requests).toHaveLength(2);
  });

  it("returns the 401 when the session cannot be refreshed", async () => {
    const server = fakeServer(() => json(401, {}));
    const client = createApiClient({
      baseUrl: BASE,
      fetch: server.fetch,
      getAccessToken: () => "expired",
      refresh: () => Promise.resolve(null),
    });

    const { response } = await client.GET("/api/v1/sessions");

    expect(response.status).toBe(401);
    expect(server.requests).toHaveLength(1);
  });

  it.each([
    "/api/v1/auth/login",
    "/api/v1/auth/login/totp",
    "/api/v1/auth/refresh",
    "/api/v1/auth/link-codes/redeem",
    "/api/v1/setup/admin",
  ] as const)("does not treat a 401 from %s as an expired token", async (path) => {
    let refreshes = 0;
    const server = fakeServer(() => json(401, { code: "auth.invalid_credentials", params: {} }));
    const client = createApiClient({
      baseUrl: BASE,
      fetch: server.fetch,
      getAccessToken: () => null,
      refresh: () => {
        refreshes++;
        return Promise.resolve("token");
      },
    });

    // The body does not matter here; only the path and the status do.
    const { response } = await client.POST(path, { body: {} as never });

    expect(response.status).toBe(401);
    expect(refreshes).toBe(0);
    expect(server.requests).toHaveLength(1);
  });
});

describe("errors", () => {
  it("reads the code and parameters from the server's error format", () => {
    expect(toApiProblem({ code: "auth.too_many_attempts", params: { retryAfterSeconds: 30 } }, 429)).toEqual({
      code: "auth.too_many_attempts",
      params: { retryAfterSeconds: 30 },
      status: 429,
    });
  });

  it.each([undefined, null, "text", 42, {}, { code: 5 }])("falls back to unknown for %j", (body) => {
    expect(toApiProblem(body, 500)).toEqual({ code: "unknown", params: {}, status: 500 });
  });

  it("offers the specific message first and the general one as a fallback", () => {
    expect(errorMessageKeys("storage.root_missing")).toEqual(["errors.storage.root_missing", "errors.unknown"]);
  });
});
