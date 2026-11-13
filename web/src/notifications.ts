import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useMemo } from "react";

import { api, call } from "./api.ts";

export type Notification = components["schemas"]["Notification"];
export type NotificationList = components["schemas"]["NotificationList"];

const key = ["notifications"];

/** The person's notifications, kept on the server (D-045), checked every minute. */
export function useNotifications(enabled = true) {
  return useQuery({
    queryKey: key,
    queryFn: () => call(api.GET("/api/v1/me/notifications")),
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
      markRead: (ids: readonly string[] | null) =>
        apply(call(api.POST("/api/v1/me/notifications/read", { body: { ids: ids ? [...ids] : null } }))),
      remove: (id: string) => apply(call(api.DELETE("/api/v1/me/notifications/{id}", { params: { path: { id } } }))),
      clear: () => apply(call(api.DELETE("/api/v1/me/notifications"))),
      /** What this client saw itself, such as uploads that finished. */
      add: (kind: "uploads.finished" | "uploads.failed", values: Record<string, string>, link: string | null) =>
        apply(call(api.POST("/api/v1/me/notifications", { body: { kind, params: values, link } }))),
    };
  }, [queryClient]);
}

/** The words for a notification; unknown kinds from a newer server still read sensibly. */
export function notificationText(t: TFunction, notification: Notification): string {
  const values = notification.params;
  const count = Number(values["count"] ?? 0);
  const work = values["type"]
    ? t(`backgroundWork.jobTypes.${values["type"].replace(".", "_")}`, { defaultValue: values["type"] })
    : "";
  return t(`notifications.kinds.${notification.kind.replace(".", "_")}`, {
    count,
    name: values["name"] ?? "",
    work,
    defaultValue: t("notifications.kinds.other"),
  });
}
