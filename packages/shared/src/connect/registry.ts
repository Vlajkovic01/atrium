import { ApiError, call } from "../api/call.ts";
import { createApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";
import { ServerSession, type SecretStore, type TokenResponse } from "./session.ts";

export type DeviceInfo = components["schemas"]["DeviceRequest"];
export type UserSummary = components["schemas"]["UserSummary"];

/** Plain settings kept on the device. Nothing secret goes here. */
export interface KeyValueStore {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
}

/** A server the app is signed in to. */
export interface KnownServer {
  readonly id: string;
  /** Scheme, host, port and path, without a trailing slash. */
  readonly url: string;
  readonly user: UserSummary;
  readonly sessionId: string;
  /** Milliseconds since the Unix epoch. */
  readonly addedAt: number;
}

export interface ServerRegistryOptions {
  readonly store: KeyValueStore;
  readonly secrets: SecretStore;
  readonly fetch?: typeof globalThis.fetch;
  readonly now?: () => number;
}

interface Saved {
  readonly servers: readonly KnownServer[];
  readonly activeId: string | null;
}

const STORE_KEY = "atrium.servers";

/**
 * The servers one app is signed in to, each with its own session, and
 * which of them is in front (docs/UX.md, Mobile, Connect). The list is
 * kept in plain storage; every refresh token in the secret store.
 */
export class ServerRegistry {
  readonly #options: ServerRegistryOptions;
  readonly #sessions = new Map<string, ServerSession>();
  readonly #listeners = new Set<() => void>();
  #servers: KnownServer[];
  #activeId: string | null;

  private constructor(options: ServerRegistryOptions, saved: Saved) {
    this.#options = options;
    this.#servers = [...saved.servers];
    this.#activeId = saved.activeId;
  }

  static async load(options: ServerRegistryOptions): Promise<ServerRegistry> {
    return new ServerRegistry(options, readSaved(await options.store.get(STORE_KEY)));
  }

  get servers(): readonly KnownServer[] {
    return this.#servers;
  }

  /** The server in front, or null when the app is not connected to any. */
  get active(): KnownServer | null {
    return this.#servers.find((s) => s.id === this.#activeId) ?? null;
  }

  /** Called after every change of the list or of the server in front. */
  onChange(listener: () => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  /** The session of a known server. One object per server, so its requests share one token refresh. */
  session(id: string): ServerSession {
    const existing = this.#sessions.get(id);
    if (existing) {
      return existing;
    }

    const server = this.#servers.find((s) => s.id === id);
    if (!server) {
      throw new Error(`No server ${id} is known to this app.`);
    }

    const session = new ServerSession({
      serverId: id,
      baseUrl: server.url,
      secrets: this.#options.secrets,
      ...(this.#options.fetch ? { fetch: this.#options.fetch } : {}),
      // The server ended it: the entry would only lead to errors.
      onEnded: () => void this.#drop(id),
    });
    this.#sessions.set(id, session);
    return session;
  }

  /**
   * Adds a server after a sign-in and puts it in front. Signing in again
   * to the same server as the same person replaces the earlier entry
   * instead of listing it twice.
   */
  async add(url: string, tokens: TokenResponse): Promise<KnownServer> {
    const earlier = this.#servers.find((s) => s.url === url && s.user.id === tokens.user.id);
    const server: KnownServer = {
      id: earlier?.id ?? newId(),
      url,
      user: tokens.user,
      sessionId: tokens.sessionId,
      addedAt: earlier?.addedAt ?? (this.#options.now ?? Date.now)(),
    };
    this.#servers = [...this.#servers.filter((s) => s.id !== server.id), server];
    this.#sessions.delete(server.id);
    await this.session(server.id).adopt(tokens);
    this.#activeId = server.id;
    await this.#save();
    return server;
  }

  async setActive(id: string): Promise<void> {
    if (this.#activeId !== id && this.#servers.some((s) => s.id === id)) {
      this.#activeId = id;
      await this.#save();
    }
  }

  /** Signs out of a server and removes it. Another server, if any, comes to the front. */
  async remove(id: string): Promise<void> {
    if (this.#servers.some((s) => s.id === id)) {
      await this.session(id).signOut();
      await this.#drop(id);
    }
  }

  async #drop(id: string): Promise<void> {
    if (!this.#servers.some((s) => s.id === id)) {
      return;
    }

    this.#servers = this.#servers.filter((s) => s.id !== id);
    this.#sessions.delete(id);
    if (this.#activeId === id) {
      this.#activeId = this.#servers[0]?.id ?? null;
    }

    await this.#save();
  }

  async #save(): Promise<void> {
    const saved: Saved = { servers: this.#servers, activeId: this.#activeId };
    await this.#options.store.set(STORE_KEY, JSON.stringify(saved));
    for (const listener of this.#listeners) {
      listener();
    }
  }
}

function readSaved(text: string | null): Saved {
  if (text) {
    try {
      const value = JSON.parse(text) as Partial<Saved>;
      if (Array.isArray(value.servers)) {
        const servers = value.servers.filter(
          (s): s is KnownServer =>
            typeof s?.id === "string" && typeof s.url === "string" && typeof s.user?.id === "string",
        );
        const activeId = servers.some((s) => s.id === value.activeId) ? value.activeId! : (servers[0]?.id ?? null);
        return { servers, activeId };
      }
    } catch {
      // Unreadable settings are treated as none: the person connects again.
    }
  }

  return { servers: [], activeId: null };
}

// Names an entry on this device only; it is not a secret.
function newId(): string {
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`;
}

/** The outcome of a password sign-in: signed in, or a second step is needed. */
export type PasswordSignIn =
  | { readonly status: "signedIn"; readonly tokens: TokenResponse }
  | { readonly status: "totpRequired"; readonly challenge: string };

/**
 * Signing in to a server the app does not know yet. Each call throws an
 * `ApiError` with the server's code, or "network" when it did not answer.
 */
export function signInTo(url: string, device: DeviceInfo, fetch: typeof globalThis.fetch = globalThis.fetch) {
  const api = createApiClient({
    baseUrl: url,
    fetch,
    getAccessToken: () => null,
    refresh: () => Promise.resolve(null),
  });

  return {
    /** Exchanges the single-use code of a scanned QR code for a session. No password is typed. */
    async withLinkCode(code: string): Promise<TokenResponse> {
      return call(api.POST("/api/v1/auth/link-codes/redeem", { body: { code, device } }));
    },

    async withPassword(name: string, password: string): Promise<PasswordSignIn> {
      const result = await call(api.POST("/api/v1/auth/login", { body: { name, password, device } }));
      if (result.status === "totpRequired" && result.totpChallenge) {
        return { status: "totpRequired", challenge: result.totpChallenge };
      }

      return { status: "signedIn", tokens: requireTokens(result.tokens) };
    },

    /** The second step: a code from the authenticator app, or a recovery code. */
    async withTotp(challenge: string, code: string): Promise<TokenResponse> {
      const result = await call(api.POST("/api/v1/auth/login/totp", { body: { challenge, code, device } }));
      return requireTokens(result.tokens);
    },
  };
}

function requireTokens(tokens: TokenResponse | null): TokenResponse {
  if (!tokens) {
    throw new ApiError({ code: "unknown", params: {}, status: 200 });
  }

  return tokens;
}
