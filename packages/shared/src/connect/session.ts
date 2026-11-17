import { createApiClient, type ApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";

export type TokenResponse = components["schemas"]["TokenResponse"];

/** Where secrets are kept: the Keychain on iOS, the Keystore on Android (DESIGN.md 6.2). */
export interface SecretStore {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  delete(key: string): Promise<void>;
}

export interface ServerSessionOptions {
  /** Names this session's secret; one per server the app knows. */
  readonly serverId: string;
  /** The server's address, without a trailing slash. */
  readonly baseUrl: string;
  readonly secrets: SecretStore;
  readonly fetch?: typeof globalThis.fetch;
  /** The server ended the session: signed out elsewhere, revoked, or the account was turned off. */
  readonly onEnded?: () => void;
}

/**
 * One signed-in session with one server, for apps that hold the refresh
 * token themselves. The access token lives in memory only; the refresh
 * token only in the secret store.
 */
export class ServerSession {
  readonly api: ApiClient;
  readonly baseUrl: string;
  readonly #options: ServerSessionOptions;
  readonly #fetch: typeof globalThis.fetch;
  #accessToken: string | null = null;
  #refreshing: Promise<string | null> | null = null;

  constructor(options: ServerSessionOptions) {
    this.#options = options;
    this.baseUrl = options.baseUrl;
    // Called as a plain function, never as a method of this object: a
    // browser's own fetch refuses to run with anything but the window as
    // its `this`, and the refresh would fail as if the server were away.
    const send = options.fetch ?? globalThis.fetch;
    this.#fetch = (input, init) => send(input, init);
    this.api = createApiClient({
      baseUrl: options.baseUrl,
      fetch: this.#fetch,
      getAccessToken: this.getAccessToken,
      refresh: this.refresh,
    });
  }

  /** The current access token, for requests made outside the API client, such as uploads. */
  readonly getAccessToken = (): string | null => this.#accessToken;

  /** Takes the tokens of a fresh sign-in. */
  async adopt(tokens: TokenResponse): Promise<void> {
    if (!tokens.refreshToken) {
      throw new Error("The server sent no refresh token; this client did not sign in as a device.");
    }

    await this.#options.secrets.set(this.#key, tokens.refreshToken);
    this.#accessToken = tokens.accessToken;
  }

  /**
   * Gets a new access token with the stored refresh token. Null when it
   * could not: either the session is over (then `onEnded` was called and
   * the secret is gone), or the server could not be reached (then nothing
   * changed and a later attempt may work). Calls made at the same moment
   * share one request, because a refresh token works once.
   */
  readonly refresh = (): Promise<string | null> => {
    this.#refreshing ??= this.#refresh().finally(() => {
      this.#refreshing = null;
    });
    return this.#refreshing;
  };

  /** Tells the server to end the session and forgets it here, even if the server could not be told. */
  async signOut(): Promise<void> {
    try {
      if (this.#accessToken ?? (await this.refresh())) {
        await this.api.POST("/api/v1/auth/logout");
      }
    } catch {
      // Not reachable: the session ends on the server when its refresh token expires.
    }

    await this.forget();
  }

  /** Forgets the session on this device without telling the server. */
  async forget(): Promise<void> {
    this.#accessToken = null;
    await this.#options.secrets.delete(this.#key);
  }

  get #key(): string {
    return `atrium.refresh.${this.#options.serverId}`;
  }

  async #refresh(): Promise<string | null> {
    const refreshToken = await this.#options.secrets.get(this.#key);
    if (!refreshToken) {
      this.#end();
      return null;
    }

    let response: Response;
    try {
      response = await this.#fetch(`${this.baseUrl}/api/v1/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken }),
      });
    } catch {
      return null;
    }

    if (response.status === 401) {
      await this.#options.secrets.delete(this.#key);
      this.#end();
      return null;
    }

    if (!response.ok) {
      return null;
    }

    const tokens = (await response.json()) as TokenResponse;

    // Saved before it is used. If the app dies between the server's answer
    // and this line, the old token is presented next time; the server
    // accepts the previous token for a minute, and after that ends the
    // session, which costs a sign-in but never leaks one.
    if (tokens.refreshToken) {
      await this.#options.secrets.set(this.#key, tokens.refreshToken);
    }

    this.#accessToken = tokens.accessToken;
    return tokens.accessToken;
  }

  #end(): void {
    this.#accessToken = null;
    this.#options.onEnded?.();
  }
}
