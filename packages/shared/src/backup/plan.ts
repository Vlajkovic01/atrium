/** A photo or video on the device, as the device's media library describes it. */
export interface DeviceAsset {
  readonly id: string;
  readonly name: string;
  readonly kind: "photo" | "video";
  readonly size: number;
  /** Milliseconds since the Unix epoch. */
  readonly modifiedAt: number;
  /** The device albums it is in ("Camera", "Screenshots", …). */
  readonly albums: readonly string[];
}

/** What the device remembers about an asset it looked at. */
export interface BackupRecord {
  readonly assetId: string;
  readonly size: number;
  readonly modifiedAt: number;
  /** Hex SHA-256 of the content, once computed. */
  readonly sha256: string | null;
  /** On the server: uploaded from here, or found there already by its content. */
  readonly done: boolean;
  /** The server's reason the last upload failed for good, or null. */
  readonly error: string | null;
}

/** What the person chose to back up. */
export interface BackupChoice {
  readonly photos: boolean;
  readonly videos: boolean;
  /** Only these device albums, or every album when null. */
  readonly albums: readonly string[] | null;
}

/** Whether an asset is one the person chose to back up. */
export function isChosen(asset: DeviceAsset, choice: BackupChoice): boolean {
  if (asset.kind === "photo" ? !choice.photos : !choice.videos) {
    return false;
  }

  return choice.albums === null || asset.albums.some((a) => choice.albums!.includes(a));
}

/** A record still describes an asset when its size and time are the same; an edited photo is new content. */
function current(record: BackupRecord | undefined, asset: DeviceAsset): record is BackupRecord {
  return record !== undefined && record.size === asset.size && record.modifiedAt === asset.modifiedAt;
}

/** The next work of a backup run, in the order it is done. */
export interface BackupPlan {
  /** Assets whose content must be hashed first: new, or changed since. */
  readonly toHash: readonly DeviceAsset[];
  /** Assets hashed but not known to be on the server: asked about, then uploaded if missing. */
  readonly toSend: readonly DeviceAsset[];
}

/**
 * Sorts the chosen assets into what is done, what must be hashed, and
 * what must be sent. A failure for good is not retried until the asset
 * changes; everything else is tried again on every run.
 */
export function planBackup(
  assets: readonly DeviceAsset[],
  records: readonly BackupRecord[],
  choice: BackupChoice,
): BackupPlan {
  const byId = new Map(records.map((r) => [r.assetId, r]));
  const toHash: DeviceAsset[] = [];
  const toSend: DeviceAsset[] = [];
  for (const asset of assets) {
    if (!isChosen(asset, choice)) {
      continue;
    }

    const record = byId.get(asset.id);
    if (!current(record, asset) || record.sha256 === null) {
      toHash.push(asset);
    } else if (!record.done && record.error === null) {
      toSend.push(asset);
    }
  }

  return { toHash, toSend };
}

/** The numbers the backup screen shows, honestly: what is on the server, what waits, what failed. */
export interface BackupCounts {
  readonly chosen: number;
  readonly done: number;
  readonly waiting: number;
  readonly failed: number;
  /** Bytes still to send. */
  readonly bytesWaiting: number;
}

export function backupCounts(
  assets: readonly DeviceAsset[],
  records: readonly BackupRecord[],
  choice: BackupChoice,
): BackupCounts {
  const byId = new Map(records.map((r) => [r.assetId, r]));
  let chosen = 0;
  let done = 0;
  let failed = 0;
  let bytesWaiting = 0;
  for (const asset of assets) {
    if (!isChosen(asset, choice)) {
      continue;
    }

    chosen++;
    const record = byId.get(asset.id);
    if (current(record, asset) && record.done) {
      done++;
    } else if (current(record, asset) && record.error !== null) {
      failed++;
    } else {
      bytesWaiting += asset.size;
    }
  }

  return { chosen, done, waiting: chosen - done - failed, failed, bytesWaiting };
}

/** The folder a device's backup goes into, under the person's own files: "Camera backup" / device name. */
export function backupFolderNames(deviceName: string, top: string): readonly [string, string] {
  const cleaned = [...deviceName].map((c) => (c.charCodeAt(0) < 32 || '\\/:*?"<>|'.includes(c) ? " " : c)).join("");
  return [top, cleaned.replace(/\s+/g, " ").trim() || "Phone"];
}
