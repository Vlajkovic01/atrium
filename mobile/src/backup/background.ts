import { filesApi } from "@atrium/shared";
import * as BackgroundTask from "expo-background-task";
import * as TaskManager from "expo-task-manager";
import i18next from "i18next";

import { thisDevice } from "../device.ts";
import { initI18n } from "../i18n.ts";
import { loadRegistry } from "../registry.ts";
import { loadBackupSettings, runBackup } from "./engine.ts";

/**
 * Camera backup in the background (D-058). Android runs this job about
 * every 15 minutes at the soonest, when it chooses (battery, Doze), even
 * after the app was closed; each run backs up every server that has camera
 * backup on, under the same conditions as in the app, and stops starting
 * new files before Android's time for the job is up. An unfinished run is
 * simply continued the next time: what is on the server is never sent again.
 */
const TASK = "atrium-camera-backup";

/** Android gives a job about ten minutes; no new file is started after this. */
const RUN_MS = 8 * 60_000;

TaskManager.defineTask(TASK, async () => {
  const deadline = Date.now() + RUN_MS;
  try {
    if (!i18next.isInitialized) {
      // The app may not be open: the backup folder's name needs the person's language.
      await initI18n();
    }

    const registry = await loadRegistry();
    for (const server of registry.servers) {
      if (Date.now() > deadline) {
        break;
      }

      const choice = await loadBackupSettings(server.id);
      if (!choice.enabled) {
        continue;
      }

      const session = registry.session(server.id);
      await runBackup({
        serverId: server.id,
        serverUrl: server.url,
        session,
        files: filesApi(session.api),
        choice,
        topFolderName: i18next.t("mobile.backup.folder"),
        deviceName: thisDevice().name,
        background: true,
        deadline,
        onActivity: () => undefined,
        onCounts: () => undefined,
      });
    }

    return BackgroundTask.BackgroundTaskResult.Success;
  } catch {
    return BackgroundTask.BackgroundTaskResult.Failed;
  }
});

/** Asks Android for the job while any server has camera backup on, and gives it up when none has. */
export async function scheduleBackgroundBackup(): Promise<void> {
  try {
    const registry = await loadRegistry();
    const choices = await Promise.all(registry.servers.map((s) => loadBackupSettings(s.id)));
    const wanted = choices.some((c) => c.enabled);
    const registered = await TaskManager.isTaskRegisteredAsync(TASK);
    if (wanted && !registered) {
      await BackgroundTask.registerTaskAsync(TASK, { minimumInterval: 15 });
    } else if (!wanted && registered) {
      await BackgroundTask.unregisterTaskAsync(TASK);
    }
  } catch {
    // Without the job, backup still runs while the app is open.
  }
}
