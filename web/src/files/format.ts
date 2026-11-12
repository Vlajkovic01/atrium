import { currentLocale } from "../i18n.ts";

export { formatBytes, formatDateTime } from "../i18n.ts";

export function formatDay(iso: string): string {
  return new Intl.DateTimeFormat(currentLocale(), { dateStyle: "medium" }).format(new Date(iso));
}

/** Whole days from now until a date, never below zero. */
export function daysUntil(iso: string): number {
  return Math.max(0, Math.ceil((new Date(iso).getTime() - Date.now()) / 86_400_000));
}
