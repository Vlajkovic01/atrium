import type { BackupCounts } from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { AppState, PermissionsAndroid, Platform } from "react-native";

import { hideBackupService, showBackupService } from "../../modules/atrium-backup-service";
import { useActiveServer } from "../api.ts";
import { thisDevice } from "../device.ts";
import { mediaAccess, type MediaAccess } from "../platform/media";
import { settings } from "../platform/storage";
import { scheduleBackgroundBackup } from "./background";
import {
  defaultBackupSettings,
  loadBackupSettings,
  loadLastRun,
  runBackup,
  settingsKey,
  type BackupActivity,
  type BackupSettings,
  type LastRun,
} from "./engine.ts";

export type { BackupActivity, BackupSettings } from "./engine.ts";

interface BackupValue {
  readonly settings: BackupSettings;
  readonly save: (next: BackupSettings) => Promise<void>;
  readonly access: MediaAccess | null;
  readonly counts: BackupCounts | null;
  readonly activity: BackupActivity;
  /** When a run last finished on this phone, and whether in the background; null for never. */
  readonly lastRun: LastRun | null;
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

/** Lets the backup's notification show (Android 13+); backup works the same without it. */
async function askNotifications(): Promise<void> {
  if (Platform.OS === "android" && Platform.Version >= 33) {
    await PermissionsAndroid.request("android.permission.POST_NOTIFICATIONS").catch(() => undefined);
  }
}

/**
 * Camera backup on the server in front (D-058): the phone's chosen photos
 * and videos go to "Camera backup / <this phone>" in the person's own
 * files (the work itself is in engine.ts). While the app is open it runs
 * on opening and every 15 minutes; when it has files to send, a
 * notification (a foreground service) keeps it going if the person leaves
 * the app. Android's background job (background.ts) does the rest.
 */
export function BackupProvider({ children }: { readonly children: ReactNode }) {
  const { t } = useTranslation();
  const { server, session, files } = useActiveServer();
  const queryClient = useQueryClient();
  const key = settingsKey(server.id);
  const [current, setCurrent] = useState<BackupSettings>(defaultBackupSettings);
  const [access, setAccess] = useState<MediaAccess | null>(null);
  const [counts, setCounts] = useState<BackupCounts | null>(null);
  const [activity, setActivity] = useState<BackupActivity>({ kind: "off" });
  const [lastRun, setLastRun] = useState<LastRun | null>(null);
  const [failures, setFailures] = useState<readonly { name: string; code: string }[]>([]);
  const latest = useRef(current);
  latest.current = current;

  const run = useCallback(async (): Promise<void> => {
    let shown = false;
    let said = "";
    let total = 0;
    const sent = await runBackup({
      serverId: server.id,
      serverUrl: server.url,
      session,
      files,
      choice: latest.current,
      topFolderName: t("mobile.backup.folder"),
      deviceName: thisDevice().name,
      background: false,
      onActivity: (a) => {
        setActivity(a);
        if (a.kind === "sending" && shown) {
          // Changed once per file, not on every percent.
          const text = t("mobile.backup.serviceText", { left: a.left, name: a.name });
          if (text !== said) {
            said = text;
            showBackupService(t("mobile.backup.serviceTitle"), text, total - a.left, total);
          }
        }
      },
      onCounts: (c, f) => {
        setCounts(c);
        setFailures(f);
      },
      onSending: (count) => {
        // Only possible while the app is in front; otherwise the background job carries on.
        total = count;
        shown = AppState.currentState === "active" && showBackupService(t("mobile.backup.serviceTitle"), "", 0, count);
      },
    }).finally(() => {
      if (shown) {
        hideBackupService();
      }
    });

    if (sent === null) {
      // Another run (the background job's) is under way; its result shows on the next look.
      return;
    }

    setAccess(await mediaAccess().catch(() => null));
    setLastRun(await loadLastRun(server.id));
    if (sent > 0) {
      await queryClient.invalidateQueries({ queryKey: [server.id, "timeline"] });
    }
  }, [files, queryClient, server.id, server.url, session, t]);

  // The settings of this server on this phone, then a first look.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const [stored, last, allowed] = await Promise.all([
        loadBackupSettings(server.id),
        loadLastRun(server.id),
        mediaAccess().catch(() => null),
      ]);
      if (!cancelled) {
        setCurrent(stored);
        setLastRun(last);
        setAccess(allowed);
      }
    })();
    void scheduleBackgroundBackup();
    return () => {
      cancelled = true;
    };
  }, [server.id]);

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
      if (next.enabled) {
        await askNotifications();
      }

      await scheduleBackgroundBackup();
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
