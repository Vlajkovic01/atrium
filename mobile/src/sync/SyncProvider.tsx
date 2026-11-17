import { changeEffects, pullChanges } from "@atrium/shared";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, type ReactNode } from "react";
import { AppState } from "react-native";

import { useActiveServer } from "../api.ts";
import { localDb } from "../platform/db";
import { emitSync } from "./events.ts";

/** How often the change feed is read while the app is open and in front. */
const EVERY_MS = 30_000;

/**
 * Keeps what the app shows in step with the server in front (D-047): the
 * change feed is read when the app opens, every half minute while it is
 * in front, and when it comes back to the front. What changed elsewhere
 * (another device, the web, a folder scan) is read again: the folders it
 * is in, the items themselves, and the timeline when photos may have
 * changed. A device away too long reads everything again.
 *
 * Nothing runs in the background; camera backup will decide that.
 */
export function SyncProvider({ children }: { readonly children: ReactNode }) {
  const { server, session } = useActiveServer();
  const queryClient = useQueryClient();

  useEffect(() => {
    let stopped = false;
    let running = false;

    const sync = async (): Promise<void> => {
      if (running || stopped || AppState.currentState !== "active") {
        return;
      }

      running = true;
      try {
        const cursor = await localDb.cursor(server.id);
        const pull = await pullChanges(session.api, cursor);
        if (stopped) {
          return;
        }

        if (pull.reset) {
          // Nothing is known about what changed meanwhile: everything shown is read again.
          if (cursor !== null) {
            await queryClient.invalidateQueries({ queryKey: [server.id] });
          }

          emitSync({ serverId: server.id, kind: "reset" });
        } else if (pull.items.length > 0) {
          const effects = changeEffects(pull.items);
          emitSync({ serverId: server.id, kind: "changes", effects });
          await Promise.all([
            ...[...effects.folders].map((id) =>
              queryClient.invalidateQueries({ queryKey: [server.id, "children", id] }),
            ),
            ...[...effects.changed.keys(), ...effects.gone].map((id) =>
              queryClient.invalidateQueries({ queryKey: [server.id, "node", id] }),
            ),
            ...(effects.gone.size > 0
              ? [
                  queryClient.invalidateQueries({ queryKey: [server.id, "trash"] }),
                  // What is gone may not say where it was: every folder shown is read again.
                  queryClient.invalidateQueries({ queryKey: [server.id, "children"] }),
                ]
              : []),
            ...(effects.media
              ? [
                  queryClient.invalidateQueries({ queryKey: [server.id, "timeline"] }),
                  queryClient.invalidateQueries({ queryKey: [server.id, "timeline-buckets"] }),
                ]
              : []),
          ]);
        }

        // Kept only once applied: a pull that failed halfway is read again next time.
        await localDb.setCursor(server.id, pull.cursor);
      } catch {
        // Unreachable, or signed out meanwhile: the next turn tries again.
      } finally {
        running = false;
      }
    };

    void sync();
    const timer = setInterval(() => void sync(), EVERY_MS);
    const listener = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        void sync();
      }
    });
    return () => {
      stopped = true;
      clearInterval(timer);
      listener.remove();
    };
  }, [queryClient, server.id, session]);

  return children;
}
