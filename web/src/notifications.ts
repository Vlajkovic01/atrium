import { notificationsApi, type ClientNotificationKind, type NotificationList } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";

import { api } from "./api.ts";

export type { Notification, NotificationList } from "@atrium/shared";

const notifications = notificationsApi(api);

const key = ["notifications"];

/** The person's notifications, kept on the server (D-045), checked every minute. */
export function useNotifications(enabled = true) {
  return useQuery({
    queryKey: key,
    queryFn: notifications.list,
    enabled,
    refetchInterval: 60_000,
  });
}

/** Changes to the list; each shows the new list at once. */
export function useNotificationActions() {
  const queryClient = useQueryClient();
  return useMemo(() => {
    const apply = async (request: Promise<NotificationList>): Promise<void> => {
      queryClient.setQueryData(key, await request);
    };

    return {
      markRead: (ids: readonly string[] | null) => apply(notifications.markRead(ids)),
      remove: (id: string) => apply(notifications.remove(id)),
      clear: () => apply(notifications.clear()),
      /** What this client saw itself, such as uploads that finished. */
      add: (kind: ClientNotificationKind, values: Record<string, string>, link: string | null) =>
        apply(notifications.add(kind, values, link)),
    };
  }, [queryClient]);
}

export { notificationText } from "@atrium/shared";
