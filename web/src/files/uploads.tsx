import {
  fingerprint,
  sha256,
  UploadQueue,
  type ConflictAnswer,
  type ConflictChoice,
  type ConflictQuestion,
  type QueuedUpload,
  type TusTransport,
  type UploadSource,
} from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { api, ApiError, call, currentAccessToken, refreshAccessToken } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Button, Check, useErrorText } from "../components/ui.tsx";
import { formatBytes } from "./format.ts";

/** A file picked or dropped, with the folders it sat in, relative to what was chosen. */
export interface PickedFile {
  readonly file: File;
  readonly folders: readonly string[];
}

interface UploadsValue {
  readonly items: readonly QueuedUpload[];
  readonly upload: (files: readonly PickedFile[], parentId: string) => Promise<void>;
  readonly queue: UploadQueue;
}

const UploadsContext = createContext<UploadsValue | null>(null);

export function useUploads(): UploadsValue {
  const value = useContext(UploadsContext);
  if (!value) {
    throw new Error("useUploads must be used inside UploadsProvider");
  }

  return value;
}

const RESUME_KEY = "atrium.uploads";

/** An unfinished upload: where it is on the server, and a fingerprint of the file's content. */
interface ResumePoint {
  readonly location: string;
  readonly fingerprint?: string;
}

/**
 * Where unfinished uploads were, so choosing the same file again after a
 * reload continues it. An upload continues only when folder, name, size,
 * modification time and a fingerprint of the content all match: a
 * different file must start over, never continue another file's bytes.
 */
const resumeStore = {
  key: (parentId: string, file: { name: string; size: number; lastModified?: number | undefined }) =>
    `${parentId}|${file.name}|${file.size}|${file.lastModified ?? 0}`,
  read(): Record<string, ResumePoint> {
    try {
      const raw = JSON.parse(localStorage.getItem(RESUME_KEY) ?? "{}") as Record<string, unknown>;
      // Entries from before fingerprints existed are dropped: they cannot be checked.
      return Object.fromEntries(
        Object.entries(raw).filter((e): e is [string, ResumePoint] => typeof e[1] === "object" && e[1] !== null),
      );
    } catch {
      return {};
    }
  },
  write(map: Record<string, ResumePoint>): void {
    try {
      localStorage.setItem(RESUME_KEY, JSON.stringify(map));
    } catch {
      // Without storage an interrupted upload starts over; nothing else is lost.
    }
  },
};

function fileSource(file: File): UploadSource {
  return {
    name: file.name,
    size: file.size,
    lastModified: file.lastModified,
    slice: async (start, end) => new Uint8Array(await file.slice(start, end).arrayBuffer()),
  };
}

const transport: TusTransport = {
  baseUrl: "",
  fetch: (input, init) => window.fetch(input, init),
  getAccessToken: currentAccessToken,
  refresh: refreshAccessToken,
  // The browser's own SHA-256 exists only on HTTPS and localhost; on plain
  // HTTP the shared implementation checks each chunk instead.
  sha256: globalThis.crypto?.subtle
    ? async (data: Uint8Array) => new Uint8Array(await crypto.subtle.digest("SHA-256", data as BufferSource))
    : (data: Uint8Array) => Promise.resolve(sha256(data)),
};

const fingerprintOf = (file: File) => fingerprint(file.size, fileSource(file).slice);

/**
 * Holds the app's upload queue for as long as the page is open, so
 * uploads continue while the user moves between folders and screens.
 */
export function UploadsProvider({ children }: { readonly children: ReactNode }) {
  const queryClient = useQueryClient();
  const [items, setItems] = useState<readonly QueuedUpload[]>([]);
  const [question, setQuestion] = useState<{ q: ConflictQuestion; answer: (a: ConflictAnswer) => void } | null>(null);
  const finished = useRef(new Set<number>());
  // The files behind queue items, to fingerprint them once their upload exists.
  const fileById = useRef(new Map<number, File>());
  const fingerprinting = useRef(new Set<number>());

  const queue = useMemo(
    () =>
      new UploadQueue({
        transport,
        concurrency: 2,
        resolveConflict: (q) => new Promise<ConflictAnswer>((answer) => setQuestion({ q, answer })),
        onChange: (list) => setItems(list),
      }),
    [],
  );

  // Remember where each upload is, refresh folders when files land in them.
  useEffect(() => {
    const map = new Map(Object.entries(resumeStore.read()));
    for (const item of items) {
      const key = resumeStore.key(item.parentId, item);
      const state = item.status.state;
      const location = item.location;
      if (location && ["uploading", "paused", "waiting"].includes(state)) {
        const known = map.get(key);
        if (known?.location !== location) {
          map.set(key, { location });
        }

        const file = fileById.current.get(item.id);
        if (!known?.fingerprint && file && !fingerprinting.current.has(item.id)) {
          fingerprinting.current.add(item.id);
          void fingerprintOf(file).then((print) => {
            const latest = new Map(Object.entries(resumeStore.read()));
            if (latest.get(key)?.location === location) {
              latest.set(key, { location, fingerprint: print });
              resumeStore.write(Object.fromEntries(latest));
            }
          });
        }
      } else if (["completed", "failed", "cancelled", "skipped"].includes(state)) {
        map.delete(key);
        fileById.current.delete(item.id);
      }

      if (state === "completed" && !finished.current.has(item.id)) {
        finished.current.add(item.id);
        void queryClient.invalidateQueries({ queryKey: ["children", item.parentId] });
        void queryClient.invalidateQueries({ queryKey: ["usage"] });
      }
    }

    resumeStore.write(Object.fromEntries(map));
  }, [items, queryClient]);

  // Leaving the page with uploads running asks first.
  useEffect(() => {
    const busy = items.some((i) => ["queued", "uploading", "waiting"].includes(i.status.state));
    if (!busy) {
      return;
    }

    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [items]);

  const value = useMemo<UploadsValue>(
    () => ({
      items,
      queue,
      upload: async (files, parentId) => {
        queue.forgetAnswers();
        const folders = await ensureFolders(files, parentId);
        const byFolder = new Map<string, File[]>();
        for (const picked of files) {
          const target = folders.get(picked.folders.join("/")) ?? parentId;
          byFolder.set(target, [...(byFolder.get(target) ?? []), picked.file]);
        }

        const saved = resumeStore.read();
        for (const [target, list] of byFolder) {
          // Continue only an upload whose file is provably the same one.
          const resumable = new Map<File, string>();
          for (const file of list) {
            const point = saved[resumeStore.key(target, file)];
            if (point?.fingerprint && point.fingerprint === (await fingerprintOf(file))) {
              resumable.set(file, point.location);
            }
          }

          const before = queue.items.length;
          const sources = list.map(fileSource);
          queue.add(sources, target, (source) => resumable.get(list[sources.indexOf(source)]!));
          queue.items.slice(before).forEach((item, index) => fileById.current.set(item.id, list[index]!));
        }
      },
    }),
    [items, queue],
  );

  return (
    <UploadsContext.Provider value={value}>
      {children}
      <UploadPanel />
      {question ? (
        <ConflictDialog
          question={question.q}
          onAnswer={(answer) => {
            question.answer(answer);
            setQuestion(null);
          }}
        />
      ) : null}
    </UploadsContext.Provider>
  );
}

/**
 * Creates the folders a dropped or picked folder contains, parents first,
 * and returns their IDs by relative path. A folder that already exists is
 * used as it is: uploading a folder again adds to it.
 */
async function ensureFolders(files: readonly PickedFile[], parentId: string): Promise<Map<string, string>> {
  const ids = new Map<string, string>([["", parentId]]);
  const paths = [...new Set(files.flatMap((f) => f.folders.map((_, i) => f.folders.slice(0, i + 1).join("/"))))].sort(
    (a, b) => a.split("/").length - b.split("/").length,
  );
  for (const path of paths) {
    const parts = path.split("/");
    const parent = ids.get(parts.slice(0, -1).join("/"))!;
    const name = parts[parts.length - 1]!;
    try {
      const folder = await call(api.POST("/api/v1/nodes", { body: { parentId: parent, name } }));
      ids.set(path, folder.id);
    } catch (error) {
      if (
        error instanceof ApiError &&
        error.problem.code === "node.name_conflict" &&
        error.problem.params["existingKind"] === "Folder"
      ) {
        ids.set(path, String(error.problem.params["existingId"]));
      } else {
        throw error;
      }
    }
  }

  return ids;
}

function ConflictDialog({
  question,
  onAnswer,
}: {
  readonly question: ConflictQuestion;
  readonly onAnswer: (a: ConflictAnswer) => void;
}) {
  const { t } = useTranslation();
  const [all, setAll] = useState(false);
  const answer = (choice: ConflictChoice) => onAnswer({ choice, applyToAll: all });
  return (
    <Dialog
      title={t("uploads.conflictTitle")}
      onClose={() => answer("skip")}
      actions={
        <>
          <Button variant="ghost" onClick={() => answer("skip")}>
            {t("uploads.skip")}
          </Button>
          {question.existingIsFolder ? null : (
            <Button onClick={() => answer("replace")} icon="history">
              {t("uploads.replace")}
            </Button>
          )}
          <Button variant="primary" onClick={() => answer("rename")} icon="copy">
            {t("uploads.keepBoth")}
          </Button>
        </>
      }
    >
      <p>{t("uploads.conflictBody", { name: question.name })}</p>
      {question.existingIsFolder ? null : <p className="muted">{t("uploads.replaceHelp")}</p>}
      <Check label={t("uploads.applyToAll")} checked={all} onChange={(e) => setAll(e.currentTarget.checked)} />
    </Dialog>
  );
}

/** The list of uploads in a corner of the screen, with progress and controls. */
function UploadPanel() {
  const { t } = useTranslation();
  const errorText = useErrorText();
  const { items, queue } = useUploads();
  const [collapsed, setCollapsed] = useState(false);
  if (items.length === 0) {
    return null;
  }

  const active = items.filter((i) => ["queued", "uploading", "waiting", "paused"].includes(i.status.state)).length;
  return (
    <section className="upload-panel" aria-label={t("uploads.title")}>
      <header className="upload-panel-header">
        <h2 className="upload-panel-title">
          {active > 0 ? t("uploads.inProgress", { count: active }) : t("uploads.done")}
        </h2>
        {active === 0 ? (
          <Button size="sm" variant="ghost" onClick={() => queue.clearFinished()}>
            {t("uploads.clear")}
          </Button>
        ) : null}
        <button
          type="button"
          className="icon-button"
          aria-expanded={!collapsed}
          aria-label={collapsed ? t("uploads.expand") : t("uploads.collapse")}
          onClick={() => setCollapsed((c) => !c)}
        >
          <Icon name="chevronDown" size={18} />
        </button>
      </header>
      {collapsed ? null : (
        <ul className="upload-list">
          {items.map((item) => {
            const status = item.status;
            const offset = "offset" in status ? status.offset : status.state === "completed" ? item.size : 0;
            const percent = item.size === 0 ? 100 : Math.floor((offset / item.size) * 100);
            return (
              <li key={item.id} className={`upload-item is-${status.state}`}>
                <Icon name="file" size={18} />
                <div className="upload-main">
                  <p className="upload-name">{item.name}</p>
                  <p className="upload-meta">
                    {status.state === "failed"
                      ? errorText(new ApiError({ code: status.code, params: status.params, status: 0 }))
                      : t(`uploads.state.${status.state}`, { percent, size: formatBytes(item.size) })}
                  </p>
                  {["uploading", "waiting", "paused"].includes(status.state) ? (
                    <progress className="upload-progress" max={100} value={percent} aria-label={item.name} />
                  ) : null}
                </div>
                <div className="upload-actions">
                  {status.state === "uploading" || status.state === "waiting" ? (
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("uploads.pause")}
                      onClick={() => queue.pause(item.id)}
                    >
                      <Icon name="pause" size={16} />
                    </button>
                  ) : null}
                  {status.state === "paused" ? (
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("uploads.resume")}
                      onClick={() => queue.resume(item.id)}
                    >
                      <Icon name="play" size={16} />
                    </button>
                  ) : null}
                  {["queued", "uploading", "waiting", "paused"].includes(status.state) ? (
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={t("uploads.cancel")}
                      onClick={() => void queue.cancel(item.id)}
                    >
                      <Icon name="close" size={16} />
                    </button>
                  ) : null}
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

/**
 * The files of a drop: plain files, and folders read through the
 * browser's file system entries with their relative paths.
 */
export async function filesFromDrop(transfer: DataTransfer): Promise<PickedFile[]> {
  const entries = [...transfer.items]
    .map((item) => (item.kind === "file" ? item.webkitGetAsEntry() : null))
    .filter((e): e is FileSystemEntry => e !== null);
  if (entries.length === 0) {
    return [...transfer.files].map((file) => ({ file, folders: [] }));
  }

  const result: PickedFile[] = [];
  const walk = async (entry: FileSystemEntry, folders: string[]): Promise<void> => {
    if (entry.isFile) {
      const file = await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject));
      result.push({ file, folders });
      return;
    }

    const reader = (entry as FileSystemDirectoryEntry).createReader();
    // A directory is read in batches until an empty one comes back.
    for (;;) {
      const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
      if (batch.length === 0) {
        break;
      }

      for (const child of batch) {
        await walk(child, [...folders, entry.name]);
      }
    }
  };

  for (const entry of entries) {
    await walk(entry, []);
  }

  return result;
}

/** The files of an <input type=file>, with folders when a folder was chosen. */
export function filesFromInput(list: FileList): PickedFile[] {
  return [...list].map((file) => {
    const parts = (file.webkitRelativePath || file.name).split("/");
    return { file, folders: parts.slice(0, -1) };
  });
}
