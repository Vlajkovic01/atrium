import type { ClientNotificationKind, NotificationList } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";

import { useActiveServer } from "./api.ts";

/** How often the list is checked while the app is open, as on the web. */
const CHECK_MS = 60_000;

/** The person's notifications on the server in front, kept on the server (D-045). */
export function useNotifications() {
  const { server, notifications } = useActiveServer();
  return useQuery({
    queryKey: [server.id, "notifications"],
    queryFn: notifications.list,
    refetchInterval: CHECK_MS,
  });
}

/** Changes to the list; each shows the new list at once. */
export function useNotificationActions() {
  const { server, notifications } = useActiveServer();
  const queryClient = useQueryClient();
  return useMemo(() => {
    const apply = async (request: Promise<NotificationList>): Promise<void> => {
      queryClient.setQueryData([server.id, "notifications"], await request);
    };

    return {
      markRead: (ids: readonly string[] | null) => apply(notifications.markRead(ids)),
      remove: (id: string) => apply(notifications.remove(id)),
      clear: () => apply(notifications.clear()),
      /** What this phone saw itself, such as uploads that finished. */
      add: (kind: ClientNotificationKind, values: Record<string, string>, link: string | null) =>
        apply(notifications.add(kind, values, link)),
    };
  }, [notifications, queryClient, server.id]);
}
