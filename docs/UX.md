# Atrium UX

Status: Phase 0 draft. Screens and flows in text, for web, mobile and TV.

## Principles

- The user's files are never at risk from a tap. Destructive actions go to
  the trash and say so; the few permanent ones ask for confirmation and
  name what will be lost.
- Every long operation shows progress and survives closing the app.
- Every screen has designed empty, loading, offline and error states. An
  error says what happened and what the user can do.
- The same words mean the same things on every client. All text exists in
  English and Serbian.
- Keyboard and screen-reader accessible on the web; platform accessibility
  settings (text size, reduced motion, screen readers) respected on
  mobile and TV.
- A persistent indicator shows when the connection to the server is not
  encrypted.

---

## Web

### Setup wizard (Phase 1)

Shown on first visit, until an admin exists. Six steps with a progress
indicator; each can be revisited before finishing.

1. **Welcome.** Language choice. If the request comes from a public
   address, a field for the setup token with instructions on where to find
   it.
2. **Admin account.** Name, password with strength feedback.
3. **Storage.** Choose where files go: a folder on this machine, a mounted
   network share, or S3-compatible storage (with presets for AWS S3,
   Backblaze B2, Cloudflare R2, MinIO). "Test" writes, reads back and
   deletes a probe file and shows free space. The step cannot be passed
   with a failing test. A short plain-language note explains that files
   are stored in Atrium's own format and how backups protect the names
   and folders.
4. **Media.** Shows detected hardware and which hardware encoders passed a
   real test. Suggested transcoding limits for this machine, editable.
   Optional: add existing media folders as read-only libraries now.
5. **Access.** Shows the address the server sees and whether a reverse
   proxy was detected. Explains the three ways to reach the server from
   outside (bundled HTTPS, own proxy, tunnel) with links to the guides.
   Nothing is required here.
6. **Done.** Summary of what was configured, a QR code to connect the
   mobile app, and a button into the app.

### Sign in

Name and password, then a TOTP code if enabled. "Sign in with another
device" is not needed on the web; pairing is for TVs.

### App shell

Left navigation: Files, Photos, Movies and Shows (Phase 7), Shared
(Phase 8), Trash. Top bar: search, upload and job activity indicator, user
menu (Devices, Settings, Sign out). Admins see Administration.

The activity indicator opens a panel listing uploads and background jobs
with progress, pause and cancel.

### Files (Phase 2)

- Folder view as a list or grid, with breadcrumbs, sorting, multi-select
  with keyboard and mouse.
- Actions: new folder, upload, download (single file, or several as a
  ZIP), rename, move (dialog with folder picker, or drag onto a folder),
  copy, delete, details.
- Upload by button or by dragging files and folders anywhere on the view.
  A name conflict asks once: keep both, replace (the old content is kept as
  an earlier version) or skip, with "apply to all".
- Uploads continue while navigating, resume after a network drop, and
  resume after a page reload once the user reselects the same files.
- Details panel: size, dates, type, and media information when available.
  A Versions section lists earlier versions of a file with date and size;
  each can be downloaded, restored or removed.
- Read-only libraries show a lock badge and hide write actions.

### Trash

List of deleted items with original location and days left. Restore,
delete permanently, empty trash. Permanent deletion asks for confirmation
and states the count and size.

### Photos (Phase 3)

- **Timeline.** A justified grid grouped by day, with a month and year
  scrubber on the right that works instantly even with hundreds of
  thousands of items. Multi-select by click and drag or shift-click.
- **Viewer.** Full-window photo or video, previous and next, zoom,
  information panel (date, camera, location on a map), add to album,
  download, delete.
- **Albums.** Grid of albums; an album is a timeline of its items with a
  cover and title. Create from a selection.
- **Search.** One field. Understands names, types ("video"), years
  and months ("2024", "May 2024") and places. Date ranges use a date
  picker; natural-language dates such as "last summer" are out of scope.
  Filters appear as chips below the field.
- **Map.** Clusters of photos; selecting a cluster opens it as a timeline.

### Movies and shows (Phases 5 to 7)

- **Home.** Rows: Continue watching, Next up, Recently added, then one row
  per library.
- **Library.** Poster grid with sort and filters (genre, year, unwatched).
- **Movie page.** Backdrop, poster, title, year, runtime, overview, play
  or resume, audio and subtitle choice, versions if several files exist,
  mark watched, fix match.
- **Show page.** Seasons as tabs or a row; episodes with thumbnail, title,
  progress, watched state. Play resumes the next unwatched episode.
- **Fix match.** Search configured providers, choose the right result, or
  edit fields by hand. Without providers, titles come from file names and
  can be edited.

### Player (Phases 5, 6, 9)

- Controls: play and pause, seek bar with thumbnail preview, skip back and
  forward, volume, audio track, subtitles, quality, playback speed,
  picture-in-picture, fullscreen, cast.
- Subtitles menu: tracks by language and source, off, timing offset,
  appearance (size, color, background), and actions when configured:
  search online, generate from audio, translate.
- A "playback info" panel says whether the stream is direct, remuxed or
  transcoded, and why.
- Next episode card near the end with a countdown the user can cancel.
- Position is saved continuously and resumes on any device.
- Keyboard shortcuts for all controls, listed under `?`.

### Devices

List of signed-in devices with name, kind, last seen and approximate
location by address. Revoke any. Two actions:

- **Connect a phone.** Shows a QR code valid for two minutes.
- **Approve a device.** Enter the code shown on a TV. The page then shows
  the device name and kind, the warning to approve only a device in front
  of you, optional restrictions (read-only, hide my personal files), and
  Approve or Deny.

Opening the QR from a TV on a signed-in phone lands directly on the
approval step.

### Settings

Profile, language (English, Srpski, Српски), password, two-step verification with recovery codes,
playback defaults (preferred audio and subtitle languages, quality on
mobile data), and storage usage against quota.

### Sharing (Phase 8)

Share dialog on any file, folder or album: create link, optional password
and expiry, allow download. A "Shared" section lists what the user shares
and what is shared with them. The public share page is minimal, branded
with the server name, and shows only the shared content.

### Administration (Phases 1, 11)

- **Dashboard.** Storage use per location, active streams, running jobs,
  health warnings.
- **Users.** Create, role, quota, disable, reset password (with the notice
  that the user will be told).
- **Storage.** Locations with status and test; libraries with their
  location, mode and scan state. Enabling writes on an external library
  shows a warning that Atrium will then be able to delete from that
  folder.
- **Transcoding.** Hardware results, limits.
- **Jobs.** All jobs with filters, pause background work.
- **Integrations.** Metadata, subtitle and translation providers, each off
  by default and stating what it sends.
- **Backups.** List, create now, schedule, restore.
- **Integrity.** Results of the storage scrub.
- **Logs.**

Admins see sizes and counts for other users, never file names or content.

---

## Mobile (Phase 4)

### Connect

First launch: scan a QR code from the web app, or type a server address.
Scanning signs in directly. Typing leads to the sign-in screen. If the
server is on plain HTTP outside the local network, the app explains why it
cannot connect and links to the access guide.

The app holds several servers. A switcher in the account screen changes
the active one; each has its own session, settings and offline files.

### Navigation

Bottom tabs: Photos, Files, Watch (Phase 7), Account.

### Photos

Same timeline as the web, with pinch to change grid density. The viewer
supports swipe, pinch zoom, swipe down to close. Items not yet backed up
carry a small badge.

### Files

Folder browsing, search, sort. Actions through a long-press menu and
multi-select. Upload from the device's file picker, camera or photo
library. Share sheet integration: "Save to Atrium" from other apps.

### Camera backup

A setup flow, off by default:

1. Choose what to back up (photos, videos, selected device albums).
2. Choose conditions (Wi-Fi only, while charging only).
3. Grant photo access. On partial access the app explains the limitation.

The backup screen shows state honestly: items remaining, what is running,
and why it is paused ("waiting for Wi-Fi", "iOS paused background work;
open the app to continue"). Each platform limits background work
differently; the app uses what the platform allows and never claims
continuous backup where it cannot deliver it.

Backed-up items are identified by content hash so reinstalling the app
does not upload everything again. The app never deletes from the device
on its own. "Free up space" is a separate, explicit action that lists
what will be removed and only offers items the server has verified.

### Offline

"Make available offline" on files, folders, albums, movies and episodes.
A Downloads screen shows progress and space used. Downloads resume after
interruption. Offline items open without a connection; changes made on
the server appear at next sync.

### Security

Optional biometric lock on app open and after a timeout. App content is
hidden in the app switcher when the lock is on.

### Approve a device

Account, Devices, Scan: scan the TV's QR code, review, approve. Also
reachable from the system camera through the QR's link.

### Player

Platform player with the same controls as the web, plus gestures (double
tap to skip, swipe for brightness and volume), picture-in-picture,
background audio, AirPlay and Chromecast (Phase 9), and rotation lock.

---

## TV (Phase 10)

Designed for a remote at three meters: arrows, select, back, play and
pause. No screen needs text entry except the optional server address.
Focus is always visible (scale, bright outline) and never lost; back
always returns to the previous focus position. Text is large; rows scroll
horizontally, screens vertically.

### Connect and sign in

1. **Find the server.** The screen shows a QR code and the instruction
   "Open Atrium on your phone and scan". The phone app sends the server
   address and approval in one step. Alternatives listed below it: servers
   found on the network, or "Enter address".
2. **Pairing.** Once the server is known and the TV was not already
   approved by the handoff: a large code, a QR code, and "Waiting for
   approval". The code refreshes when it expires.
3. **Profile note.** After approval the TV shows whose account it is
   signed in to and any restrictions.

No password is ever typed on the TV.

### Home

Left rail (collapsed to icons until focused): Home, Movies, Shows, Photos,
Settings. Rows: Continue watching, Next up, Recently added, one row per
library. The focused item expands with title, year and progress; the
background shows its artwork.

### Library

Poster grid with a filter and sort bar reachable by pressing up. Letter
jump for long lists.

### Detail pages

- **Movie.** Backdrop, title, facts, overview, buttons: Play or Resume,
  Play from start, Audio and subtitles, Mark watched.
- **Show.** Season selector and episode row; Play resumes the right
  episode.
- **Season and episode.** Episode list with thumbnails and progress;
  episode detail with overview.

### Player

- Play and pause, left and right to skip, hold to scrub with thumbnail
  preview.
- Down opens the options row: audio, subtitles, subtitle appearance and
  offset, quality, playback info.
- Next episode card with countdown; back cancels it.
- Direct play of the original, including 4K, HEVC and HDR, whenever the
  device reports it can. The playback info panel says what is happening.
- Resume position synced with other devices.

### Photos

Albums and timeline by month as rows. Select opens a slideshow with
interval and order options; left and right step manually.

### Platform integration

Android TV: a home screen channel and the system continue watching row.
Apple TV: Top Shelf showing continue watching and recently added. Both
deep-link into the detail page or resume playback.

### Settings

Server and account shown, sign out, playback defaults, subtitle
appearance, language, about.
