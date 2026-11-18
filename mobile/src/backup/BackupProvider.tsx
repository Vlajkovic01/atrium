import {
  ApiError,
  TusUpload,
  backupCounts,
  backupFolderNames,
  homeLibrary,
  planBackup,
  sha256,
  type BackupChoice,
  type BackupCounts,
  type BackupRecord,
} from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { AppState } from "react-native";

import { useActiveServer } from "../api.ts";
import { thisDevice } from "../device.ts";
import { localDb } from "../platform/db";
import { charging, hashAsset, listAssets, mediaAccess, onWifi, type MediaAccess } from "../platform/media";
import { settings } from "../platform/storage";
import { fileSource } from "../uploads/source";

/** What the person set for camera backup on this phone and this server. */
export interface BackupSettings extends BackupChoice {
  readonly enabled: boolean;
  readonly wifiOnly: boolean;
  readonly chargingOnly: boolean;
}

export const defaultBackupSettings: BackupSettings = {
  enabled: false,
  photos: true,
  videos: true,
  albums: null,
  wifiOnly: true,
  chargingOnly: false,
};

/** What camera backup is doing now, said as it is (docs/UX.md, Mobile, Camera backup). */
export type BackupActivity =
  | { readonly kind: "off" }
  | { readonly kind: "noAccess" }
  | { readonly kind: "waitingWifi" }
  | { readonly kind: "waitingCharging" }
  | { readonly kind: "reading" }
  | { readonly kind: "checking"; readonly name: string }
  | { readonly kind: "sending"; readonly name: string; readonly percent: number }
  | { readonly kind: "unreachable" }
  | { readonly kind: "idle" };

interface BackupValue {
  readonly settings: BackupSettings;
  readonly save: (next: BackupSettings) => Promise<void>;
  readonly access: MediaAccess | null;
  readonly counts: BackupCounts | null;
  readonly activity: BackupActivity;
  /** When a run last finished, in milliseconds, or null for never on this phone. */
  readonly lastRun: number | null;
  readonly failures: readonly { readonly name: string; readonly code: string }[];
  /** Runs now, as far as the conditions allow. */
  readonly runNow: () => void;
}

const BackupContext = createContext<BackupValue | null>(null);

export function useBackup(): BackupValue {
  const value = useContext(BackupContext);
  if (!value) {
    throw new Error("useBackup must be used inside BackupProvider");
  }

  return value;
}

/** While the app is open, a new look at the phone's photos this often. */
const EVERY_MS = 15 * 60_000;
/** Hashes are asked about in batches this size. */
const ASK = 100;

/**
 * Camera backup on the server in front (D-058): the phone's chosen photos
 * and videos go to "Camera backup / <this phone>" in the person's own
 * files. Each is known by its content (SHA-256), so what the server holds
 * already, from this phone or another, is never sent again, and a
 * reinstalled app does not start over. Nothing is ever deleted from the
 * phone. This part runs while the app is open; the background follows.
 */
export function BackupProvider({ children }: { readonly children: ReactNode }) {
  const { t } = useTranslation();
  const { server, session, files } = useActiveServer();
  const queryClient = useQueryClient();
  const key = `atrium.backup.${server.id}`;
  const [current, setCurrent] = useState<BackupSettings>(defaultBackupSettings);
  const [access, setAccess] = useState<MediaAccess | null>(null);
  const [counts, setCounts] = useState<BackupCounts | null>(null);
  const [activity, setActivity] = useState<BackupActivity>({ kind: "off" });
  const [lastRun, setLastRun] = useState<number | null>(null);
  const [failures, setFailures] = useState<readonly { name: string; code: string }[]>([]);
  const running = useRef(false);
  const latest = useRef(current);
  latest.current = current;

  /**
   * The folder the backup goes into. Once made, it is remembered, so a
   * change of the phone's language (which names it) never starts a second
   * one; it is made again only when it was removed or is in the trash.
   */
  const folder = useCallback(async (): Promise<string> => {
    const remembered = await settings.get(`${key}.folder`).catch(() => null);
    if (remembered) {
      try {
        const node = await files.node(remembered);
        if (!node.trashedAt) {
          return node.id;
        }
      } catch {
        // Gone: made again below.
      }
    }

    const made = await makeFolder();
    void settings.set(`${key}.folder`, made).catch(() => undefined);
    return made;
  }, [files, key]);

  const makeFolder = async (): Promise<string> => {
    const libraries = await files.libraries();
    const home = homeLibrary(libraries.filter((l) => l.kind === "personal"));
    if (!home) {
      throw new ApiError({ code: "backup.no_library", params: {}, status: 0 });
    }

    let parent = home.rootNodeId;
    for (const name of backupFolderNames(thisDevice().name, t("mobile.backup.folder"))) {
      const page = await files.children(parent, { sort: "name", descending: false, kind: "folder", limit: 1000 });
      const found = page.items.find((n) => n.name === name);
      if (found) {
        parent = found.id;
        continue;
      }

      try {
        parent = (await files.createFolder(parent, name)).id;
      } catch (e) {
        // Made meanwhile, by another run or device: use that one.
        const again = await files.children(parent, { sort: "name", descending: false, kind: "folder", limit: 1000 });
        const made = again.items.find((n) => n.name === name);
        if (!made) {
          throw e;
        }

        parent = made.id;
      }
    }

    return parent;
  };

  const run = useCallback(async (): Promise<void> => {
    const chosen = latest.current;
    if (running.current) {
      return;
    }

    if (!chosen.enabled) {
      setActivity({ kind: "off" });
      return;
    }

    running.current = true;
    try {
      const allowed = await mediaAccess();
      setAccess(allowed);
      if (allowed === "none" || allowed === "denied") {
        setActivity({ kind: "noAccess" });
        return;
      }

      setActivity({ kind: "reading" });
      const assets = await listAssets();
      const records = new Map((await localDb.backupRecords(server.id)).map((r) => [r.assetId, r]));
      const show = () => {
        setCounts(backupCounts(assets, [...records.values()], chosen));
        setFailures(
          [...records.values()]
            .filter((r) => r.error !== null)
            .map((r) => ({ name: assets.find((a) => a.id === r.assetId)?.name ?? r.assetId, code: r.error! })),
        );
      };
      show();

      const save = async (record: BackupRecord): Promise<void> => {
        records.set(record.assetId, record);
        await localDb.putBackupRecord(server.id, record);
        show();
      };

      // Conditions, checked before the work and between files.
      const blocked = async (): Promise<BackupActivity | null> => {
        if (chosen.wifiOnly && !(await onWifi())) {
          return { kind: "waitingWifi" };
        }

        if (chosen.chargingOnly && !(await charging())) {
          return { kind: "waitingCharging" };
        }

        return null;
      };

      const plan = planBackup(assets, [...records.values()], chosen);
      const byId = new Map(assets.map((a) => [a.id, a]));

      // Hashing is local work: it goes on without Wi-Fi, and makes the server's check cheap.
      for (const asset of plan.toHash) {
        setActivity({ kind: "checking", name: asset.name });
        const phoneAsset = byId.get(asset.id)!;
        try {
          await save({
            assetId: asset.id,
            size: asset.size,
            modifiedAt: asset.modifiedAt,
            sha256: await hashAsset(phoneAsset),
            done: false,
            error: null,
          });
        } catch {
          // Unreadable now (moved, being written): looked at again next time.
        }
      }

      const waiting = await blocked();
      if (waiting) {
        setActivity(waiting);
        return;
      }

      // What the server already holds, from this phone or any other, is not sent again.
      const pending = planBackup(assets, [...records.values()], chosen).toSend;
      for (let at = 0; at < pending.length; at += ASK) {
        const batch = pending.slice(at, at + ASK);
        const owned = await files.ownedHashes(batch.map((a) => records.get(a.id)!.sha256!));
        for (const asset of batch) {
          const record = records.get(asset.id)!;
          if (owned.has(record.sha256!)) {
            await save({ ...record, done: true });
          }
        }
      }

      const toSend = planBackup(assets, [...records.values()], chosen).toSend;
      if (toSend.length > 0) {
        const parentId = await folder();
        for (const asset of toSend) {
          const stop = await blocked();
          if (stop) {
            setActivity(stop);
            return;
          }

          const phoneAsset = byId.get(asset.id)!;
          const record = records.get(asset.id)!;
          const upload = new TusUpload(
            {
              baseUrl: server.url,
              fetch: (input, init) => fetch(input, init),
              getAccessToken: session.getAccessToken,
              refresh: session.refresh,
              sha256: (data) => Promise.resolve(sha256(data)),
            },
            {
              source: fileSource({
                uri: phoneAsset.uri,
                name: asset.name,
                size: asset.size,
                lastModified: asset.modifiedAt,
              }),
              parentId,
              // Another picture with the same name keeps both; the content decides what is the same.
              onConflict: "rename",
            },
          );
          const stopWatching = upload.onChange((status) => {
            if (status.state === "uploading" || status.state === "waiting") {
              setActivity({
                kind: "sending",
                name: asset.name,
                percent: asset.size === 0 ? 100 : Math.floor((status.offset / asset.size) * 100),
              });
            }
          });
          try {
            await upload.start();
          } finally {
            stopWatching();
          }

          const end = upload.status;
          if (end.state === "completed") {
            await save({ ...record, done: true, error: null });
          } else if (end.state === "failed") {
            if (end.code === "network") {
              setActivity({ kind: "unreachable" });
              return;
            }

            await save({ ...record, error: end.code });
          }
        }

        await queryClient.invalidateQueries({ queryKey: [server.id, "timeline"] });
      }

      const finished = Date.now();
      setLastRun(finished);
      void settings.set(`${key}.last`, String(finished)).catch(() => undefined);
      setActivity({ kind: "idle" });
    } catch (e) {
      setActivity(e instanceof ApiError && e.problem.code === "network" ? { kind: "unreachable" } : { kind: "idle" });
    } finally {
      running.current = false;
    }
  }, [folder, key, queryClient, server.id, server.url, session, files]);

  // The settings of this server on this phone, then a first look.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const stored = await settings.get(key);
        const last = Number(await settings.get(`${key}.last`));
        if (!cancelled) {
          if (stored) {
            setCurrent({ ...defaultBackupSettings, ...(JSON.parse(stored) as Partial<BackupSettings>) });
          }

          setLastRun(last > 0 ? last : null);
        }
      } catch {
        // Unreadable settings are the defaults: backup off.
      }

      if (!cancelled) {
        setAccess(await mediaAccess().catch(() => null));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [key]);

  useEffect(() => {
    void run();
    const timer = setInterval(() => void run(), EVERY_MS);
    const listener = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        void run();
      }
    });
    return () => {
      clearInterval(timer);
      listener.remove();
    };
  }, [current.enabled, run]);

  const value: BackupValue = {
    settings: current,
    save: async (next) => {
      setCurrent(next);
      latest.current = next;
      await settings.set(key, JSON.stringify(next)).catch(() => undefined);
      void run();
    },
    access,
    counts,
    activity,
    lastRun,
    failures,
    runNow: () => void run(),
  };

  return <BackupContext.Provider value={value}>{children}</BackupContext.Provider>;
}
