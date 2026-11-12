/**
 * A tus 1.0.0 client for Atrium's upload endpoint (D-024, D-028). It works
 * the same in the browser and in React Native: everything platform-specific
 * comes in through `fetch`, the source of the bytes and the hash function.
 */

/** The bytes of one file, read one slice at a time so a large file is never held in memory. */
export interface UploadSource {
  readonly name: string;
  readonly size: number;
  /** Milliseconds since the Unix epoch, when the platform knows it. */
  readonly lastModified?: number;
  slice(start: number, end: number): Promise<Uint8Array>;
}

export type ConflictPolicy = "fail" | "rename" | "replace";

export type UploadStatus =
  | { readonly state: "queued" }
  | { readonly state: "uploading"; readonly offset: number }
  | { readonly state: "waiting"; readonly offset: number; readonly retryAt: number }
  | { readonly state: "paused"; readonly offset: number }
  | { readonly state: "completed"; readonly nodeId: string }
  | { readonly state: "failed"; readonly code: string; readonly params: Readonly<Record<string, unknown>> }
  | { readonly state: "cancelled" };

export interface TusTransport {
  /** Server address without a trailing slash; empty for the page's own origin. */
  readonly baseUrl: string;
  readonly fetch: typeof globalThis.fetch;
  readonly getAccessToken: () => string | null;
  /** Gets a new access token after a 401, or null when the session is over. */
  readonly refresh: () => Promise<string | null>;
  /**
   * SHA-256 of a chunk, sent so the server can refuse a chunk damaged on
   * the way. Optional: browsers offer it only on HTTPS and localhost. The
   * server verifies the whole file's content either way.
   */
  readonly sha256?: (data: Uint8Array) => Promise<Uint8Array>;
  /** Waits, so tests can replace real time. */
  readonly sleep?: (ms: number) => Promise<void>;
  readonly now?: () => number;
}

export interface UploadRequest {
  readonly source: UploadSource;
  readonly parentId: string;
  readonly onConflict: ConflictPolicy;
  /** Where an earlier attempt left off, to resume instead of starting over. */
  readonly location?: string;
}

/** Errors that end an upload: retrying cannot help. */
const FINAL_STATUSES = new Set([400, 403, 404, 409, 410, 413, 415, 422, 507]);

const MAX_ATTEMPTS = 8;
const MAX_BACKOFF_MS = 30_000;

/** One upload. Call `start`; follow it with `onChange`; `pause`, `resume` and `cancel` at any time. */
export class TusUpload {
  #status: UploadStatus = { state: "queued" };
  #location: string | null;
  #chunkSize = 16 * 1024 * 1024;
  #pauseRequested = false;
  #cancelRequested = false;
  #running: Promise<void> | null = null;
  readonly #listeners = new Set<(status: UploadStatus) => void>();
  readonly #transport: TusTransport;
  readonly #request: UploadRequest;

  constructor(transport: TusTransport, request: UploadRequest) {
    this.#transport = transport;
    this.#request = request;
    this.#location = request.location ?? null;
  }

  get status(): UploadStatus {
    return this.#status;
  }

  /** The server's address for this upload once it exists; keep it to resume after a reload. */
  get location(): string | null {
    return this.#location;
  }

  get source(): UploadSource {
    return this.#request.source;
  }

  onChange(listener: (status: UploadStatus) => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  /** Runs the upload to an end state. Starting twice returns the same run. */
  start(): Promise<void> {
    this.#pauseRequested = false;
    this.#running ??= this.#run().finally(() => {
      this.#running = null;
    });
    return this.#running;
  }

  /** Stops after the chunk in flight; `resume` continues from the server's offset. */
  pause(): void {
    this.#pauseRequested = true;
  }

  resume(): Promise<void> {
    return this.start();
  }

  /** Stops and tells the server to discard what it received. */
  async cancel(): Promise<void> {
    this.#cancelRequested = true;
    await this.#running?.catch(() => undefined);
    if (this.#location && this.#status.state !== "completed") {
      await this.#send(this.#location, { method: "DELETE" }).catch(() => undefined);
    }

    if (this.#status.state !== "completed") {
      this.#set({ state: "cancelled" });
    }
  }

  async #run(): Promise<void> {
    const sleep = this.#transport.sleep ?? ((ms: number) => new Promise((resolve) => setTimeout(resolve, ms)));
    const now = this.#transport.now ?? Date.now;
    let failures = 0;
    let offsetAtFailure = -1;
    for (;;) {
      if (this.#cancelRequested || this.#isFinal()) {
        return;
      }

      try {
        const offset = this.#location ? await this.#offset() : await this.#create();
        if (offset === null || this.#isFinal()) {
          return;
        }

        await this.#sendChunks(offset);
        return;
      } catch (error) {
        if (error instanceof PauseSignal) {
          return;
        }

        if (error instanceof FinalError) {
          this.#set({ state: "failed", code: error.code, params: error.params });
          return;
        }

        // A dropped connection or a busy server: wait, ask where to continue, go on.
        // Progress since the last failure means the connection works again.
        const reached = "offset" in this.#status ? this.#status.offset : 0;
        failures = reached > offsetAtFailure ? 1 : failures + 1;
        offsetAtFailure = reached;
        if (failures >= MAX_ATTEMPTS) {
          this.#set({ state: "failed", code: "network", params: {} });
          return;
        }

        const delay = Math.min(MAX_BACKOFF_MS, 1000 * 2 ** (failures - 1));
        const offset = "offset" in this.#status ? this.#status.offset : 0;
        this.#set({ state: "waiting", offset, retryAt: now() + delay });
        await sleep(delay);
      }
    }
  }

  #isFinal(): boolean {
    return ["completed", "failed", "cancelled"].includes(this.#status.state);
  }

  async #create(): Promise<number | null> {
    const source = this.#request.source;
    const metadata: Record<string, string> = {
      parentId: this.#request.parentId,
      name: source.name,
      onConflict: this.#request.onConflict,
    };
    if (source.lastModified !== undefined) {
      metadata["modifiedAt"] = String(Math.trunc(source.lastModified));
    }

    const response = await this.#send(`${this.#transport.baseUrl}/api/v1/uploads`, {
      method: "POST",
      headers: { "Upload-Length": String(source.size), "Upload-Metadata": encodeMetadata(metadata) },
    });
    await this.#throwIfFailed(response);
    const location = response.headers.get("Location");
    if (!location) {
      throw new FinalError("unknown", {});
    }

    this.#location = location.startsWith("http") ? location : `${this.#transport.baseUrl}${location}`;
    return this.#afterResponse(response);
  }

  async #offset(): Promise<number | null> {
    const response = await this.#send(this.#location!, { method: "HEAD" });
    if (response.status === 404 || response.status === 410) {
      throw new FinalError("upload.not_active", {});
    }

    await this.#throwIfFailed(response);
    return this.#afterResponse(response);
  }

  /** Reads the offset, chunk size and, once finished, the new file from a response. */
  #afterResponse(response: Response): number | null {
    const chunk = Number(response.headers.get("Atrium-Chunk-Size"));
    if (Number.isFinite(chunk) && chunk > 0) {
      this.#chunkSize = chunk;
    }

    const nodeId = response.headers.get("Atrium-Node-Id");
    if (nodeId) {
      this.#set({ state: "completed", nodeId });
      return null;
    }

    const offset = Number(response.headers.get("Upload-Offset") ?? "0");
    this.#set({ state: "uploading", offset });
    return offset;
  }

  async #sendChunks(start: number): Promise<void> {
    const source = this.#request.source;
    let offset = start;
    while (offset < source.size) {
      if (this.#cancelRequested) {
        return;
      }

      if (this.#pauseRequested) {
        this.#set({ state: "paused", offset });
        throw new PauseSignal();
      }

      const end = Math.min(source.size, offset + this.#chunkSize);
      const chunk = await source.slice(offset, end);
      const headers: Record<string, string> = {
        "Content-Type": "application/offset+octet-stream",
        "Upload-Offset": String(offset),
      };
      if (this.#transport.sha256) {
        headers["Upload-Checksum"] = `sha256 ${base64(await this.#transport.sha256(chunk))}`;
      }

      const response = await this.#send(this.#location!, { method: "PATCH", headers, body: chunk as BodyInit });

      if (response.status === 409 || response.status === 460) {
        // Out of step, or damaged on the way: the server's offset decides.
        const problem = await readProblem(response);
        if (
          problem.code !== "upload.offset_mismatch" &&
          problem.code !== "upload.checksum_mismatch" &&
          problem.code !== "upload.busy"
        ) {
          throw new FinalError(problem.code, problem.params);
        }

        throw new RetrySignal();
      }

      await this.#throwIfFailed(response);
      const next = this.#afterResponse(response);
      if (next === null) {
        return;
      }

      offset = next;
    }
  }

  async #throwIfFailed(response: Response): Promise<void> {
    if (response.ok) {
      return;
    }

    if (FINAL_STATUSES.has(response.status)) {
      const problem = await readProblem(response);
      throw new FinalError(problem.code, problem.params);
    }

    throw new RetrySignal();
  }

  /** Sends a tus request with the access token, refreshing it once on 401. */
  async #send(url: string, init: RequestInit): Promise<Response> {
    const attempt = (token: string | null) => {
      const headers = new Headers(init.headers);
      headers.set("Tus-Resumable", "1.0.0");
      if (token) {
        headers.set("Authorization", `Bearer ${token}`);
      }

      return this.#transport.fetch(url, { ...init, headers });
    };

    const response = await attempt(this.#transport.getAccessToken());
    if (response.status !== 401) {
      return response;
    }

    const token = await this.#transport.refresh();
    if (!token) {
      throw new FinalError("auth.invalid_refresh_token", {});
    }

    return attempt(token);
  }

  #set(status: UploadStatus): void {
    this.#status = status;
    for (const listener of this.#listeners) {
      listener(status);
    }
  }
}

class FinalError extends Error {
  readonly code: string;
  readonly params: Readonly<Record<string, unknown>>;

  constructor(code: string, params: Readonly<Record<string, unknown>>) {
    super(code);
    this.code = code;
    this.params = params;
  }
}

class RetrySignal extends Error {}

class PauseSignal extends Error {}

async function readProblem(response: Response): Promise<{ code: string; params: Record<string, unknown> }> {
  try {
    const body = (await response.json()) as { code?: unknown; params?: unknown };
    if (typeof body.code === "string") {
      return {
        code: body.code,
        params: typeof body.params === "object" && body.params ? (body.params as Record<string, unknown>) : {},
      };
    }
  } catch {
    // Not JSON: fall through to a generic code.
  }

  return { code: response.status === 507 ? "files.quota_exceeded" : "unknown", params: {} };
}

/** tus metadata: comma-separated pairs of a key and a Base64 value. */
export function encodeMetadata(values: Readonly<Record<string, string>>): string {
  return Object.entries(values)
    .map(([key, value]) => `${key} ${base64(new TextEncoder().encode(value))}`)
    .join(",");
}

function base64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary);
}
