import { requireOptionalNativeModule } from "expo";

interface Native {
  show(title: string, text: string, done: number, total: number): boolean;
  hide(): void;
}

// Present in the Android app; absent elsewhere, where the calls do nothing.
const native = requireOptionalNativeModule<Native>("AtriumBackupService");

/**
 * Shows the backup's notification and keeps the app running while it
 * shows (a foreground service, D-058). Returns false where it could not
 * start: no Android, the app not in front, or Android's daily limit.
 */
export function showBackupService(title: string, text: string, done: number, total: number): boolean {
  return native?.show(title, text, done, total) ?? false;
}

export function hideBackupService(): void {
  native?.hide();
}
