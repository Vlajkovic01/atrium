import { describe, expect, it } from "vitest";

import { UploadQueue, type ConflictAnswer } from "../src/upload/queue.ts";
import { TusUpload, type TusTransport, type UploadSource, type UploadStatus } from "../src/upload/tus.ts";

const BASE = "http://atrium.test";
const sha256 = async (data: Uint8Array) => new Uint8Array(await crypto.subtle.digest("SHA-256", data as BufferSource));

interface StoredUpload {
  parentId: string;
  name: string;
  length: number;
  bytes: number[];
  policy: string;
  nodeId: string | null;
  cancelled: boolean;
}

/**
 * An in-memory tus server that behaves like Atrium's: a fixed chunk size,
 * a checksum on each chunk, the offset as the truth, and a name conflict
 * when the policy is "fail". Faults can be injected per request.
 */
function fakeTusServer(options: { chunkSize?: number; existing?: Map<string, "File" | "Folder"> } = {}) {
  const uploads = new Map<string, StoredUpload>();
  const files = new Map<string, number[]>();
  const existing = options.existing ?? new Map<string, "File" | "Folder">();
  let next = 1;
  let token = "token-1";
  const faults: ((request: Request) => "drop-before" | "drop-after" | "corrupt" | 503 | 401 | null)[] = [];

  const headers = (upload: StoredUpload) => {
    const h = new Headers({
      "Upload-Offset": String(upload.bytes.length),
      "Atrium-Chunk-Size": String(options.chunkSize ?? 4),
    });
    if (upload.nodeId) {
      h.set("Atrium-Node-Id", upload.nodeId);
    }

    return h;
  };

  const finish = (upload: StoredUpload) => {
    if (upload.bytes.length === upload.length) {
      upload.nodeId = `node-${upload.name}`;
      files.set(upload.name, [...upload.bytes]);
    }
  };

  const fetch: typeof globalThis.fetch = async (input, init) => {
    const request = new Request(input, init);
    const fault = faults.map((f) => f(request)).find((f) => f !== null) ?? null;
    if (fault === "drop-before") {
      throw new TypeError("network down");
    }

    if (fault === 503) {
      return new Response(null, { status: 503 });
    }

    if (request.headers.get("Authorization") !== `Bearer ${token}` || fault === 401) {
      return new Response(null, { status: 401 });
    }

    const url = new URL(request.url);
    if (request.method === "POST") {
      const metadata = Object.fromEntries(
        (request.headers.get("Upload-Metadata") ?? "").split(",").map((pair) => {
          const [key, value] = pair.split(" ");
          return [key, new TextDecoder().decode(Uint8Array.from(atob(value ?? ""), (c) => c.charCodeAt(0)))];
        }),
      ) as Record<string, string>;
      const name = metadata["name"]!;
      if (existing.has(name) && metadata["onConflict"] === "fail") {
        return Response.json(
          { code: "node.name_conflict", params: { existingId: "x", existingKind: existing.get(name) } },
          { status: 409 },
        );
      }

      const id = `u${next++}`;
      const upload: StoredUpload = {
        parentId: metadata["parentId"]!,
        name: existing.has(name) && metadata["onConflict"] === "rename" ? `${name} (2)` : name,
        length: Number(request.headers.get("Upload-Length")),
        bytes: [],
        policy: metadata["onConflict"]!,
        nodeId: null,
        cancelled: false,
      };
      uploads.set(id, upload);
      finish(upload);
      const h = headers(upload);
      h.set("Location", `/api/v1/uploads/${id}`);
      return new Response(null, { status: 201, headers: h });
    }

    const upload = uploads.get(url.pathname.split("/").pop()!);
    if (!upload) {
      return new Response(null, { status: 404 });
    }

    if (upload.cancelled) {
      return new Response(null, { status: 410 });
    }

    if (request.method === "HEAD") {
      return new Response(null, { status: 200, headers: headers(upload) });
    }

    if (request.method === "DELETE") {
      upload.cancelled = true;
      return new Response(null, { status: 204 });
    }

    const offset = Number(request.headers.get("Upload-Offset"));
    if (offset !== upload.bytes.length) {
      return Response.json({ code: "upload.offset_mismatch", params: {} }, { status: 409, headers: headers(upload) });
    }

    let body = new Uint8Array(await request.arrayBuffer());
    if (fault === "corrupt") {
      body = body.map((b, i) => (i === 0 ? b ^ 1 : b));
    }

    const checksum = request.headers.get("Upload-Checksum");
    const [algorithm, sum] = (checksum ?? "").split(" ");
    const actual = btoa(String.fromCharCode(...(await sha256(body))));
    if (checksum !== null && (algorithm !== "sha256" || sum !== actual)) {
      return Response.json({ code: "upload.checksum_mismatch", params: {} }, { status: 460 });
    }

    upload.bytes.push(...body);
    finish(upload);
    if (fault === "drop-after") {
      // Stored, but the client never hears about it.
      throw new TypeError("connection reset");
    }

    return new Response(null, { status: 204, headers: headers(upload) });
  };

  return {
    fetch,
    uploads,
    files,
    faults,
    rotateToken: (value: string) => {
      token = value;
    },
  };
}

function source(name: string, text: string): UploadSource {
  const bytes = new TextEncoder().encode(text);
  return {
    name,
    size: bytes.length,
    lastModified: 1_700_000_000_000,
    slice: (start, end) => Promise.resolve(bytes.slice(start, end)),
  };
}

function transport(server: ReturnType<typeof fakeTusServer>, overrides: Partial<TusTransport> = {}): TusTransport {
  let token = "token-1";
  return {
    baseUrl: BASE,
    fetch: server.fetch,
    getAccessToken: () => token,
    refresh: () => {
      token = "token-2";
      return Promise.resolve(token);
    },
    sha256,
    sleep: () => Promise.resolve(),
    ...overrides,
  };
}

const text = (server: ReturnType<typeof fakeTusServer>, name: string) =>
  new TextDecoder().decode(Uint8Array.from(server.files.get(name) ?? []));

describe("tus upload", () => {
  it("sends a file in the server's chunk size and ends with the new file's id", async () => {
    const server = fakeTusServer({ chunkSize: 4 });
    const upload = new TusUpload(transport(server), {
      source: source("a.txt", "hello, world"),
      parentId: "p",
      onConflict: "fail",
    });
    const seen: UploadStatus[] = [];
    upload.onChange((s) => seen.push(s));

    await upload.start();

    expect(upload.status).toEqual({ state: "completed", nodeId: "node-a.txt" });
    expect(text(server, "a.txt")).toBe("hello, world");
    expect(seen.filter((s) => s.state === "uploading").map((s) => ("offset" in s ? s.offset : -1))).toEqual([0, 4, 8]);
  });

  it("survives a connection that drops before or after a chunk was stored", async () => {
    const server = fakeTusServer({ chunkSize: 3 });
    let patches = 0;
    server.faults.push((request) => {
      if (request.method !== "PATCH") {
        return null;
      }

      patches++;
      return patches === 2 ? "drop-before" : patches === 4 ? "drop-after" : null;
    });
    const upload = new TusUpload(transport(server), {
      source: source("b.txt", "abcdefghijklmno"),
      parentId: "p",
      onConflict: "fail",
    });

    await upload.start();

    expect(upload.status.state).toBe("completed");
    expect(text(server, "b.txt")).toBe("abcdefghijklmno");
  });

  it("sends a chunk again when it arrived damaged", async () => {
    const server = fakeTusServer({ chunkSize: 4 });
    let first = true;
    server.faults.push((request) => {
      if (request.method === "PATCH" && first) {
        first = false;
        return "corrupt";
      }

      return null;
    });
    const upload = new TusUpload(transport(server), {
      source: source("c.txt", "12345678"),
      parentId: "p",
      onConflict: "fail",
    });

    await upload.start();

    expect(text(server, "c.txt")).toBe("12345678");
  });

  it("waits out a busy server and gives up only after repeated failures", async () => {
    const server = fakeTusServer();
    server.faults.push((request) => (request.method === "PATCH" ? 503 : null));
    const waits: number[] = [];
    const upload = new TusUpload(transport(server, { sleep: (ms) => (waits.push(ms), Promise.resolve()) }), {
      source: source("d.txt", "data"),
      parentId: "p",
      onConflict: "fail",
    });

    await upload.start();

    expect(upload.status).toEqual({ state: "failed", code: "network", params: {} });
    expect(waits).toEqual([1000, 2000, 4000, 8000, 16000, 30000, 30000]);
  });

  it("works without chunk checksums where the platform cannot hash", async () => {
    const server = fakeTusServer({ chunkSize: 4 });
    const plain = { ...transport(server) };
    delete (plain as { sha256?: unknown }).sha256;
    const upload = new TusUpload(plain, {
      source: source("plain.txt", "over plain http"),
      parentId: "p",
      onConflict: "fail",
    });

    await upload.start();

    expect(text(server, "plain.txt")).toBe("over plain http");
  });

  it("refreshes an expired token and carries on", async () => {
    const server = fakeTusServer();
    server.rotateToken("token-2");
    const upload = new TusUpload(transport(server), {
      source: source("e.txt", "data"),
      parentId: "p",
      onConflict: "fail",
    });

    await upload.start();

    expect(upload.status.state).toBe("completed");
  });

  it("resumes from a saved location after a reload", async () => {
    const server = fakeTusServer({ chunkSize: 2 });
    const first = new TusUpload(transport(server), {
      source: source("f.txt", "abcdef"),
      parentId: "p",
      onConflict: "fail",
    });
    first.onChange((s) => {
      if (s.state === "uploading" && s.offset === 2) {
        first.pause();
      }
    });
    await first.start();
    expect(first.status).toEqual({ state: "paused", offset: 2 });

    const again = new TusUpload(transport(server), {
      source: source("f.txt", "abcdef"),
      parentId: "p",
      onConflict: "fail",
      location: first.location!,
    });
    await again.start();

    expect(again.status.state).toBe("completed");
    expect(text(server, "f.txt")).toBe("abcdef");
    expect(server.uploads.size).toBe(1);
  });

  it("stops for good on an answer that retrying cannot change", async () => {
    const server = fakeTusServer({ existing: new Map([["g.txt", "File"]]) });
    const upload = new TusUpload(transport(server), {
      source: source("g.txt", "x"),
      parentId: "p",
      onConflict: "fail",
    });

    await upload.start();

    expect(upload.status).toMatchObject({
      state: "failed",
      code: "node.name_conflict",
      params: { existingKind: "File" },
    });
  });

  it("tells the server to discard a cancelled upload", async () => {
    const server = fakeTusServer({ chunkSize: 2 });
    const upload = new TusUpload(transport(server), {
      source: source("h.txt", "abcdef"),
      parentId: "p",
      onConflict: "fail",
    });
    upload.onChange((s) => {
      if (s.state === "uploading" && s.offset === 2) {
        upload.pause();
      }
    });
    await upload.start();

    await upload.cancel();

    expect(upload.status.state).toBe("cancelled");
    expect([...server.uploads.values()][0]!.cancelled).toBe(true);
  });
});

describe("upload queue", () => {
  it("asks once about a taken name when told to apply the answer to all", async () => {
    const server = fakeTusServer({
      existing: new Map([
        ["a.txt", "File"],
        ["b.txt", "File"],
      ]),
    });
    const questions: string[] = [];
    const queue = new UploadQueue({
      transport: transport(server),
      concurrency: 1,
      resolveConflict: (q): Promise<ConflictAnswer> => {
        questions.push(q.name);
        return Promise.resolve({ choice: "rename", applyToAll: true });
      },
    });

    queue.add([source("a.txt", "1"), source("b.txt", "2"), source("c.txt", "3")], "p");
    await waitFor(() => queue.items.every((i) => ["completed", "failed", "skipped"].includes(i.status.state)));

    expect(questions).toEqual(["a.txt"]);
    expect(text(server, "a.txt (2)")).toBe("1");
    expect(text(server, "b.txt (2)")).toBe("2");
    expect(text(server, "c.txt")).toBe("3");
  });

  it("keeps an item pending, not failed, while the user decides about a taken name", async () => {
    const server = fakeTusServer({ existing: new Map([["a.txt", "File"]]) });
    let answer: ((a: ConflictAnswer) => void) | null = null;
    const states: string[] = [];
    const queue = new UploadQueue({
      transport: transport(server),
      resolveConflict: () => new Promise<ConflictAnswer>((resolve) => (answer = resolve)),
      onChange: (items) => states.push(items[0]!.status.state),
    });

    queue.add([source("a.txt", "x")], "p");
    await waitFor(() => answer !== null);

    expect(states).not.toContain("failed");
    expect(queue.items[0]!.status.state).toBe("queued");
    answer!({ choice: "rename", applyToAll: false });
    await waitFor(() => queue.items[0]!.status.state === "completed");
  });

  it("skips a file the user chose to skip and never replaces a folder", async () => {
    const server = fakeTusServer({
      existing: new Map([
        ["docs", "Folder"],
        ["note.txt", "File"],
      ]),
    });
    const answers: ConflictAnswer[] = [
      { choice: "replace", applyToAll: false },
      { choice: "skip", applyToAll: false },
    ];
    const queue = new UploadQueue({
      transport: transport(server),
      concurrency: 1,
      resolveConflict: () => Promise.resolve(answers.shift()!),
    });

    queue.add([source("docs", "file named like a folder"), source("note.txt", "new")], "p");
    await waitFor(() => queue.items.every((i) => ["completed", "failed", "skipped"].includes(i.status.state)));

    expect(queue.items.map((i) => i.status.state)).toEqual(["completed", "skipped"]);
    expect(text(server, "docs (2)")).toBe("file named like a folder");
    expect(server.files.has("note.txt")).toBe(false);
  });
});

async function waitFor(condition: () => boolean, timeoutMs = 2000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error("Timed out waiting.");
    }

    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}
