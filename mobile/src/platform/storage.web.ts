import type { KeyValueStore, SecretStore } from "@atrium/shared";

// Only for looking at the app's screens in a browser while developing.
// The app is not published for the web, and a browser has no Keystore:
// tokens here last as long as the tab.

export const settings: KeyValueStore = {
  get: (key) => Promise.resolve(localStorage.getItem(key)),
  set: (key, value) => Promise.resolve(localStorage.setItem(key, value)),
};

export const secrets: SecretStore = {
  get: (key) => Promise.resolve(sessionStorage.getItem(key)),
  set: (key, value) => Promise.resolve(sessionStorage.setItem(key, value)),
  delete: (key) => Promise.resolve(sessionStorage.removeItem(key)),
};
