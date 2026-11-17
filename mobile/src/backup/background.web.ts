// Only for looking at the app's screens in a browser while developing
// (mobile/preview): a browser has no background jobs, so backup runs only
// while the page is open.

export function scheduleBackgroundBackup(): Promise<void> {
  return Promise.resolve();
}
