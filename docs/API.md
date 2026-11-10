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

Available only until an admin exists; see DESIGN.md 6.8 for the setup
token.

| Method | Path | Notes |
|---|---|---|
| GET | `/setup/state` | Which steps are done |
| POST | `/setup/admin` | Create the first admin |
| POST | `/setup/storage/test` | Probe a location: write, read back, delete, free space |
| POST | `/setup/storage` | Save the first storage location and cache location |
| GET | `/setup/hardware` | Detected CPU, memory, hardware profile, working encoders |
| GET | `/setup/network` | The address the server sees for this request, proxy detection |
| POST | `/setup/complete` | Finish; setup endpoints close |

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
| POST | `/me/totp`, `/me/totp/confirm` | Enroll |
| DELETE | `/me/totp` | Disable, requires password |
| GET | `/me/usage` | Quota and usage |

## Users (Phase 1, admin)

| Method | Path | Notes |
|---|---|---|
| GET, POST | `/users` | List, create |
| GET, PATCH, DELETE | `/users/{id}` | Role, quota, disable. Delete moves nothing; content handling is an explicit choice in the request |
| POST | `/users/{id}/reset-password` | Logged and shown to the user |

## Storage locations and libraries (Phase 1 to 3, admin)

| Method | Path | Notes |
|---|---|---|
| GET, POST | `/storage-locations` | Secrets are write-only |
| GET, PATCH, DELETE | `/storage-locations/{id}` | Delete only when no library uses it |
| POST | `/storage-locations/{id}/check` | Run the probe and compatibility tests |
| GET, POST | `/libraries` | Non-admins see libraries they can access |
| GET, PATCH, DELETE | `/libraries/{id}` | Deleting an external library removes only the index |
| GET, PUT | `/libraries/{id}/grants` | Who can read or write |
| POST | `/libraries/{id}/scan` | Enqueue a scan job |

## Files and folders (Phase 2)

| Method | Path | Notes |
|---|---|---|
| GET | `/nodes/{id}` | Metadata |
| GET | `/nodes/{id}/children` | Sort by name, date, size, type; filter by type |
| GET | `/nodes/{id}/path` | Ancestors for breadcrumbs |
| POST | `/nodes` | Create a folder |
| PATCH | `/nodes/{id}` | Rename or move (`name`, `parentId`) |
| POST | `/nodes/{id}/copy` | Copy; no bytes move in a managed library |
| DELETE | `/nodes/{id}` | Move to trash |
| POST | `/nodes/batch` | Move, copy or trash many, one result per item |
| GET | `/nodes/{id}/content` | Download or direct play; `Range` supported |
| POST | `/nodes/{id}/content-url` | Issue a signed URL |
| POST | `/downloads/archive` | Several nodes or a folder as a streamed ZIP |
| GET | `/trash` | The user's trashed items |
| POST | `/trash/{id}/restore` | Back to the original parent, or a given one if it is gone |
| DELETE | `/trash/{id}`, `/trash` | Remove permanently, empty trash |
| GET | `/changes?cursor=` | Change feed for sync and offline copies |

Uploads use the tus protocol with the creation, termination, checksum and
expiration extensions:

| Method | Path | Notes |
|---|---|---|
| POST | `/uploads` | Create. Metadata: parent ID, name, modified time, optional SHA-256, conflict policy (`fail`, `rename`, `replace`) |
| HEAD | `/uploads/{id}` | Current offset, for resume |
| PATCH | `/uploads/{id}` | Append bytes |
| DELETE | `/uploads/{id}` | Cancel |
| POST | `/uploads/check` | Which of these hashes the user already has (own files only, DESIGN.md 3.3) |

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
| GET | `/timeline/buckets` | Counts per month, so the client can lay out a scrollbar for a huge library |
| GET | `/timeline?bucket=` | Items in a bucket |
| GET | `/nodes/{id}/thumbnail?size=` | Signed URL allowed |
| GET | `/nodes/{id}/preview` | Larger image, or video preview clip |
| GET | `/nodes/{id}/media-info` | EXIF or stream details |
| GET, POST | `/albums` | |
| GET, PATCH, DELETE | `/albums/{id}` | |
| POST, DELETE | `/albums/{id}/items` | Add, remove |
| GET | `/search` | `q`, `type`, date range, `near` or bounding box, library |
| GET | `/map/clusters` | Photo clusters for a bounding box and zoom |

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
| GET | `/jobs` | Filter by state and type; own jobs, or all for admins |
| GET | `/jobs/{id}` | Progress, error |
| POST | `/jobs/{id}/cancel`, `/jobs/{id}/retry` | |

## Administration (Phase 1 basics, Phase 11 complete)

| Method | Path | Notes |
|---|---|---|
| GET, PATCH | `/admin/settings` | Limits, retention, providers; secrets write-only |
| GET | `/admin/hardware` | Profile, encoders, last detection result |
| POST | `/admin/hardware/detect` | Rerun detection |
| GET | `/admin/storage/usage` | Per location and per user totals |
| GET | `/admin/sessions` | All sessions; revoke |
| GET | `/admin/transcodes` | Active streams and why each is transcoding |
| GET | `/admin/logs` | Redacted |
| GET, POST | `/admin/backups` | List, create |
| POST | `/admin/backups/{id}/restore` | Phase 11 |
| GET | `/admin/integrity` | Scrub results: mismatched, missing and unknown objects |
| POST | `/admin/libraries/{id}/relocate` | Move to another storage location, Phase 11 |
