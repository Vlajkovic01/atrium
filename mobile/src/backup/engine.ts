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
  type FilesApi,
  type ServerSession,
} from "@atrium/shared";

import { localDb } from "../platform/db";
import { charging, hashAsset, listAssets, mediaAccess, onWifi } from "../platform/media";
import { settings } from "../platform/storage";
import { fileSource } from "../uploads/source";

/** What the person set for camera backup on this phone and one server. */
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

/** What camera backup is doing, said as it is (docs/UX.md, Mobile, Camera backup). */
export type BackupActivity =
  | { readonly kind: "off" }
  | { readonly kind: "noAccess" }
  | { readonly kind: "waitingWifi" }
  | { readonly kind: "waitingCharging" }
  | { readonly kind: "reading" }
  | { readonly kind: "checking"; readonly name: string }
  | { readonly kind: "sending"; readonly name: string; readonly percent: number; readonly left: number }
  | { readonly kind: "unreachable" }
  | { readonly kind: "paused" }
  | { readonly kind: "idle" };

export const settingsKey = (serverId: string) => `atrium.backup.${serverId}`;

export async function loadBackupSettings(serverId: string): Promise<BackupSettings> {
  try {
    const stored = await settings.get(settingsKey(serverId));
    return stored
      ? { ...defaultBackupSettings, ...(JSON.parse(stored) as Partial<BackupSettings>) }
      : defaultBackupSettings;
  } catch {
    return defaultBackupSettings;
  }
}

/** When a run last finished, and whether that was in the background. */
export interface LastRun {
  readonly at: number;
  readonly background: boolean;
}

export async function loadLastRun(serverId: string): Promise<LastRun | null> {
  try {
    const stored = await settings.get(`${settingsKey(serverId)}.last`);
    if (!stored) {
      return null;
    }

    // Older entries hold only the time.
    return stored.startsWith("{") ? (JSON.parse(stored) as LastRun) : { at: Number(stored), background: false };
  } catch {
    return null;
  }
}

/** Everything a run needs: the server, its calls, and how to tell what happens. */
export interface BackupRun {
  readonly serverId: string;
  readonly serverUrl: string;
  readonly session: ServerSession;
  readonly files: FilesApi;
  readonly choice: BackupSettings;
  /** The name of the top backup folder, in the person's language, used only when it must be made. */
  readonly topFolderName: string;
  readonly deviceName: string;
  /** Whether this run is the background job's (it then stops at `deadline`). */
  readonly background: boolean;
  /** Milliseconds since the epoch after which no new file is started. */
  readonly deadline?: number;
  /** Asked between files: true stops the run early. */
  readonly stop?: () => boolean;
  readonly onActivity: (activity: BackupActivity) => void;
  readonly onCounts: (counts: BackupCounts, failures: readonly { name: string; code: string }[]) => void;
  /** Told how many files and bytes are about to be sent, before the first one. */
  readonly onSending?: (files: number, bytes: number) => void;
}

/** Hashes are asked about in batches this size. */
const ASK = 100;

/** The folder the backup goes into, remembered once made (D-058). */
async function backupFolder(run: BackupRun): Promise<string> {
  const key = `${settingsKey(run.serverId)}.folder`;
  const remembered = await settings.get(key).catch(() => null);
  if (remembered) {
    try {
      const node = await run.files.node(remembered);
      if (!node.trashedAt) {
        return node.id;
      }
    } catch {
      // Gone: made again below.
    }
  }

  const libraries = await run.files.libraries();
  const home = homeLibrary(libraries.filter((l) => l.kind === "personal"));
  if (!home) {
    throw new ApiError({ code: "backup.no_library", params: {}, status: 0 });
  }

  let parent = home.rootNodeId;
  for (const name of backupFolderNames(run.deviceName, run.topFolderName)) {
    const list = async () =>
      (await run.files.children(parent, { sort: "name", descending: false, kind: "folder", limit: 1000 })).items;
    const found = (await list()).find((n) => n.name === name);
    if (found) {
      parent = found.id;
      continue;
    }

    try {
      parent = (await run.files.createFolder(parent, name)).id;
    } catch (e) {
      // Made meanwhile, by another run or device: use that one.
      const made = (await list()).find((n) => n.name === name);
      if (!made) {
        throw e;
      }

      parent = made.id;
    }
  }

  await settings.set(key, parent).catch(() => undefined);
  return parent;
}

/** Servers a run is under way for, in this app process. */
const busy = new Set<string>();

/**
 * One run of camera backup for one server (D-058): new and changed
 * photos and videos are hashed, the server is asked which contents it
 * holds, and the rest is sent. Used by the app while open and by the
 * background job alike, never both at once for one server, so nothing is
 * sent twice. Never deletes anything from the phone.
 *
 * Resolves to how many files were sent, or null when another run for the
 * same server was already under way.
 */
export async function runBackup(run: BackupRun): Promise<number | null> {
  if (busy.has(run.serverId)) {
    return null;
  }

  busy.add(run.serverId);
  try {
    return await backUp(run);
  } catch (e) {
    run.onActivity(e instanceof ApiError && e.problem.code === "network" ? { kind: "unreachable" } : { kind: "idle" });
    return 0;
  } finally {
    busy.delete(run.serverId);
  }
}

async function backUp(run: BackupRun): Promise<number> {
  const choice = run.choice;
  if (!choice.enabled) {
    run.onActivity({ kind: "off" });
    return 0;
  }

  const allowed = await mediaAccess();
  if (allowed === "none" || allowed === "denied") {
    run.onActivity({ kind: "noAccess" });
    return 0;
  }

  let sent = 0;
  const outOfTime = () => (run.deadline !== undefined && Date.now() > run.deadline) || (run.stop?.() ?? false);

  run.onActivity({ kind: "reading" });
  const assets = await listAssets();
  const records = new Map((await localDb.backupRecords(run.serverId)).map((r) => [r.assetId, r]));
  const show = () =>
    run.onCounts(
      backupCounts(assets, [...records.values()], choice),
      [...records.values()]
        .filter((r) => r.error !== null)
        .map((r) => ({ name: assets.find((a) => a.id === r.assetId)?.name ?? r.assetId, code: r.error! })),
    );
  show();

  const save = async (record: BackupRecord): Promise<void> => {
    records.set(record.assetId, record);
    await localDb.putBackupRecord(run.serverId, record);
    show();
  };

  const blocked = async () => {
    if (choice.wifiOnly && !(await onWifi())) {
      return { kind: "waitingWifi" } as const;
    }

    if (choice.chargingOnly && !(await charging())) {
      return { kind: "waitingCharging" } as const;
    }

    return null;
  };

  const byId = new Map(assets.map((a) => [a.id, a]));

  // Hashing is local work: it goes on without Wi-Fi, and makes the server's check cheap.
  for (const asset of planBackup(assets, [...records.values()], choice).toHash) {
    if (outOfTime()) {
      run.onActivity({ kind: "paused" });
      return sent;
    }

    run.onActivity({ kind: "checking", name: asset.name });
    try {
      await save({
        assetId: asset.id,
        size: asset.size,
        modifiedAt: asset.modifiedAt,
        sha256: await hashAsset(byId.get(asset.id)!),
        done: false,
        error: null,
      });
    } catch {
      // Unreadable now (moved, being written): looked at again next time.
    }
  }

  const waiting = await blocked();
  if (waiting) {
    run.onActivity(waiting);
    return 0;
  }

  // What the server already holds, from this phone or any other, is not sent again.
  const pending = planBackup(assets, [...records.values()], choice).toSend;
  for (let at = 0; at < pending.length; at += ASK) {
    const batch = pending.slice(at, at + ASK);
    const owned = await run.files.ownedHashes(batch.map((a) => records.get(a.id)!.sha256!));
    for (const asset of batch) {
      const record = records.get(asset.id)!;
      if (owned.has(record.sha256!)) {
        await save({ ...record, done: true });
      }
    }
  }

  const toSend = planBackup(assets, [...records.values()], choice).toSend;
  if (toSend.length > 0) {
    run.onSending?.(
      toSend.length,
      toSend.reduce((n, a) => n + a.size, 0),
    );
    const parentId = await backupFolder(run);
    for (const [index, asset] of toSend.entries()) {
      if (outOfTime()) {
        run.onActivity({ kind: "paused" });
        return sent;
      }

      const stop = await blocked();
      if (stop) {
        run.onActivity(stop);
        return sent;
      }

      const phoneAsset = byId.get(asset.id)!;
      const record = records.get(asset.id)!;
      const upload = new TusUpload(
        {
          baseUrl: run.serverUrl,
          // Called as a plain function: a browser's fetch refuses any other `this`.
          fetch: (input, init) => fetch(input, init),
          getAccessToken: run.session.getAccessToken,
          refresh: run.session.refresh,
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
      const left = toSend.length - index;
      const unwatch = upload.onChange((status) => {
        if (status.state === "uploading" || status.state === "waiting") {
          run.onActivity({
            kind: "sending",
            name: asset.name,
            left,
            percent: asset.size === 0 ? 100 : Math.floor((status.offset / asset.size) * 100),
          });
        }
      });
      try {
        await upload.start();
      } finally {
        unwatch();
      }

      const end = upload.status;
      if (end.state === "completed") {
        await save({ ...record, done: true, error: null });
        sent++;
      } else if (end.state === "failed") {
        if (end.code === "network") {
          run.onActivity({ kind: "unreachable" });
          return sent;
        }

        await save({ ...record, error: end.code });
      }
    }
  }

  const last: LastRun = { at: Date.now(), background: run.background };
  await settings.set(`${settingsKey(run.serverId)}.last`, JSON.stringify(last)).catch(() => undefined);
  run.onActivity({ kind: "idle" });
  return sent;
}
