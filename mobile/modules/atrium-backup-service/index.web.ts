// Only for looking at the app's screens in a browser while developing
// (mobile/preview): a browser has no foreground service; backup runs only
// while the page is open.

export function showBackupService(): boolean {
  return false;
}

export function hideBackupService(): void {}
