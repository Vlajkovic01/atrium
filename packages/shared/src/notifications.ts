import { call } from "./api/call.ts";
import type { ApiClient } from "./api/client.ts";
import type { components } from "./api/schema.d.ts";
import type { QueuedUpload } from "./upload/queue.ts";

export type Notification = components["schemas"]["Notification"];
export type NotificationList = components["schemas"]["NotificationList"];

/** What a client may add itself (D-045): only what it saw, such as its own uploads. */
export type ClientNotificationKind = "uploads.finished" | "uploads.failed";

/** The person's notifications, kept on the server so every device shows the same list (D-045). */
export function notificationsApi(api: ApiClient) {
  return {
    list: (): Promise<NotificationList> => call(api.GET("/api/v1/me/notifications")),
    /** Marks the given ones read, or all of them with null. */
    markRead: (ids: readonly string[] | null): Promise<NotificationList> =>
      call(api.POST("/api/v1/me/notifications/read", { body: { ids: ids ? [...ids] : null } })),
    remove: (id: string): Promise<NotificationList> =>
      call(api.DELETE("/api/v1/me/notifications/{id}", { params: { path: { id } } })),
    clear: (): Promise<NotificationList> => call(api.DELETE("/api/v1/me/notifications")),
    add: (
      kind: ClientNotificationKind,
      values: Record<string, string>,
      link: string | null,
    ): Promise<NotificationList> =>
      call(api.POST("/api/v1/me/notifications", { body: { kind, params: values, link } })),
  };
}

export type NotificationsApi = ReturnType<typeof notificationsApi>;

/** A translation function, as i18next gives it. */
export type Translate = (key: string, options: Record<string, unknown> & { readonly defaultValue: string }) => string;

/** The words for a notification; a kind from a newer server still reads sensibly. */
export function notificationText(t: Translate, notification: Notification): string {
  const values = notification.params;
  const count = Number(values["count"] ?? 0);
  const work = values["type"]
    ? t(`backgroundWork.jobTypes.${values["type"].replace(".", "_")}`, { defaultValue: values["type"] })
    : "";
  return t(`notifications.kinds.${notification.kind.replace(".", "_")}`, {
    count,
    name: values["name"] ?? "",
    work,
    defaultValue: t("notifications.kinds.other", { defaultValue: "" }),
  });
}

/** Where a notification leads inside the app: a folder, or nowhere the app knows. */
export function notificationFolder(link: string | null): string | null {
  return /^\/files\/([0-9a-f-]{36})$/i.exec(link ?? "")?.[1] ?? null;
}

const ACTIVE = new Set(["queued", "uploading", "waiting", "paused"]);

export interface UploadReport {
  readonly kind: ClientNotificationKind;
  readonly values: Record<string, string>;
  readonly link: string | null;
}

/**
 * What to tell the server once a batch of uploads is over: how many
 * arrived (with their folder, when they share one) and how many failed.
 * Nothing while any upload is still going on, so a batch is one
 * notification; `reported` holds the uploads already told about.
 */
export function uploadReports(
  items: readonly QueuedUpload[],
  reported: ReadonlySet<number>,
): { readonly reports: readonly UploadReport[]; readonly ids: readonly number[] } {
  const fresh = items.filter((i) => !reported.has(i.id));
  if (fresh.length === 0 || items.some((i) => ACTIVE.has(i.status.state))) {
    return { reports: [], ids: [] };
  }

  const done = fresh.filter((i) => i.status.state === "completed");
  const failed = fresh.filter((i) => i.status.state === "failed");
  const folders = [...new Set(done.map((i) => i.parentId))];
  const reports: UploadReport[] = [];
  if (done.length > 0) {
    reports.push({
      kind: "uploads.finished",
      values: { count: String(done.length) },
      link: folders.length === 1 ? `/files/${folders[0]}` : null,
    });
  }

  if (failed.length > 0) {
    reports.push({ kind: "uploads.failed", values: { count: String(failed.length) }, link: null });
  }

  return { reports, ids: fresh.map((i) => i.id) };
}
