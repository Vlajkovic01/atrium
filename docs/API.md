# Atrium API sketch

Status: Phase 0 draft. This is a sketch of endpoints, not a contract. The
contract is the OpenAPI document generated from the server from Phase 1
on; clients are generated from it.

## Conventions

- Base path `/api/v1`. JSON bodies, UTF-8. See DESIGN.md 8.3 for
  versioning rules.
- Authentication: `Authorization: Bearer <access token>`, or a signed URL
  on media endpoints (DESIGN.md 6.6). Endpoints marked *public* need
  neither.
- Errors: `application/problem+json` with a stable `code`
  (`node.name_conflict`, `auth.invalid_credentials`) and `params`. Clients
  translate the code; the server sends no display text.
- Lists use cursor pagination: `?cursor=&limit=`, response
  `{ items, nextCursor }`.
- Edits use optimistic concurrency: resources carry `version`, mutations
  send `If-Match`, a mismatch returns 412.
- Creating requests accept `Idempotency-Key` so a retried request does not
  create twice.
- File content supports `Range`, `ETag` and conditional requests.
- IDs are opaque strings. Clients never build storage paths.

Each section notes the phase in which it is built.

## System (Phase 1)

| Method | Path | Notes |
|---|---|---|
| GET | `/system/info` | *Public.* Server name, version, minimum client version, setup complete, sign-in methods |
| GET | `/health/live`, `/health/ready` | *Public.* For containers and monitors; no details |
| GET | `/events` | Server-sent events for the signed-in session: job progress, changes, pairing requests |

## Setup (Phase 1)

See DESIGN.md 6.8. Only creating the first admin is special. After that
the wizard is signed in as the admin and uses the storage endpoints below,
the same ones the admin interface uses later.

| Method | Path | Notes |
|---|---|---|
| GET | `/setup/state` | *Public.* Admin exists, storage configured, completed, whether this client needs the setup token |
| POST | `/setup/admin` | *Public while no admin exists.* Creates the first admin; needs the setup token from the server log |
| POST | `/setup/complete` | Admin. Finishes setup; refused until a storage location exists |
| GET | `/admin/hardware` | Not built yet. Detected CPU, memory, hardware profile, working encoders |
| GET | `/admin/network` | Not built yet. The address the server sees for this request, proxy detection |

## Authentication and sessions (Phase 1)

| Method | Path | Notes |
|---|---|---|
| POST | `/auth/login` | Name and password. Returns tokens, or a challenge when TOTP is on |
| POST | `/auth/login/totp` | Complete the challenge with a code or recovery code |
| POST | `/auth/refresh` | Rotate the refresh token |
| POST | `/auth/logout` | Revoke the current session |
| GET | `/sessions` | The user's devices |
| DELETE | `/sessions/{id}` | Revoke a device |

Device pairing (DESIGN.md 6.4):

| Method | Path | Notes |
|---|---|---|
| POST | `/auth/pairing` | *Public.* Device starts; returns device code, user code, QR URL, expiry, poll interval |
| POST | `/auth/pairing/token` | *Public.* Device polls with the device code; returns pending, denied, expired or tokens |
| GET | `/auth/pairing/{userCode}` | Signed-in user looks up a request: device name, kind, address |
| POST | `/auth/pairing/{userCode}/approve` | Approve, with optional restrictions for the new session |
| POST | `/auth/pairing/{userCode}/deny` | Deny |

Linking a phone by QR (DESIGN.md 6.5):

| Method | Path | Notes |
|---|---|---|
| POST | `/auth/link-codes` | Signed-in session creates a single-use code |
| POST | `/auth/link-codes/redeem` | *Public.* New device exchanges it for tokens |

## Current user (Phase 1)

| Method | Path | Notes |
|---|---|---|
| GET, PATCH | `/me` | Profile, locale, preferences |
| POST | `/me/password` | Change password; revokes other sessions |
| POST | `/me/totp`, `/me/totp/confirm` | Enroll; confirming returns the recovery codes once |
| POST | `/me/totp/disable` | Disable, requires the password in the body |
| GET | `/me/usage` | Quota and usage |

## Users (Phase 1, admin)

| Method | Path | Notes |
|---|---|---|
| GET, POST | `/users` | List, create |
| GET, PATCH | `/users/{id}` | Role, quota, disable. `PATCH` requires `If-Match` with the version read. The last active admin cannot be demoted or disabled |
| GET | `/users/{id}/deletion` | What deleting would remove: file and folder counts and bytes, never names (D-004); and the schedule if one is set |
| POST | `/users/{id}/deletion` | Schedule deletion (D-025). Body `confirmName` must match the account's name. Disables the account and signs it out at once; files are removed 30 days later. Not for yourself or the last active admin |
| DELETE | `/users/{id}/deletion` | Cancel a scheduled deletion. The account stays disabled until enabled with `PATCH`, which is refused while a deletion is scheduled |
| POST | `/users/{id}/reset-password` | Logged and shown to the user. Signs the user out everywhere |

Restricted sessions (a paired device approved as read-only or with the
personal library hidden) cannot change the account, administer users,
approve pairings or create link codes.

## Storage locations and libraries (Phase 1 to 3, admin)

| Method | Path | Notes |
|---|---|---|
| GET, POST | `/storage-locations` | Secrets are write-only. Saving tests the location, refuses one already used by an Atrium server, and writes its identity file |
| POST | `/storage-locations/test` | Try settings without saving. Never creates a directory. Always 200 with the outcome inside |
| GET, PATCH, DELETE | `/storage-locations/{id}` | Not built yet. Delete only when no library uses it |
| POST | `/storage-locations/{id}/check` | Verify the identity, then the probe; records online or offline |
| GET, POST | `/libraries` | `GET` built in Phase 2: the caller's libraries, with the personal one created on first use and its root folder ID. A session approved with "hide my personal files" does not see it; a read-only session sees every library as read-only |
| GET, POST | `/admin/libraries` | Built in Phase 3. Non-personal libraries only (D-004), with path, state, file count, who has access and a running scan. `POST` adds an external folder: `name`, `path`, `userIds`; it is checked first (D-035) and scanned at once |
| POST | `/admin/libraries/check-path` | Try a folder before adding it. Reads only; always 200 with `ok`, `errorCode` and the number of entries |
| PATCH, DELETE | `/admin/libraries/{id}` | Rename or replace `userIds` (`If-Match`). Delete needs `confirmName` and removes only Atrium's index, never a file |
| POST | `/admin/libraries/{id}/scan` | Enqueue a scan job; returns 202 with the job |
| PUT | `/admin/libraries/{id}/writes` | `enabled`, and `confirmName` (the library's name) to enable. Lets Atrium change the folder's files (D-036). Disabling takes effect at once. `PATCH` takes `writerIds`, those of `userIds` who may change files |

## Files and folders (Phase 2)

| Method | Path | Notes |
|---|---|---|
| GET | `/nodes/{id}` | Metadata, with `ETag` set to the version |
| GET | `/nodes/{id}/children` | Folders first, then `sort` = `name`, `modified`, `size` or `type`, `descending`; filter by `kind`. Cursor pagination with a total |
| GET | `/nodes/{id}/path` | Ancestors for breadcrumbs, root first |
| POST | `/nodes` | Create a folder. 409 `node.name_conflict` carries `existingId`, so a retried request can recognize its own folder |
| PATCH | `/nodes/{id}` | Rename or move (`name`, `parentId`). Requires `If-Match` |
| POST | `/nodes/{id}/copy` | Copy; no bytes move in a managed library. Without a name in the same folder the copy is numbered. At most 10,000 items |
| DELETE | `/nodes/{id}` | Move to trash; `If-Match` optional |
| POST | `/nodes/batch` | Move, copy or trash up to 1,000 items, one result per item |

Someone else's node answers 404 for every operation, the same as a node
that does not exist; this includes admins (D-004). A full quota answers
507 `files.quota_exceeded` with `quotaBytes`, `usedBytes` and
`neededBytes`.
| GET, HEAD | `/nodes/{id}/content` | Download or direct play. One `Range` per request (several answer with the whole file), `If-Range`, `If-None-Match`; the `ETag` is the content's SHA-256. Bearer token or signed URL |
| POST | `/nodes/{id}/content-url` | Issue a signed URL valid for one hour. `inline: true` asks to show rather than save; `versionId` signs an earlier version |
| GET | `/nodes/{id}/versions/{version}/content` | An earlier version, always as a download |
| POST | `/downloads/archive` | Up to 1,000 files and folders; returns a signed URL that streams the ZIP. Folders include everything in them not in the trash, at most 50,000 items |

Content is always served with `Content-Security-Policy: sandbox` and
`nosniff`. It is shown inline only for images, video, audio, PDF and
plain text, and only when asked; everything else, including HTML and
SVG, is a download with the type `application/octet-stream`.
| GET | `/trash` | The user's trashed items with size, item count and the date retention removes them |
| POST | `/trash/{id}/restore` | Back to the original parent, or to `parentId`, which is required when the original folder is in the trash too. `onConflict`: `fail` or `rename` |
| DELETE | `/trash/{id}`, `/trash` | Remove permanently, empty trash. Rows only; content follows through the blob collector |
| GET | `/changes?cursor=` | Change feed for sync and offline copies. Moved to Phase 4 (D-027) |
| GET | `/me/usage` | Used bytes split into files, trash and versions, and the quota |

Uploads use the tus protocol with the creation, termination, checksum and
expiration extensions:

| Method | Path | Notes |
|---|---|---|
| OPTIONS | `/uploads` | *Public.* tus discovery |
| POST | `/uploads` | Create. `Upload-Metadata`: `parentId`, `name`, optional `modifiedAt` (Unix milliseconds), `sha256` (hex), `onConflict` (`fail`, `rename`, `replace`). Name, folder, conflict and quota are checked here and again at the end. The response carries `Atrium-Chunk-Size` |
| HEAD | `/uploads/{id}` | Current offset, for resume. `Atrium-Upload-State`, and `Atrium-Node-Id` once finished |
| PATCH | `/uploads/{id}` | Append one chunk (D-028), optionally with `Upload-Checksum` (`sha1` or `sha256`; 460 on mismatch). The last chunk finishes the upload and the response carries `Atrium-Node-Id` |
| DELETE | `/uploads/{id}` | Cancel |
| GET | `/uploads`, `/uploads/{id}` | The caller's active uploads, so a reloaded page can resume; one upload as JSON |
| POST | `/uploads/check` | Which of these hashes the user already has (own files only, D-010) |

An upload nobody continues for 24 hours expires and its parts are
removed. Content whose SHA-256 differs from the declared one answers 422
`upload.content_mismatch` and never becomes a file.

`replace` keeps the previous content as a version of the file; it never
overwrites (DESIGN.md 4.2).

| Method | Path | Notes |
|---|---|---|
| GET | `/nodes/{id}/versions` | Earlier versions with size and date |
| GET | `/nodes/{id}/versions/{version}/content` | Download an earlier version |
| POST | `/nodes/{id}/versions/{version}/restore` | Make it current; the current content becomes a version |
| DELETE | `/nodes/{id}/versions/{version}` | Remove one version |

## Photos, albums, search (Phase 3)

| Method | Path | Notes |
|---|---|---|
| GET | `/timeline/buckets` | Built in Phase 3. Counts per month, newest first, so the client can lay out a scrubber for a huge library. Optional `libraryId` |
| GET | `/timeline` | Built in Phase 3. Photos and videos newest first, keyset `cursor`, `limit` up to 500, optional `from` and `to` (local time, D-034) and `libraryId`. Each item has its upright width and height and a signed thumbnail URL |
| GET | `/nodes/{id}/thumbnail?size=` | Built in Phase 3. `small` (grid, 400 px) or `large` (viewer, 1,920 px), WebP without metadata, made on first request if needed. Bearer token or signed URL |
| GET | `/nodes/{id}/preview` | Built in Phase 3. A video's four-second silent MP4 clip. Bearer token or signed URL |
| GET | `/nodes/{id}/media-info` | Built in Phase 3. Size, duration, format, codecs, capture time and offset, camera, thumbnail state. No location (D-031) |
| GET, POST | `/albums` | Built in Phase 3. The caller's albums with count, cover and date range; create with `name` and optional `nodeIds` |
| GET, PATCH, DELETE | `/albums/{id}` | Rename or choose the cover (`If-Match`). Deleting removes the album only |
| GET | `/albums/{id}/items` | The items the caller can still see, newest first |
| POST | `/albums/{id}/items`, `/albums/{id}/items/remove` | Add or remove up to 1,000 `nodeIds`; only photos and videos the caller can read |
| GET | `/search` | Built in Phase 3. `q` (words in the name, ignoring case and diacritics), `type` (`photo`, `video`, `document`, `folder`), `from`, `to`, `libraryId`, `cursor`. No location (D-031) |

## Playback (Phase 5)

| Method | Path | Notes |
|---|---|---|
| POST | `/playback/sessions` | Body: node or item, capability profile, preferred audio and subtitle, start position. Returns the decision, the reasons and stream URLs |
| PATCH | `/playback/sessions/{id}` | Change audio, subtitle, quality; may change the decision |
| POST | `/playback/sessions/{id}/progress` | Position and state; keeps the session and its URLs alive |
| DELETE | `/playback/sessions/{id}` | Stop; frees the transcoder |
| GET | `/stream/{session}/master.m3u8` | Signed. HLS master playlist |
| GET | `/stream/{session}/{variant}/index.m3u8` | Signed. Full playlist from the first request |
| GET | `/stream/{session}/{variant}/{segment}` | Signed. Produced on demand |
| GET, PUT | `/playback/state/{nodeOrItemId}` | Resume position, watched flag |

## Subtitles and audio (Phase 6)

| Method | Path | Notes |
|---|---|---|
| GET | `/nodes/{id}/subtitles` | All tracks, every source |
| GET | `/nodes/{id}/subtitles/{track}?format=vtt` | Converted on demand, with optional time offset |
| POST | `/nodes/{id}/subtitles` | Upload a sidecar file into Atrium's metadata |
| GET | `/nodes/{id}/subtitles/search` | Query configured providers |
| POST | `/nodes/{id}/subtitles/download` | Fetch a search result |
| POST | `/nodes/{id}/subtitles/generate` | Speech-to-text job |
| POST | `/nodes/{id}/subtitles/{track}/translate` | Translation job |

## Movies and series (Phase 7)

| Method | Path | Notes |
|---|---|---|
| GET | `/home` | Continue watching, next up, recently added, libraries; one call for the TV and mobile home screens |
| GET | `/items` | Filter by library, kind, genre, year, watched; sort |
| GET | `/items/{id}` | Movie, show, season or episode with files and artwork |
| GET | `/items/{id}/children` | Seasons of a show, episodes of a season |
| GET | `/items/{id}/next` | Next episode |
| PATCH | `/items/{id}` | Edit fields; edited fields become locked |
| POST | `/items/{id}/match` | Search providers and pick the right match |
| POST | `/items/{id}/refresh` | Refetch metadata and artwork |
| POST | `/items/{id}/watched`, DELETE | Mark watched, unwatched |

## Sharing (Phase 8)

| Method | Path | Notes |
|---|---|---|
| GET, POST | `/shares` | Create with expiry, password, permissions |
| PATCH, DELETE | `/shares/{id}` | |
| GET | `/public/shares/{token}` | *Public.* Metadata; password challenge when set |
| POST | `/public/shares/{token}/unlock` | *Public.* Exchange the password for a short-lived share token |
| GET | `/public/shares/{token}/...` | *Public.* Browse and download within the share only |
| GET | `/activity` | Own activity; admins see events without personal content names |

## Jobs (Phase 2)

| Method | Path | Notes |
|---|---|---|
| GET | `/jobs` | Filter by state and type, newest first, cursor pagination. Own jobs, or all for admins. An admin sees the error code of another user's job but not its detail (D-004) |
| GET | `/jobs/{id}` | Progress, error |
| POST | `/jobs/{id}/cancel` | A queued job is cancelled at once, a running one at its next heartbeat. 409 `job.not_active` when it has finished |
| POST | `/jobs/{id}/retry` | Only a failed or cancelled job; starts with fresh attempts. 409 `job.not_retryable`, or `job.already_queued` when an identical job is active |

Cancel and retry need an unrestricted session. Payloads are never
returned.

## Administration (Phase 1 basics, Phase 11 complete)

| Method | Path | Notes |
|---|---|---|
| GET, PATCH | `/admin/settings` | Limits, retention, providers; secrets write-only |
| GET | `/admin/hardware` | Encoders and last detection result, Phase 5 |
| GET | `/admin/background-work` | Built in Phase 3. Hardware profile, cores and memory, per resource class the default, override and effective limit with running and waiting counts, and the pause |
| PUT | `/admin/background-work/limits` | Replace the overrides: a list of `resourceClass` and `limit` (1 to 32, null for the default) |
| POST | `/admin/background-work/pause`, `/admin/background-work/resume` | Pause background and idle jobs for `minutes` (1 to 10080) or, with null, until resumed |
| POST | `/admin/hardware/detect` | Rerun detection |
| GET | `/admin/storage/usage` | Per location and per user totals |
| GET | `/admin/sessions` | All sessions; revoke |
| GET | `/admin/transcodes` | Active streams and why each is transcoding |
| GET | `/admin/logs` | Redacted |
| GET, POST | `/admin/backups` | List, create. Built in Phase 2; creating returns 202 with the job to follow |
| POST | `/admin/backups/{id}/restore` | Phase 11 |
| GET | `/admin/integrity` | Scrub results: damaged, missing and unknown objects, and unreachable locations. Built in Phase 2 |
| POST | `/admin/integrity/check` | Run the weekly check now. Built in Phase 2 |
| POST | `/admin/libraries/{id}/relocate` | Move to another storage location, Phase 11 |
