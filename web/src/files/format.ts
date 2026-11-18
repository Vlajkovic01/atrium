import { formatDay as sharedFormatDay } from "@atrium/shared";

import { currentLocale } from "../i18n.ts";

export { daysUntil } from "@atrium/shared";
export { formatBytes, formatDateTime } from "../i18n.ts";

export function formatDay(iso: string): string {
  return sharedFormatDay(iso, currentLocale());
}
