import * as LocalAuthentication from "expo-local-authentication";
import * as ScreenCapture from "expo-screen-capture";

/** Whether the phone can prove it is its owner: a fingerprint, a face, or at least the screen lock. */
export async function canLock(): Promise<boolean> {
  const level = await LocalAuthentication.getEnrolledLevelAsync();
  return level !== LocalAuthentication.SecurityLevel.NONE;
}

/**
 * Asks the phone's own prompt: fingerprint or face, with the phone's PIN,
 * pattern or password as the way back when those fail. Atrium never sees
 * any of it, only whether it succeeded.
 */
export async function unlock(prompt: string, cancel: string): Promise<boolean> {
  const result = await LocalAuthentication.authenticateAsync({
    promptMessage: prompt,
    cancelLabel: cancel,
    disableDeviceFallback: false,
  });
  return result.success;
}

/**
 * Hides the app's content in the list of open apps, and with it keeps
 * screenshots out (Android's FLAG_SECURE); on while the lock is on.
 */
export async function hideFromSwitcher(hide: boolean): Promise<void> {
  if (hide) {
    await ScreenCapture.preventScreenCaptureAsync("atrium-lock");
  } else {
    await ScreenCapture.allowScreenCaptureAsync("atrium-lock");
  }
}
