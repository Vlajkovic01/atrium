import {
  UploadQueue,
  uploadReports,
  sha256,
  type ConflictAnswer,
  type ConflictQuestion,
  type QueuedUpload,
  type TusTransport,
} from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";

import { useActiveServer } from "../api.ts";
import { useNotificationActions } from "../notifications.ts";
import { settings } from "../platform/storage";
import { fileSource, removeCopy, stillThere, type Picked } from "./source";

/** An upload not finished yet, as remembered on the device to continue after the app was closed. */
interface Saved extends Picked {
  readonly parentId: string;
  readonly location: string | null;
}

interface UploadsValue {
  readonly items: readonly QueuedUpload[];
  readonly add: (files: readonly Picked[], parentId: string) => void;
  readonly pause: (id: number) => void;
  readonly resume: (id: number) => void;
  readonly cancel: (id: number) => void;
  readonly clearFinished: () => void;
  /** A taken name waiting for the person's answer. */
  readonly question: ConflictQuestion | null;
  readonly answer: (answer: ConflictAnswer) => void;
}

const UploadsContext = createContext<UploadsValue | null>(null);

export function useUploads(): UploadsValue {
  const value = useContext(UploadsContext);
  if (!value) {
    throw new Error("useUploads must be used inside UploadsProvider");
  }

  return value;
}

const OVER = new Set(["completed", "failed", "cancelled", "skipped"]);

/**
 * The uploads to the server in front (docs/UX.md, Mobile, Files): a few
 * at a time through the shared tus queue, each continuing after a lost
 * connection. What is not finished is remembered with its place on the
 * server, and continues when the app is opened again, from the copy the
 * picker made in the app's cache. A copy is removed once it is on the
 * server, or is no longer wanted.
 */
export function UploadsProvider({ children }: { readonly children: ReactNode }) {
  const { server, session } = useActiveServer();
  const queryClient = useQueryClient();
  const [items, setItems] = useState<readonly QueuedUpload[]>([]);
  const [question, setQuestion] = useState<ConflictQuestion | null>(null);
  const answering = useRef<((answer: ConflictAnswer) => void) | null>(null);
  const picked = useRef(new Map<number, Saved>());
  const reported = useRef(new Set<number>());
  const notify = useNotificationActions();
  const key = `atrium.uploads.${server.id}`;

  const queue = useMemo(() => {
    const transport: TusTransport = {
      baseUrl: server.url,
      // Called as a plain function: a browser's fetch refuses any other `this`.
      fetch: (input, init) => fetch(input, init),
      getAccessToken: session.getAccessToken,
      refresh: session.refresh,
      sha256: (data) => Promise.resolve(sha256(data)),
    };

    return new UploadQueue({
      transport,
      resolveConflict: (q) =>
        new Promise<ConflictAnswer>((resolve) => {
          answering.current = resolve;
          setQuestion(q);
        }),
      onChange: setItems,
    });
  }, [server.url, session]);

  // Each change: finished files leave the device and show in their folder; the rest is remembered.
  useEffect(() => {
    const unfinished: Saved[] = [];
    const landedIn = new Set<string>();
    for (const item of items) {
      const copy = picked.current.get(item.id);
      if (!copy) {
        continue;
      }

      if (OVER.has(item.status.state)) {
        removeCopy(copy);
        picked.current.delete(item.id);
        if (item.status.state === "completed") {
          landedIn.add(item.parentId);
        }
      } else {
        unfinished.push({ ...copy, location: item.location });
      }
    }

    void settings.set(key, JSON.stringify(unfinished)).catch(() => undefined);
    const { reports, ids } = uploadReports(items, reported.current);
    ids.forEach((id) => reported.current.add(id));
    for (const report of reports) {
      void notify.add(report.kind, report.values, report.link).catch(() => undefined);
    }

    for (const parentId of landedIn) {
      void queryClient.invalidateQueries({ queryKey: [server.id, "children", parentId] });
    }

    if (landedIn.size > 0) {
      void queryClient.invalidateQueries({ queryKey: [server.id, "timeline"] });
    }
  }, [items, key, notify, queryClient, server.id]);

  const add = (files: readonly Picked[], parentId: string, location?: string | null): void => {
    const before = queue.items.length;
    queue.forgetAnswers();
    queue.add(files.map(fileSource), parentId, location ? () => location : undefined);
    queue.items.slice(before).forEach((item, index) => {
      const file = files[index];
      if (file) {
        picked.current.set(item.id, { ...file, parentId, location: location ?? null });
      }
    });
    setItems(queue.items);
  };

  // What was not finished when the app last closed continues, if its copy is still there.
  useEffect(() => {
    let cancelled = false;
    void settings
      .get(key)
      .then((text) => {
        if (cancelled || !text) {
          return;
        }

        const saved = JSON.parse(text) as Saved[];
        for (const entry of Array.isArray(saved) ? saved : []) {
          if (stillThere(entry)) {
            add([entry], entry.parentId, entry.location);
          }
        }
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
    // Once per server: the queue is that server's.
  }, [queue]);

  const value: UploadsValue = {
    items,
    add: (files, parentId) => add(files, parentId),
    pause: (id) => queue.pause(id),
    resume: (id) => queue.resume(id),
    cancel: (id) => void queue.cancel(id),
    clearFinished: () => queue.clearFinished(),
    question,
    answer: (a) => {
      setQuestion(null);
      answering.current?.(a);
      answering.current = null;
    },
  };

  return <UploadsContext.Provider value={value}>{children}</UploadsContext.Provider>;
}
