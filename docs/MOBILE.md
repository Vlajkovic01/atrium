# Atrium mobile app

The Android app in `/mobile` (React Native, Expo). Built in Phase 4. iOS
is configured but not built or tested (D-048). Screens and flows are
described in [UX.md, Mobile](UX.md#mobile-phase-4); the decisions behind
them are D-047 to D-058 in [DECISIONS.md](DECISIONS.md).

## Getting the app onto a phone

There are no store releases before the first Atrium release (D-030).

1. Every CI run builds an APK in the job "Mobile app build". Open the
   run on GitHub, download the artifact `atrium-android` (kept 14 days)
   and unzip it.
2. Copy `app-release.apk` to the phone and open it. Android asks once to
   allow installing from that app (Files, the browser).
3. The APK is signed with the debug key: installable for trying it out,
   not a store release. An APK signed with another key cannot be
   installed over it; uninstall first (offline copies and backup state
   on the phone go with it; nothing on the server does).

The package name `app.atrium.mobile` cannot change after a store release.
Publishing anywhere is the owner's decision.

## Connecting

- **Scan**: in the web app, Devices, "Connect a phone" shows a QR code
  valid for 2 minutes. Scanning it in the app signs in without a
  password. The same code opened with the phone's camera opens the app.
- **Type the address**, then sign in. The app asks the server who it is
  before a password is typed.
- Plain HTTP is accepted only to a local address (D-049). The phone must
  reach the server: same Wi-Fi, a VPN, or a public HTTPS address. While
  connected over plain HTTP the app shows "Not encrypted" above the tabs.
- Several servers can be added; Account switches between them. Each has
  its own session, settings, offline files and camera backup.

## What it does

| Area | Built |
|---|---|
| Files | Libraries, folders, sort, select, new folder, rename, move, copy, trash and restore, delete forever. Files open in the app (photo, video, audio, PDF, text) or in another app (D-051, D-052) |
| Photos | Timeline grouped by day with three sizes, albums (make, rename, cover, add, remove), search by name, type and date (D-053) |
| Uploads | From the gallery, the camera or any file, resumable (tus), kept across restarts, reported in the notifications (D-054) |
| Notifications | The server's notifications with an unread count on Account |
| Offline | Folders and files kept on the phone, refreshed from the change feed, with a storage limit (D-055, D-056) |
| Camera backup | Off until turned on; photos, videos, chosen albums; only on Wi-Fi, only while charging; known by content, never sent twice, never deletes from the phone; runs while open, in Android's background job, and with a notification while sending (D-058) |
| Security | Optional lock with fingerprint, face or screen lock; refresh tokens in the Android Keystore (D-050, D-057) |
| Devices | The person's signed-in devices, signing one out, approving a TV by scanning its code (D-050) |
| Video | The phone's own player (Media3). What the phone decodes and its screen shows is read from Android; the server plays the original when it can and streams otherwise. Quality, audio track and playback info above the player; a copy kept on the phone plays as it is (D-067) |

## Permissions

Each is asked when the feature that needs it is first used, with the
reason, and refusing it leaves the rest of the app working.

| Permission | For |
|---|---|
| Camera | Scanning a connect or TV code; taking a photo to upload |
| Photos and videos (Android 13+), storage read (12 and older) | Uploading from the gallery; camera backup |
| Media location | Camera backup keeps a photo whole (Atrium reads no location, D-031) |
| Notifications (Android 13+) | The camera backup notification |
| Foreground service (data sync) | Keeping camera backup sending after leaving the app |
| Biometrics | The optional lock |

Writing to the gallery (`WRITE_EXTERNAL_STORAGE`) is blocked in the build.

## Developing

From the repository root, with Node and pnpm as in `package.json`:

| Command | What it does |
|---|---|
| `pnpm --filter @atrium/mobile run typecheck` | Types of the app and the preview |
| `pnpm --filter @atrium/mobile run bundle` | Builds the Android JavaScript bundle, the quickest check that it compiles |
| `pnpm --filter @atrium/mobile run preview` | Runs every screen in Edge at a phone's size against a real server and saves pictures to `screenshots/mobile/` (about 15 minutes) |
| `pnpm --filter @atrium/mobile run start` | Expo's development server, for a phone with a development build |

The Android project is not committed: CI generates it from `app.json`
(`expo prebuild`) and compiles it. The owner's laptop has no Android
tools, so native code (Kotlin in `mobile/modules/`, plugins in
`app.json`) is first compiled by CI.

### Structure

- Logic the web app also needs lives in `packages/shared` with tests
  (file, photo, album, search, notification and device calls, the change
  feed, offline and backup plans, hashing, layout and formatting). The
  app holds screens and platform code only.
- `app/` is the routes (expo-router): tabs Photos, Files, Account; search
  and notifications above them.
- `src/platform/` wraps the phone: secure storage, SQLite (versioned
  migrations), media library, biometrics. Each has a `.web.ts` twin used
  only by the browser preview.
- Providers under the tabs, per server: uploads, sync (change feed),
  offline, camera backup. The list of servers and sessions is one object
  for the whole app (`src/registry.ts`), shared with the background job.
- `modules/atrium-backup-service` is a small local Expo module: the
  foreground service that holds the backup notification.

### The browser preview

`mobile/preview` builds the app for the web (react-native-web), serves it
with `/api` forwarded to a real server, and drives it with Playwright in
both languages and both themes. It checks layout, text, flows and what
reaches the server. Scenarios run in file order and share one server.

It cannot check what only a phone has. Every pull request lists those
separately. The usual ones:

- the camera and QR scanning, the gallery picker, the Keystore;
- the biometric prompt;
- opening files in other apps, PDF rendering, video playback;
- background work: the backup job and the notification;
- real network loss and Wi-Fi or charging conditions;
- the system back button and gestures.

## Known limits

- iOS is not built.
- Opening a TV's approval link from the phone's camera goes to the web
  app: claiming https links needs a decision on the server's domain.
- A large video cut off by the background job's time limit is sent again
  from its start the next time.
- Freeing space on the phone after backup is not built.
- No emulator tests in CI yet; the phone checks are manual.
