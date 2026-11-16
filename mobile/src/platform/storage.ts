import type { KeyValueStore, SecretStore } from "@atrium/shared";
import * as SecureStore from "expo-secure-store";
import Storage from "expo-sqlite/kv-store";

/** Plain settings, in the app's own database. Removed with the app. */
export const settings: KeyValueStore = {
  get: (key) => Storage.getItem(key),
  set: (key, value) => Storage.setItem(key, value),
};

/**
 * Refresh tokens, in the Android Keystore or the iOS Keychain
 * (DESIGN.md 6.2): encrypted by the system, readable only by this app,
 * and only after the device was unlocked once since it started.
 */
export const secrets: SecretStore = {
  get: (key) => SecureStore.getItemAsync(key),
  set: (key, value) =>
    SecureStore.setItemAsync(key, value, { keychainAccessible: SecureStore.AFTER_FIRST_UNLOCK_THIS_DEVICE_ONLY }),
  delete: (key) => SecureStore.deleteItemAsync(key),
};
