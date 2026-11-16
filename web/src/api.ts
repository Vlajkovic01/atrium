import { createApiClient, type components } from "@atrium/shared";

export type TokenResponse = components["schemas"]["TokenResponse"];
export type UserSummary = components["schemas"]["UserSummary"];

/** What this browser tells the server about itself when it signs in. */
export const thisDevice = {
  name: describeBrowser(),
  kind: "web",
  platform: navigator.platform || null,
  appVersion: null,
} as const;

function describeBrowser(): string {
  const agent = navigator.userAgent;
  const browser = /Firefox\//.test(agent)
    ? "Firefox"
    : /Edg\//.test(agent)
      ? "Edge"
      : /Chrome\//.test(agent)
        ? "Chrome"
        : /Safari\//.test(agent)
          ? "Safari"
          : "Browser";
  const system = /Windows/.test(agent)
    ? "Windows"
    : /Android/.test(agent)
      ? "Android"
      : /iPhone|iPad/.test(agent)
        ? "iOS"
        : /Mac OS X/.test(agent)
          ? "macOS"
          : /Linux/.test(agent)
            ? "Linux"
            : "";
  return system ? `${browser} on ${system}` : browser;
}

// The access token lives only in this variable. It is never written to
// storage a script could read later; the refresh token is an HttpOnly
// cookie the page cannot see at all (DESIGN.md 6.2).
let accessToken: string | null = null;
let onSignedOut: () => void = () => {};

export function setAccessToken(token: string | null): void {
  accessToken = token;
}

/** Called when the session ends on the server's side, so the app can show the sign-in page. */
export function setSignedOutHandler(handler: () => void): void {
  onSignedOut = handler;
}

/** Exchanges the refresh cookie for a new access token. Null when there is no session. */
export async function refreshSession(): Promise<TokenResponse | null> {
  try {
    const response = await fetch("/api/v1/auth/refresh", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: null }),
    });
    if (!response.ok) {
      return null;
    }

    const tokens = (await response.json()) as TokenResponse;
    accessToken = tokens.accessToken;
    return tokens;
  } catch {
    return null;
  }
}

/** The access token for requests made outside the API client, such as uploads. */
export function currentAccessToken(): string | null {
  return accessToken;
}

/** Gets a new access token after a 401; signs the app out when the session is over. */
export async function refreshAccessToken(): Promise<string | null> {
  const tokens = await refreshSession();
  if (!tokens) {
    accessToken = null;
    onSignedOut();
  }

  return tokens?.accessToken ?? null;
}

export const api = createApiClient({
  baseUrl: "",
  getAccessToken: () => accessToken,
  refresh: refreshAccessToken,
});

// Shared with the mobile app, so both report errors the same way.
export { ApiError, call } from "@atrium/shared";
