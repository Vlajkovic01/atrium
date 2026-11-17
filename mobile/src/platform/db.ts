import type { BackupRecord } from "@atrium/shared";
import * as SQLite from "expo-sqlite";

/** A file or folder the person keeps on the phone ("Available offline"). */
export interface OfflineRoot {
  readonly nodeId: string;
  readonly kind: "file" | "folder";
  readonly name: string;
  readonly addedAt: number;
}

/** One file kept on the phone for an offline item: the item itself, or a file below it. */
export interface OfflineFile {
  readonly rootId: string;
  readonly nodeId: string;
  readonly parentId: string | null;
  readonly name: string;
  readonly mediaType: string | null;
  readonly version: number;
  readonly size: number;
  /** Bytes already on the phone; the copy is whole when this equals `size`. */
  readonly bytes: number;
  /** Where the copy is, as a `file://` address, or null before any byte arrived. */
  readonly path: string | null;
  /** The server's reason the last attempt failed for good, or null. */
  readonly error: string | null;
}

/**
 * The app's own database on the phone (expo-sqlite): where each server's
 * change feed was last read, and the offline copies. Nothing here is
 * secret; tokens stay in the Keystore. It can be lost: the app then reads
 * everything again from the servers.
 */
export interface LocalDb {
  /** Where the change feed of a server was last read, or null for never. */
  cursor(serverId: string): Promise<string | null>;
  setCursor(serverId: string, cursor: string): Promise<void>;
  roots(serverId: string): Promise<OfflineRoot[]>;
  addRoot(serverId: string, root: OfflineRoot): Promise<void>;
  /** Forgets an offline item and the rows of its files; the copies themselves are removed by the caller. */
  removeRoot(serverId: string, nodeId: string): Promise<void>;
  files(serverId: string, rootId?: string): Promise<OfflineFile[]>;
  putFile(serverId: string, file: OfflineFile): Promise<void>;
  removeFile(serverId: string, rootId: string, nodeId: string): Promise<void>;
  /** What camera backup knows about the phone's photos and videos, for one server. */
  backupRecords(serverId: string): Promise<BackupRecord[]>;
  putBackupRecord(serverId: string, record: BackupRecord): Promise<void>;
  /** Forgets everything about a server, when it is signed out of. */
  forgetServer(serverId: string): Promise<void>;
}

/** Each step brings the database from one version to the next; never change a step once released. */
const MIGRATIONS: readonly string[] = [
  `CREATE TABLE sync (
     server_id TEXT PRIMARY KEY NOT NULL,
     cursor TEXT NOT NULL,
     read_at INTEGER NOT NULL
   );`,
  `CREATE TABLE offline_root (
     server_id TEXT NOT NULL,
     node_id TEXT NOT NULL,
     kind TEXT NOT NULL,
     name TEXT NOT NULL,
     added_at INTEGER NOT NULL,
     PRIMARY KEY (server_id, node_id)
   );
   CREATE TABLE offline_file (
     server_id TEXT NOT NULL,
     root_id TEXT NOT NULL,
     node_id TEXT NOT NULL,
     parent_id TEXT,
     name TEXT NOT NULL,
     media_type TEXT,
     version INTEGER NOT NULL,
     size INTEGER NOT NULL,
     bytes INTEGER NOT NULL,
     path TEXT,
     error TEXT,
     PRIMARY KEY (server_id, root_id, node_id)
   );`,
  `CREATE TABLE backup_record (
     server_id TEXT NOT NULL,
     asset_id TEXT NOT NULL,
     size INTEGER NOT NULL,
     modified_at INTEGER NOT NULL,
     sha256 TEXT,
     done INTEGER NOT NULL,
     error TEXT,
     PRIMARY KEY (server_id, asset_id)
   );`,
];

let opened: Promise<SQLite.SQLiteDatabase> | null = null;

async function database(): Promise<SQLite.SQLiteDatabase> {
  opened ??= (async () => {
    const db = await SQLite.openDatabaseAsync("atrium.db");
    await db.execAsync("PRAGMA journal_mode = WAL;");
    const row = await db.getFirstAsync<{ user_version: number }>("PRAGMA user_version;");
    for (let version = row?.user_version ?? 0; version < MIGRATIONS.length; version++) {
      await db.withTransactionAsync(async () => {
        await db.execAsync(MIGRATIONS[version]!);
        await db.execAsync(`PRAGMA user_version = ${version + 1};`);
      });
    }

    return db;
  })();
  return opened;
}

interface FileRow {
  root_id: string;
  node_id: string;
  parent_id: string | null;
  name: string;
  media_type: string | null;
  version: number;
  size: number;
  bytes: number;
  path: string | null;
  error: string | null;
}

const toFile = (r: FileRow): OfflineFile => ({
  rootId: r.root_id,
  nodeId: r.node_id,
  parentId: r.parent_id,
  name: r.name,
  mediaType: r.media_type,
  version: r.version,
  size: r.size,
  bytes: r.bytes,
  path: r.path,
  error: r.error,
});

export const localDb: LocalDb = {
  cursor: async (serverId) =>
    (
      await (
        await database()
      ).getFirstAsync<{ cursor: string }>("SELECT cursor FROM sync WHERE server_id = ?", serverId)
    )?.cursor ?? null,
  setCursor: async (serverId, cursor) => {
    await (
      await database()
    ).runAsync(
      "INSERT INTO sync (server_id, cursor, read_at) VALUES (?, ?, ?) ON CONFLICT(server_id) DO UPDATE SET cursor = excluded.cursor, read_at = excluded.read_at",
      serverId,
      cursor,
      Date.now(),
    );
  },
  roots: async (serverId) =>
    (
      await (
        await database()
      ).getAllAsync<{ node_id: string; kind: "file" | "folder"; name: string; added_at: number }>(
        "SELECT node_id, kind, name, added_at FROM offline_root WHERE server_id = ? ORDER BY added_at DESC",
        serverId,
      )
    ).map((r) => ({ nodeId: r.node_id, kind: r.kind, name: r.name, addedAt: r.added_at })),
  addRoot: async (serverId, root) => {
    await (
      await database()
    ).runAsync(
      "INSERT OR REPLACE INTO offline_root (server_id, node_id, kind, name, added_at) VALUES (?, ?, ?, ?, ?)",
      serverId,
      root.nodeId,
      root.kind,
      root.name,
      root.addedAt,
    );
  },
  removeRoot: async (serverId, nodeId) => {
    const db = await database();
    await db.withTransactionAsync(async () => {
      await db.runAsync("DELETE FROM offline_file WHERE server_id = ? AND root_id = ?", serverId, nodeId);
      await db.runAsync("DELETE FROM offline_root WHERE server_id = ? AND node_id = ?", serverId, nodeId);
    });
  },
  files: async (serverId, rootId) =>
    (
      await (
        await database()
      ).getAllAsync<FileRow>(
        rootId
          ? "SELECT * FROM offline_file WHERE server_id = ? AND root_id = ? ORDER BY name"
          : "SELECT * FROM offline_file WHERE server_id = ? ORDER BY name",
        ...(rootId ? [serverId, rootId] : [serverId]),
      )
    ).map(toFile),
  putFile: async (serverId, f) => {
    await (
      await database()
    ).runAsync(
      `INSERT OR REPLACE INTO offline_file
         (server_id, root_id, node_id, parent_id, name, media_type, version, size, bytes, path, error)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      serverId,
      f.rootId,
      f.nodeId,
      f.parentId,
      f.name,
      f.mediaType,
      f.version,
      f.size,
      f.bytes,
      f.path,
      f.error,
    );
  },
  removeFile: async (serverId, rootId, nodeId) => {
    await (
      await database()
    ).runAsync(
      "DELETE FROM offline_file WHERE server_id = ? AND root_id = ? AND node_id = ?",
      serverId,
      rootId,
      nodeId,
    );
  },
  backupRecords: async (serverId) =>
    (
      await (
        await database()
      ).getAllAsync<{
        asset_id: string;
        size: number;
        modified_at: number;
        sha256: string | null;
        done: number;
        error: string | null;
      }>("SELECT asset_id, size, modified_at, sha256, done, error FROM backup_record WHERE server_id = ?", serverId)
    ).map((r) => ({
      assetId: r.asset_id,
      size: r.size,
      modifiedAt: r.modified_at,
      sha256: r.sha256,
      done: r.done === 1,
      error: r.error,
    })),
  putBackupRecord: async (serverId, r) => {
    await (
      await database()
    ).runAsync(
      "INSERT OR REPLACE INTO backup_record (server_id, asset_id, size, modified_at, sha256, done, error) VALUES (?, ?, ?, ?, ?, ?, ?)",
      serverId,
      r.assetId,
      r.size,
      r.modifiedAt,
      r.sha256,
      r.done ? 1 : 0,
      r.error,
    );
  },
  forgetServer: async (serverId) => {
    const db = await database();
    await db.withTransactionAsync(async () => {
      await db.runAsync("DELETE FROM backup_record WHERE server_id = ?", serverId);
      await db.runAsync("DELETE FROM sync WHERE server_id = ?", serverId);
      await db.runAsync("DELETE FROM offline_file WHERE server_id = ?", serverId);
      await db.runAsync("DELETE FROM offline_root WHERE server_id = ?", serverId);
    });
  },
};
