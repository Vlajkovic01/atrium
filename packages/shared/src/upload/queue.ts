import { TusUpload, type ConflictPolicy, type TusTransport, type UploadSource, type UploadStatus } from "./tus.ts";

/** What the user decided for a name that is already taken. */
export type ConflictChoice = Exclude<ConflictPolicy, "fail"> | "skip";

export interface ConflictQuestion {
  readonly name: string;
  readonly parentId: string;
  /** The item in the way: replacing is offered only for a file. */
  readonly existingIsFolder: boolean;
}

/** The answer, and whether it applies to every later conflict of this batch. */
export interface ConflictAnswer {
  readonly choice: ConflictChoice;
  readonly applyToAll: boolean;
}

export interface QueuedUpload {
  readonly id: number;
  readonly name: string;
  readonly size: number;
  /** With name, size and folder, identifies the same file chosen again for resuming. */
  readonly lastModified: number | undefined;
  readonly parentId: string;
  readonly status: UploadStatus | { readonly state: "skipped" };
  /** Saved by the app to offer resuming after a reload. */
  readonly location: string | null;
}

export interface UploadQueueOptions {
  readonly transport: TusTransport;
  /** How many files are sent at the same time. Two keeps a slow line responsive. */
  readonly concurrency?: number;
  /** Asks the user what to do with a taken name. */
  readonly resolveConflict: (question: ConflictQuestion) => Promise<ConflictAnswer>;
  /** Called after every change, with the whole list. */
  readonly onChange?: (items: readonly QueuedUpload[]) => void;
}

interface Entry {
  readonly id: number;
  readonly source: UploadSource;
  readonly parentId: string;
  upload: TusUpload;
  skipped: boolean;
  /** A taken name is being decided: the upload is not over, it waits for the answer. */
  deciding: boolean;
}

/**
 * The uploads of one app: a few at a time, in the order they were added,
 * each resumable. A taken name is asked about once; "apply to all" answers
 * the rest of the batch the same way.
 */
export class UploadQueue {
  readonly #options: UploadQueueOptions;
  readonly #entries: Entry[] = [];
  #nextId = 1;
  #active = 0;
  #standingAnswer: ConflictChoice | null = null;
  #asking: Promise<ConflictAnswer> | null = null;

  constructor(options: UploadQueueOptions) {
    this.#options = options;
  }

  get items(): readonly QueuedUpload[] {
    return this.#entries.map((e) => ({
      id: e.id,
      name: e.source.name,
      size: e.source.size,
      lastModified: e.source.lastModified,
      parentId: e.parentId,
      status: e.skipped ? { state: "skipped" as const } : e.deciding ? { state: "queued" as const } : e.upload.status,
      location: e.upload.location,
    }));
  }

  /**
   * Adds files to the queue. A file that was being uploaded before (same
   * name, size and target, with its saved location) continues there.
   */
  add(
    sources: readonly UploadSource[],
    parentId: string,
    resumeFrom?: (source: UploadSource) => string | undefined,
  ): void {
    for (const source of sources) {
      const location = resumeFrom?.(source);
      const upload = new TusUpload(this.#options.transport, {
        source,
        parentId,
        onConflict: this.#standingAnswer && this.#standingAnswer !== "skip" ? this.#standingAnswer : "fail",
        ...(location ? { location } : {}),
      });
      const entry: Entry = { id: this.#nextId++, source, parentId, upload, skipped: false, deciding: false };
      this.#watch(entry);
      this.#entries.push(entry);
    }

    this.#notify();
    this.#pump();
  }

  /** Starts a new batch: the next conflict is asked about again. */
  forgetAnswers(): void {
    this.#standingAnswer = null;
  }

  pause(id: number): void {
    this.#find(id)?.upload.pause();
  }

  resume(id: number): void {
    const entry = this.#find(id);
    if (entry && entry.upload.status.state === "paused") {
      this.#run(entry);
    }
  }

  async cancel(id: number): Promise<void> {
    await this.#find(id)?.upload.cancel();
    this.#notify();
  }

  /** Removes finished, failed, skipped and cancelled items from the list. */
  clearFinished(): void {
    for (let i = this.#entries.length - 1; i >= 0; i--) {
      const entry = this.#entries[i]!;
      if (entry.skipped || ["completed", "failed", "cancelled"].includes(entry.upload.status.state)) {
        this.#entries.splice(i, 1);
      }
    }

    this.#notify();
  }

  #find(id: number): Entry | undefined {
    return this.#entries.find((e) => e.id === id);
  }

  #watch(entry: Entry): void {
    const upload = entry.upload;
    upload.onChange((status) => {
      // A taken name is a question, not the end: the item stays pending
      // until the user answers, so nobody sees "finished" in between.
      if (upload === entry.upload && status.state === "failed" && status.code === "node.name_conflict") {
        entry.deciding = true;
      }

      this.#notify();
    });
  }

  #pump(): void {
    const limit = this.#options.concurrency ?? 2;
    for (const entry of this.#entries) {
      if (this.#active >= limit) {
        return;
      }

      if (!entry.skipped && entry.upload.status.state === "queued") {
        this.#run(entry);
      }
    }
  }

  #run(entry: Entry): void {
    this.#active++;
    void entry.upload
      .start()
      .then(() => this.#afterRun(entry))
      .finally(() => {
        this.#active--;
        this.#notify();
        this.#pump();
      });
  }

  async #afterRun(entry: Entry): Promise<void> {
    const status = entry.upload.status;
    if (status.state !== "failed" || status.code !== "node.name_conflict") {
      return;
    }

    const choice = await this.#decide(entry, status.params["existingKind"] === "Folder");
    if (choice === "skip") {
      entry.skipped = true;
      entry.deciding = false;
      return;
    }

    entry.upload = new TusUpload(this.#options.transport, {
      source: entry.source,
      parentId: entry.parentId,
      onConflict: choice,
    });
    entry.deciding = false;
    this.#watch(entry);
    await entry.upload.start();
  }

  async #decide(entry: Entry, existingIsFolder: boolean): Promise<ConflictChoice> {
    if (this.#standingAnswer && !(this.#standingAnswer === "replace" && existingIsFolder)) {
      return this.#standingAnswer;
    }

    // One question at a time, even when several uploads hit a conflict together.
    while (this.#asking) {
      await this.#asking;
      if (this.#standingAnswer && !(this.#standingAnswer === "replace" && existingIsFolder)) {
        return this.#standingAnswer;
      }
    }

    this.#asking = this.#options.resolveConflict({
      name: entry.source.name,
      parentId: entry.parentId,
      existingIsFolder,
    });
    try {
      const answer = await this.#asking;
      if (answer.applyToAll) {
        this.#standingAnswer = answer.choice;
      }

      return answer.choice === "replace" && existingIsFolder ? "rename" : answer.choice;
    } finally {
      this.#asking = null;
    }
  }

  #notify(): void {
    this.#options.onChange?.(this.items);
  }
}
