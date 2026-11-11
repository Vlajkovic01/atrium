import createClient, { type Client, type Middleware } from "openapi-fetch";

import type { paths } from "./schema.d.ts";

export type ApiClient = Client<paths>;

export interface ApiClientOptions {
  /** Server address without a trailing slash; empty for the same origin as the page. */
  readonly baseUrl: string;

  /** The current access token, or null when signed out. */
  readonly getAccessToken: () => string | null;

  /**
   * Gets a new access token with the refresh token, or null if the session
   * is over. Called when a request comes back 401.
   */
  readonly refresh: () => Promise<string | null>;

  readonly fetch?: typeof globalThis.fetch;
}

// Requests to these paths carry their own credentials. A 401 from them
// means "wrong password" or "bad refresh token", not "token expired".
const NO_RETRY = [
  "/api/v1/auth/login",
  "/api/v1/auth/login/totp",
  "/api/v1/auth/refresh",
  "/api/v1/auth/link-codes/redeem",
  "/api/v1/setup/admin",
];

/**
 * The API client every Atrium app uses. It adds the access token to each
 * request and, when the server says the token expired, refreshes it once
 * and repeats the request. Several requests failing at the same moment
 * share one refresh, because a refresh token can be used only once.
 */
export function createApiClient(options: ApiClientOptions): ApiClient {
  const send = options.fetch ?? globalThis.fetch;
  const client = createClient<paths>({ baseUrl: options.baseUrl, fetch: send });

  // A request's body can be read only once, so a copy is kept for the retry.
  const copies = new Map<string, Request>();
  let refreshing: Promise<string | null> | null = null;

  const refreshOnce = (): Promise<string | null> => {
    refreshing ??= options.refresh().finally(() => {
      refreshing = null;
    });
    return refreshing;
  };

  const auth: Middleware = {
    onRequest({ request, id }) {
      const token = options.getAccessToken();
      if (token) {
        request.headers.set("Authorization", `Bearer ${token}`);
      }

      copies.set(id, request.clone());
      return request;
    },

    async onResponse({ request, response, id }) {
      const copy = copies.get(id);
      copies.delete(id);
      const path = new URL(request.url).pathname;
      if (response.status !== 401 || !copy || NO_RETRY.some((p) => path.endsWith(p))) {
        return response;
      }

      const token = await refreshOnce();
      if (!token) {
        return response;
      }

      copy.headers.set("Authorization", `Bearer ${token}`);
      return send(copy);
    },

    onError({ id }) {
      copies.delete(id);
    },
  };

  client.use(auth);
  return client;
}

/** The server's error format: a stable code and parameters, never display text. */
export interface ApiProblem {
  readonly code: string;
  readonly params: Readonly<Record<string, unknown>>;
  readonly status: number;
}

/** Reads the error code out of an error response body, whatever shape arrived. */
export function toApiProblem(error: unknown, status: number): ApiProblem {
  if (typeof error === "object" && error !== null) {
    const body = error as { code?: unknown; params?: unknown };
    if (typeof body.code === "string") {
      const params = typeof body.params === "object" && body.params !== null ? body.params : {};
      return { code: body.code, params: params as Record<string, unknown>, status };
    }
  }

  return { code: "unknown", params: {}, status };
}

/**
 * The translation key for an error code. Codes are dotted
 * ("auth.invalid_credentials") and live under "errors" in the
 * translation files; an unknown code falls back to a general message.
 */
export function errorMessageKeys(code: string): readonly string[] {
  return [`errors.${code}`, "errors.unknown"];
}
