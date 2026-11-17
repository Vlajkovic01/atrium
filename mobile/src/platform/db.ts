import * as SQLite from "expo-sqlite";

/**
 * The app's own database on the phone (expo-sqlite): where each server's
 * change feed was last read, and later the offline copies. Nothing here
 * is secret; tokens stay in the Keystore. It can be deleted at any time:
 * the app then reads everything again from the servers.
 */
export interface LocalDb {
  /** Where the change feed of a server was last read, or null for never. */
  cursor(serverId: string): Promise<string | null>;
  setCursor(serverId: string, cursor: string): Promise<void>;
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
  forgetServer: async (serverId) => {
    await (await database()).runAsync("DELETE FROM sync WHERE server_id = ?", serverId);
  },
};
