# Atrium design

Status: Phase 0 design, revised 2026-11-10 after the owner's review. No
implementation exists yet.

Atrium is a self-hosted personal cloud and media server: files, photos and
video from one server, with web, mobile and TV clients on one API.

Priorities, in order: safety of the user's files, security, ease of
installation, reliability, user experience, performance. When two of them
conflict, the earlier one wins. Choices in this document that are not
obvious are recorded with alternatives in [DECISIONS.md](DECISIONS.md) and
referenced as D-nnn.

Contents:

1. Architecture
2. Data model
3. Storage
4. File safety
5. Job system
6. Security model
7. Media pipeline
8. Upgrades, backup and API versioning
9. Internationalization
10. External services and privacy
11. Testing
12. Risks that can lose or expose data

---

## 1. Architecture

### 1.1 Shape

One server process, one container (D-009). It contains:

- the REST API (ASP.NET Core, `/api/v1`),
- the built web app, served as static files,
- the job workers, as hosted services in the same process,
- the transcode manager, which starts and supervises FFmpeg processes.

There is no clustering. Exactly one server instance owns a database. A
second instance pointed at the same database refuses to start (it finds a
live instance lease in the database).

```
 web / mobile / TV clients
          |  HTTPS (optional Caddy, or the user's own proxy or tunnel)
          v
 +----------------------- atrium container -----------------------+
 |  API  |  static web app  |  job workers  |  transcode manager   |
 |                                      |            |             |
 |                                  ffprobe       ffmpeg           |
 +---------+----------------+-----------------+--------------------+
           |                |                 |
       database         storage           cache location
   (SQLite file or     locations        (thumbnails, segments,
     PostgreSQL)    (local, share, S3)    rebuildable)
```

### 1.2 Three kinds of data

Every byte Atrium stores belongs to exactly one of these. The distinction
drives backup, deletion and recovery rules.

| Kind | What | Where | If lost |
|---|---|---|---|
| User data | Uploaded files; existing folders that libraries point at | Storage locations | Unrecoverable. Protected by section 4. |
| Metadata | Database, server keys, settings, fetched artwork, downloaded or generated subtitles | Config volume, plus backups copied to a storage location | Names, folders, albums and watch state are lost; recoverable from backup. |
| Cache | Thumbnails, previews, transcoded segments, search indexes | Cache location | Nothing. Rebuilt on demand or by jobs. |

Generated subtitles and fetched artwork are metadata, not cache, because
they are slow or impossible to regenerate offline.

### 1.3 Container and volumes

The default `docker-compose.yml` starts one service with three volumes:

| Mount | Content |
|---|---|
| `/config` | SQLite database, server keys, metadata files, local backups |
| `/data` | Default storage location (the wizard can add others) |
| `/cache` | Cache location |

Two opt-in Compose profiles: `postgres` (PostgreSQL instead of SQLite) and
`https` (Caddy with automatic certificates, D-006). Images are built for
`linux/amd64` and `linux/arm64` (D-003).

Only bootstrap values come from environment variables: config directory,
database connection, listen address. Everything else is stored in the
database and edited through the wizard or admin UI, so a normal install
never edits a file.

### 1.4 Server projects

A modular monolith. Module boundaries are enforced by project references,
not by network calls.

| Project | Responsibility |
|---|---|
| `Atrium.Api` | Host, endpoints, authentication, authorization, OpenAPI |
| `Atrium.Core` | Domain model and rules, no I/O |
| `Atrium.Data` | EF Core context, repositories |
| `Atrium.Data.Sqlite`, `Atrium.Data.Postgres` | One migration set each |
| `Atrium.Storage` | Provider interface, local and S3 providers, blob store |
| `Atrium.Jobs` | Queue, scheduler, workers |
| `Atrium.Media` | ffprobe and FFmpeg wrappers, playback decisions, transcode manager |
| `*.Tests` | Unit and integration tests beside each project |

### 1.5 Clients

| Path | Content |
|---|---|
| `/packages/shared` | API client generated from the OpenAPI document, domain logic (capability profiles, upload state machine, sync), player core (state machine, track selection, progress reporting), translations |
| `/web` | React app |
| `/mobile` | React Native app for Android and iOS |
| `/tv` | react-native-tvos app for Android TV and Apple TV, reusing mobile code where the UI is not involved |

The player core is engine-independent logic. Rendering uses the platform
player on each client (D-007): the browser's media element with an HLS
library on the web, Media3/ExoPlayer on Android, AVPlayer on Apple.

### 1.6 Small-device budget

Target floor: 4 ARM64 cores, 2 GB RAM (D-003).

- Server process steady state under 400 MB; the .NET garbage collector is
  configured to conserve memory with a hard heap limit derived from
  container memory.
- All file and media I/O is streamed. Nothing is buffered whole in memory.
- A hardware profile (small, medium, large) is detected at startup from
  cores and memory and sets job concurrency defaults (section 5.4).
- Interactive work always preempts background work.

---

## 2. Data model

Conventions: primary keys are UUIDv7 (time-ordered, identical in SQLite and
PostgreSQL). Timestamps are UTC, stored as milliseconds since the Unix
epoch, and enums are stored by name (D-020). Rows that clients edit carry a `version`
used for optimistic concurrency. The schema is the same on both databases
except for full-text search, which sits behind an interface (SQLite FTS5,
PostgreSQL `tsvector`).

### 2.1 Identity and access

| Entity | Key fields | Notes |
|---|---|---|
| `User` | name, display name, password hash, role, locale, quota bytes, disabled | Roles: `admin`, `user`, `guest` (read-only) |
| `TotpCredential` | user, encrypted secret, confirmed at | Plus hashed single-use recovery codes |
| `Session` | user, device name, device kind, platform, app version, created, last seen, last IP, revoked at | One per signed-in device |
| `RefreshToken` | session, token hash, issued, used at, replaced by | Rotation chain, section 6.2 |
| `PairingRequest` | device code hash, user code, device info, state, expires, approved by, session | Section 6.4 |
| `LinkCode` | code hash, user, expires, used at | QR sign-in for phones, section 6.5 |
| `LibraryGrant` | library, user, access (`read`, `write`) | Access to non-personal libraries |

### 2.2 Storage and libraries

| Entity | Key fields | Notes |
|---|---|---|
| `StorageLocation` | name, provider kind, encrypted config, state, last check | Local path, network share, or S3-compatible bucket |
| `Library` | name, kind, mode, storage location, root, owner | See below |
| `Blob` | storage location, SHA-256, size, state, created | One stored object in a managed library |
| `Node` | library, parent, name, name key, type, blob or external path, size, modified, created, owner, trashed at, original parent, version | A file or folder |
| `NodeVersion` | node, blob, size, modified, created, created by | Earlier content of a file, kept when it is replaced (D-019) |
| `Upload` | user, target folder, name, declared length, offset, state, provider upload state, expires | A tus upload in progress |
| `ChangeLogEntry` | sequence, library, node, change kind | Feeds client sync and offline copies |

Library `mode`:

- `managed`: Atrium owns the bytes. Files are content-addressed blobs
  (D-002). Names and folders exist only in the database.
- `external`: an existing folder that Atrium indexes. Read-only by
  default. Nodes carry a relative path instead of a blob.

Library `kind`:

| Kind | Mode | Purpose |
|---|---|---|
| `personal` | managed | One per user, private. "My files", including camera uploads. |
| `shared` | managed | A shared space with grants. |
| `photos` | external | An existing photo folder, shown in the timeline. |
| `movies`, `shows` | external or managed | Video library with metadata. |

The photo timeline is a view, not a library: it shows every photo and video
node the user can read across their personal library and `photos`
libraries.

`Node` rules:

- Tree by `parent`. Unique on (`parent`, `name key`) among non-trashed
  nodes. `name key` is the NFC-normalized, case-folded name: names preserve
  case but two siblings cannot differ only by case (D-011).
- Moves check for cycles. A move is a metadata change only.
- Several nodes may reference one blob (deduplication, copies).

### 2.3 Media

| Entity | Key fields | Notes |
|---|---|---|
| `MediaInfo` | content key, kind, width, height, duration, container, streams (JSON), taken at, latitude, longitude, camera, orientation | Technical facts from EXIF and ffprobe. Keyed by content, so duplicates share it. |
| `Derivative` | content key, kind, variant, cache key, state | Index of thumbnails and previews in the cache |
| `Album`, `AlbumItem` | owner, name, cover; node, position | |
| `MediaItem` | library, kind (`movie`, `show`, `season`, `episode`), parent, title, sort title, year, numbers, overview, provider ids, locked fields | Field locks keep user edits from being overwritten by a refresh |
| `MediaItemFile` | item, node, part, edition | A movie can have several files |
| `Artwork` | item, kind, language, metadata file key | |
| `SubtitleTrack` | node, source (`embedded`, `sidecar`, `downloaded`, `generated`, `translated`), language, format, forced, hearing impaired, location | |
| `PlaybackState` | user, node or item, position, duration, watched, play count, updated at, updated by session | Last write wins on `updated at` |

`content key` is the blob hash in managed libraries and a fingerprint
(library, path, size, modification time) in external ones, replaced by the
content hash once the optional background hashing job has run.

### 2.4 Jobs, sharing, system

| Entity | Key fields |
|---|---|
| `Job` | type, payload, priority, resource class, state, progress, attempts, run after, lease owner, lease expires, checkpoint, dedupe key, parent job, error |
| `Share` | token hash, target (node or album), creator, password hash, expires, permissions, download count |
| `ActivityEvent` | actor, session, action, target, time, IP |
| `Setting` | key, value (JSON), secret flag |
| `BackupRecord` | time, reason, schema version, location, size, checksum |

---

## 3. Storage

### 3.1 Provider interface

One interface, three implementations: local filesystem, mounted network
share, S3-compatible object storage. Keys are relative, forward-slash
paths. The provider never sees user-supplied names for managed libraries,
only blob keys.

```csharp
public interface IStorageProvider
{
    StorageCapabilities Capabilities { get; }

    // Streamed read. Range is optional; providers must honor it.
    Task<Stream> OpenReadAsync(string key, ByteRange? range, CancellationToken ct);

    // Streamed single-shot write. Atomic: the key either holds the complete
    // new content or is unchanged. Fails if the key exists unless overwrite.
    Task<StorageObject> WriteAsync(string key, Stream content, bool overwrite, CancellationToken ct);

    // Multipart upload for large or resumable writes. Nothing is visible
    // under the key until the upload is completed.
    Task<MultipartUpload> BeginMultipartAsync(string key, CancellationToken ct);
    Task<UploadedPart> UploadPartAsync(MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken ct);
    Task<StorageObject> CompleteMultipartAsync(MultipartUpload upload, IReadOnlyList<UploadedPart> parts, bool overwrite, CancellationToken ct);
    Task AbortMultipartAsync(MultipartUpload upload, CancellationToken ct);

    IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken ct);
    Task<StorageObject?> StatAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task MoveAsync(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct);

    // Wizard and health checks: write, read back, delete a probe object;
    // report free space where the backend can tell.
    Task<StorageHealth> CheckAsync(CancellationToken ct);
}
```

`StorageCapabilities` tells callers what the backend can do: atomic move,
free-space reporting, minimum and maximum multipart part size. Read-only
and change-notification flags are added when external libraries arrive.

Both implementations run the same contract test suite, the local one on
every machine and the S3 one against a real S3-compatible server in CI.

"Fails if the key exists" is exact on the local filesystem. On S3 it is a
check followed by a write, which two simultaneous writers can both pass.
This is accepted because managed objects are named by their content hash:
two writers of one key write the same bytes.

| Provider | Notes |
|---|---|
| Local filesystem | Write to a temp file in the same filesystem, flush to disk, rename, flush the directory. Move is a rename. |
| Network share | The local provider with different assumptions: no change notifications, rename may not be atomic so writes are verified by reading back, and availability is checked before every scan (section 4.4). |
| S3-compatible | Multipart upload completes atomically. No rename: move is a server-side copy, a size check of the copy, and only then a delete of the source, using multipart copy above 5 GB. Each part is sent with a Content-MD5 checksum the service verifies; the newer SDK checksum headers are turned off because several compatible services reject them. Tested in CI against SeaweedFS, an S3-compatible server; the quirks of AWS S3, Backblaze B2, Cloudflare R2 and MinIO are covered by a compatibility test suite the user can run from the admin UI. |

### 3.2 Layout of a managed storage location

```
atrium/
  location.json            identity of this location (id, created, format version)
  blobs/ab/cd/<sha256>     file content
  staging/<upload id>      uploads in progress
  backups/                 copies of metadata backups (section 8.2)
```

`location.json` lets the server prove it is looking at the right place and
not at an empty mount point. The format version leaves room for
per-location encryption later without moving existing blobs (D-005).

### 3.3 Deduplication

Deduplication is per storage location, by SHA-256 of the content.

Privacy rule (D-010): deduplication across users is invisible. A client may
ask "do I already have these hashes?" and the answer covers only that
user's own files. A file another user already stored is still uploaded in
full, then collapsed on the server. Otherwise any user could test whether
a given file exists on the server.

Quota counts logical size per user, including trashed items and earlier
versions, so deduplication never changes what a user is charged.

### 3.4 Cache location

A local directory. Keys are derived from the content key and variant, so
entries never need invalidation: content changes produce a new key. The
cache has a size cap with least-recently-used eviction for transcode
segments and no eviction for thumbnails by default. Deleting the whole
directory at any time is supported; the server recreates what is requested
and schedules the rest at low priority.

### 3.5 Path validation

Applies wherever a client-supplied name or path reaches storage (external
libraries, wizard path selection, sidecar files).

- Names: reject empty, `.`, `..`, path separators, NUL and control
  characters, and names reserved on Windows.
- Paths: normalize, resolve against the library root, and verify the result
  is inside the root. Symbolic links that lead outside the root are not
  followed.
- Clients address nodes by ID. The API accepts paths only where there is no
  ID yet (setup, admin library creation).

---

## 4. File safety

### 4.1 Upload write path (managed libraries)

1. The client creates a tus upload with the target folder, name, length
   and, if it has one, the SHA-256 of the content.
2. Bytes stream into `staging/<upload id>` (a file locally, a multipart
   upload on S3). The offset is persisted after each chunk is durable, so
   the upload resumes after a network drop or server restart.
3. On the last byte the server finalizes:
   - computes the SHA-256 of what is stored. Locally this is a read-back of
     the staged file, which also verifies what reached the disk. On S3 the
     running hash is used, with each part verified by the service; if the
     running hash was lost in a restart, the object is read back;
   - if the client declared a hash and it differs, the upload fails and the
     staged data is discarded;
   - if the blob already exists with the same size, the staged data is
     discarded; otherwise it is moved to `blobs/...`;
   - one database transaction creates the `Blob` (if new) and the `Node`.
4. A crash between the move and the transaction leaves an unreferenced
   blob. It is harmless and is removed by the collector (4.3).

A node never points at a blob whose content has not been verified.

### 4.2 Trash and versions

Deleting a node sets `trashed at` and remembers the original parent. Trash
is per user, has a retention period (default 30 days, configurable), and
counts toward quota. Emptying the trash or reaching retention removes the
node rows. It does not delete content directly.

Replacing a file's content never overwrites anything. The node points at
the new blob and the previous blob becomes a `NodeVersion` (D-019).
Versions are kept by age and count (default 30 days and 10 versions,
configurable), count toward quota, and can be restored or downloaded.
Restoring a version makes the current content a version in turn. Because
content is addressed by hash, a version is one extra row pointing at a
blob that already exists; uploading identical content creates no version.

### 4.3 Blob collection

A blob is deleted only by the collector job, in two passes:

1. Blobs referenced by no node and no version are marked `pending delete`
   with a timestamp.
2. After a grace period (default 7 days), the collector checks again
   inside a transaction that no node or version references the blob, then
   deletes the object and the row.

An upload that produces the same hash during the grace period revives the
blob. The grace period also covers restoring a metadata backup that is a
few days old: its nodes still find their blobs.

### 4.4 External libraries

- Read-only by default. Atrium never creates, renames, modifies or deletes
  anything in the folder. Thumbnails, artwork, subtitles and metadata are
  stored in Atrium's own cache and metadata areas.
- An admin can enable writes per library. Deletes then move the file into
  a trash folder on the same filesystem inside the library root.
- Missing-folder guard: before a scan, the root must exist and be
  readable. If the root is missing or empty, or more than a threshold of
  known files have vanished, the library is marked offline and nothing is
  removed from the index. Vanished files are marked missing and kept, with
  their watch state and metadata, for a retention period. A network share
  that drops for a day must not wipe a library's history.

### 4.5 Consistency checks

A low-priority scrub job walks blobs, re-hashes them and reports
mismatches and missing objects in the admin UI. It never repairs or
deletes on its own. A second check lists objects on storage that the
database does not know.

---

## 5. Job system

### 5.1 Model

Jobs are rows in the database. Workers in the server process claim them.
Nothing is held only in memory, so a restart loses no work.

| Field | Meaning |
|---|---|
| Priority | `interactive`, `user`, `background`, `idle` |
| Resource class | `cpu`, `io`, `transcode`, `network`, `gpu`. Each class has a concurrency limit. |
| State | `queued`, `running`, `succeeded`, `failed`, `cancelled`, `paused` |
| Lease | Owner and expiry, renewed by heartbeat. An expired lease returns the job to the queue. |
| Checkpoint | Opaque progress data the handler writes so it can resume |
| Dedupe key | Prevents a second identical job (for example two scans of one library) |
| Attempts | Retries with exponential backoff, then `failed` with the error kept |

Claiming uses `FOR UPDATE SKIP LOCKED` on PostgreSQL and a single atomic
update on SQLite. Workers wake on an in-process signal when a job is
enqueued and poll as a fallback.

### 5.2 Handler contract

Handlers are idempotent and resumable: running one twice, or resuming from
the last checkpoint, gives the same result. A library scan checkpoints the
directory cursor. A thumbnail batch is one parent job with small child
jobs, so progress is real and a failure affects one file.

### 5.3 Job types by phase

The queue itself is built in Phase 2, because that phase already needs
persistent background work (D-017). Phase 3 adds the media jobs and the
tuning for small devices.

| Phase | Jobs |
|---|---|
| 2 | Upload cleanup, trash and version retention, blob collection, scrub |
| 3 | Library scan, metadata extraction, thumbnails, video previews, search indexing |
| 5–6 | Subtitle extraction and conversion, speech-to-text, translation |
| 7 | File name matching, metadata and artwork fetch |
| 8–11 | Metadata backup, library move between storage locations |

A live transcode for a viewer is not a job: it is owned by the transcode
manager (section 7.4). It does count against the `transcode` limit and
pauses background work in the `cpu` class while it runs.

### 5.4 Defaults by hardware profile

| Profile | Detected as | `cpu` | `io` | `transcode` |
|---|---|---|---|---|
| Small | under 4 GB RAM | 1 | 2 | 1 |
| Medium | 4–16 GB | half the cores | 4 | 2 |
| Large | over 16 GB and 8 or more cores | cores minus 2 | 8 | 4 |

All limits are editable in the admin UI. Background and idle jobs pause
while a stream is transcoding in software, and run at reduced process
priority.

### 5.5 Visibility

`GET /api/v1/jobs` lists jobs with progress. A server-sent event stream
pushes progress to signed-in clients. Users see their own jobs; admins see
all.

---

## 6. Security model

### 6.1 Passwords and second factor

- Argon2id. Default 64 MiB memory, 3 iterations; reduced on the small
  hardware profile to 19 MiB and 2 iterations. Parameters are stored in
  the hash and upgraded on next sign-in. At most two verifications run at
  once so sign-in attempts cannot exhaust memory.
- Optional TOTP (RFC 6238) with hashed single-use recovery codes. The TOTP
  secret is encrypted with the server key.
- Rate limiting on sign-in, TOTP, pairing code entry, link codes and share
  passwords: per IP and per account, with growing delays. Responses do not
  reveal whether an account exists.

### 6.2 Sessions and tokens

- A sign-in creates a `Session` for the device and returns an access token
  and a refresh token.
- Access token: signed, 15-minute lifetime, carries user and session IDs.
  Every request also checks the session against an in-memory set of
  revoked sessions, so revocation takes effect immediately, not after
  expiry.
- Refresh token: random 256-bit value stored only as a hash. Each use
  rotates it. Reuse of an already-rotated token revokes the whole session,
  with one exception: the immediately previous token is accepted for 60
  seconds, because mobile clients lose responses on bad networks and would
  otherwise be signed out.
- Storage on clients: the web app keeps the access token in memory and the
  refresh token in an `HttpOnly`, `Secure`, `SameSite=Strict` cookie scoped
  to the auth endpoints. Native apps use the Keychain or Keystore.
- Users see and revoke their sessions. Admins can revoke any session.

### 6.3 Authorization

Checked on the server for every request, in one place per resource type:

1. Role: `admin`, `user`, `guest`.
2. Library access: owner of a personal library, or a `LibraryGrant`.
3. Operation: read or write, and whether the library is read-only.

Admins do not read other users' personal content (D-004). Admin endpoints
expose counts and sizes, never names or content, of personal libraries.
Two limits of this promise are stated in the UI and documentation:

- anyone with direct access to the disk or bucket can read files;
- an admin can reset a user's password and then sign in as them. Resets
  are written to the activity log and shown to the user at next sign-in.

### 6.4 Device pairing

For devices without a keyboard (TV apps). Modeled on the OAuth device
authorization grant (RFC 8628).

```
 TV                         Server                     Phone or web (signed in)
  |-- start pairing --------->|
  |<- device code, user code -|
  |   shows code and QR       |
  |                           |<--- scan QR or type code ----|
  |                           |---- device name, kind, IP -->|
  |                           |<--- approve -----------------|
  |-- poll with device code ->|
  |<- access + refresh token -|
```

- `device code`: long random secret, known only to the device, stored
  hashed. `user code`: 8 characters from an alphabet without look-alikes,
  shown as `XXXX-XXXX`.
- The QR encodes `https://<server>/pair?code=<user code>`. The signed-in
  user sees the device name, kind and network address and must confirm.
  The screen says to approve only a device in front of them showing this
  code.
- Requests expire after 10 minutes, are single-use, and code lookups are
  rate limited.
- Approval creates a new `Session` owned by the approving user. The
  approver can restrict it at that moment (for example read-only, no
  access to the personal library), which suits a shared living-room TV.
- The device polls every 5 seconds.

How a TV finds the server, in order of preference:

1. Phone handoff: the TV shows a QR; the signed-in phone app on the same
   network scans it and sends the server address and the approval in one
   step.
2. Discovery on the local network, best effort (it does not work in every
   Docker network mode).
3. Typing the server address with the remote.

Details of 1 and 2 are settled in Phase 10; the server side of pairing is
built in Phase 1.

### 6.5 Linking a phone by QR

A signed-in web session can show a "connect a device" QR containing the
server address and a single-use link code valid for 2 minutes. The mobile
app scans it, exchanges the code for a session, and never needs the
password typed on the phone. The link code is stored hashed.

### 6.6 Signed media URLs

Media elements, native players and cast devices cannot always send an
authorization header. Media endpoints therefore accept a signature in the
URL: an HMAC over the path, relevant query parameters, expiry and session
ID. Thumbnails get about an hour; stream URLs are issued per playback
session and renewed while it is alive. Revoking the session invalidates
its URLs. URLs with signatures are redacted in logs.

### 6.7 Secrets and keys

- A server key set is created at first start in `/config/keys` with
  owner-only permissions. It signs tokens and URLs and encrypts secrets
  stored in the database (S3 credentials, TOTP secrets, provider API keys).
- Secrets are never returned by the API after being saved and never
  logged. Log output passes through a redaction filter for tokens,
  signatures, passwords and keys.
- A metadata backup contains encrypted secrets but not the key. Restoring
  on a new machine needs the key file or re-entry of credentials; the
  admin UI explains this when a backup is taken.

### 6.8 First-run takeover protection

Until an admin exists, anyone who can reach the server could claim it.
Creating the first admin therefore needs a setup token that the server
prints to its log at startup; reading the log is the proof of control
over the server. Only a request made on the server itself, over loopback
and without forwarding headers, skips the token. Private network
addresses are not exempt, because behind Docker or a proxy a request from
the internet can look private (D-012). The token changes at every start
and is useless once an admin exists. Two simultaneous setup requests
create exactly one admin.

After the first admin exists, the wizard signs in as that admin and uses
the ordinary admin endpoints. Setup is complete when an admin exists, at
least one storage location is registered, and the wizard has finished.

A storage location is registered in three steps that protect files:

1. It is tested: written to, read back, cleaned up. A local directory
   that does not exist is reported and created only on explicit request.
2. If it already carries an identity file, it was used by an Atrium
   server and is not taken over. Two servers on one location could delete
   each other's files; adopting a location is a recovery action
   (Phase 11).
3. An identity file with the location's ID is written to it
   (`atrium/location.json`), and its settings are stored encrypted.

Later checks read the identity first, before writing anything. A location
whose identity is missing or different is marked offline. This is how an
unmounted network share, which looks like an empty writable directory, is
told apart from the real location. User files may not be stored inside
the server's config directory.

### 6.9 Transport and web hardening

- The server speaks HTTP; TLS is provided by the optional Caddy profile or
  the user's proxy or tunnel (D-006). Forwarded headers are trusted only
  from proxies listed in settings.
- Strict content security policy, no third-party origins, `nosniff`, and
  user files are always served with `Content-Disposition` and a sandboxed
  content type policy so an uploaded HTML file cannot run script in the
  app's origin.
- Share links (Phase 8) are served from a separate path with their own
  rate limits and cannot reach authenticated endpoints.

---

## 7. Media pipeline

### 7.1 Ingest

For each new or changed file, jobs run in this order, each optional
depending on type:

1. Probe: ffprobe for audio and video, EXIF reader for images. Writes
   `MediaInfo`.
2. Thumbnails: a small grid size and a larger preview size.
3. Video previews: a short low-bitrate clip and seek-bar thumbnails.
4. Indexing: name, date, type, location for search.

Location search uses stored coordinates. Turning coordinates into place
names needs a data set; an offline one is proposed in Phase 3 so the
feature works without any external service.

### 7.2 Client capability profile

When starting playback a client sends what it can play:

- containers,
- video codecs with profile, level, bit depth and maximum resolution,
- HDR formats (HDR10, HLG, Dolby Vision profiles),
- audio codecs and channel counts,
- subtitle formats it can render itself,
- maximum bitrate (user setting or measured).

The profile is built in the shared package from platform queries, so all
clients describe themselves the same way.

### 7.3 Playback decision

The server compares the profile with the file's streams and picks the
cheapest option that works. It returns the decision and the reasons, which
the player can show ("Transcoding: audio format not supported").

| Decision | What happens | Cost |
|---|---|---|
| Direct play | The original file, served with range requests | None |
| Remux | Video and audio copied into HLS with fMP4 segments | Very low |
| Audio transcode | Video copied, audio converted | Low |
| Full transcode | Video re-encoded, adaptive HLS | High |

The server never transcodes when direct play is possible (rule 8). Remux
matters mostly on Apple devices, whose player does not open MKV.

### 7.4 On-demand transcoding

- Output is HLS with fMP4 segments of fixed duration. Because segment
  boundaries are known from the duration, the full playlist is generated
  immediately and any position can be requested at once.
- A seek outside what has been produced restarts FFmpeg at the requested
  segment. This is what makes seeking fast.
- FFmpeg is paused when it is far ahead of the playback position and
  stopped when the session goes idle.
- Segments are written to the cache and reused by later sessions with the
  same parameters.
- Limits, all configurable: concurrent transcodes, maximum resolution and
  bitrate, per-user stream count. When a limit is reached the client gets
  a clear error, not a stalled player.
- Sources on S3 are read by FFmpeg through a loopback URL on the server,
  signed for that process. FFmpeg never receives storage credentials.

### 7.5 Hardware acceleration

NVENC, Quick Sync, VAAPI and VideoToolbox. Detection runs a real
one-second test encode per candidate at setup and after upgrades, because
an encoder being listed does not mean it works on this machine. The wizard
shows the result. A hardware failure during playback falls back to
software for that session and is reported in the admin UI.

HDR to SDR tone mapping is used only when the client cannot display HDR.
On the small profile without hardware support it is refused with an
explanation instead of producing unwatchable playback.

### 7.6 Subtitles and audio (Phase 6)

- Text subtitles (SRT, ASS, VTT) are converted to WebVTT. ASS styling is
  lost in conversion; clients that can render ASS get the original, and
  burn-in is offered when styling must be kept.
- Image subtitles (PGS) are delivered as-is to clients that render them
  and burned in otherwise. Burn-in forces a video transcode.
- Speech-to-text runs a local Whisper model as a job. The model is
  downloaded only when the admin asks for it.
- Translation goes through a pluggable engine configured by the admin.
- Generated and downloaded subtitles are stored as metadata, never beside
  the media in an external library.

### 7.7 FFmpeg distribution

FFmpeg and ffprobe run as separate processes. Atrium is AGPL-3.0 (D-001),
which is compatible with a GPL-3.0 FFmpeg build, so the image can include
libx264 and libx265. Builds must never use `--enable-nonfree`: the result
cannot be redistributed. Details and the patent note are in
[LICENSES.md](LICENSES.md).

---

## 8. Upgrades, backup and API versioning

### 8.1 Migrations

On start, if the database schema is older than the server:

1. Take a metadata backup (8.2). If it fails, stop; do not migrate.
2. Apply migrations in a transaction where the database allows it.
3. On failure, stop with the database restored and a clear message.

A newer database than the server (a downgrade) is refused. Each provider
has its own migration set, and CI runs every migration from every released
schema version to the current one on both databases with realistic data.

### 8.2 Metadata backup

One portable format for both databases (D-008): a compressed archive of
every table as line-delimited JSON, read in a single snapshot, plus the
metadata files. It restores into SQLite or PostgreSQL, which also gives a
supported path between the two.

- Taken before every migration, on a schedule (default daily), and on
  demand.
- Kept in `/config/backups` and copied to `backups/` in each managed
  storage location, so a location holding blobs also holds what is needed
  to name them.
- Retention is configurable. Restore is an admin action in Phase 11 and a
  documented command line step before that.

Current state (Phase 1): the portable format is not built yet. Before a
migration, SQLite gets a verified snapshot of the database file in
`/config/backups`. PostgreSQL has no automatic backup yet, so the server
refuses to migrate a PostgreSQL database that already holds data. Only
the initial migration exists, and it runs on an empty database, so no
install is affected; the portable backup must land before a second
migration is released.

### 8.3 API versioning

- Paths start with `/api/v1`. Within a major version changes are additive:
  new endpoints, new optional fields, new enum values.
- Clients ignore unknown fields and tolerate unknown enum values. This is
  enforced in the generated client and its tests.
- `GET /api/v1/system/info` reports the server version and the minimum
  client version. A client older than the minimum shows an upgrade prompt
  instead of failing unpredictably.
- Removing or changing behavior requires one release of deprecation at
  minimum, so a client one release behind keeps working (rule 9). CI runs
  the previous release's client test suite against the current server.

---

## 9. Internationalization

- Every client ships English and Serbian from the first screen. Strings
  live in the shared package, in ICU message format, because Serbian has
  three plural forms.
- The server returns error codes and parameters, never display text.
  Clients translate.
- Dates, numbers and sizes use the platform's locale formatting. Sorting
  uses locale-aware collation.
- Serbian ships in Latin script only (D-022). Its language tag is
  `sr-Latn`, not `sr`: platforms format dates and numbers for plain `sr`
  in Cyrillic, which would put Cyrillic month names into a Latin
  interface. A device set to Serbian in either script gets the Serbian
  translation.
- A test fails when a key is missing in either language, when the two
  use different arguments, or when a plural lacks a form the language
  needs.

---

## 10. External services and privacy

- Metadata providers, subtitle providers and translation engines are
  plug-ins behind interfaces, all disabled until an admin configures them.
- With none configured the product is complete: files, photos, timeline,
  streaming, embedded and sidecar subtitles, and name-based library
  organization all work offline.
- The server makes no outbound connection that the admin has not enabled.
  No telemetry, no update check, without opt-in.
- Each outbound integration states in the UI what it sends (for example a
  movie title and year to a metadata provider).

---

## 11. Testing

| Level | Scope |
|---|---|
| Unit | Domain rules, path validation, playback decisions, token rotation, name parsing |
| Integration | API against real SQLite and PostgreSQL; storage providers against a temp directory and an S3-compatible server in a container; migrations from each released schema |
| Fault injection | Kill the server mid-upload, mid-scan and mid-job, then verify resume and that no file is lost or half-written |
| Contract | The OpenAPI document is generated and compared; the previous release's client runs against the current server |
| End to end | Web app flows in a real browser |
| Media | A small set of sample files covering common codecs, containers, HDR and subtitle formats, each with a license that allows redistribution |

A failing test is fixed or the change is withdrawn. Tests are not
weakened, skipped or deleted to make progress.

---

## 12. Risks that can lose or expose data

Flagged here so they are decided on purpose.

1. Content-addressed storage depends on the database. Without metadata, a
   managed location is a pile of unnamed blobs. Mitigations: backups
   copied into every storage location (8.2), the blob grace period (4.3),
   an export tool that writes a normal folder tree. The setup wizard says
   this in plain words.
2. The server key is needed to decrypt stored credentials. Losing `/config`
   without a copy of the key means re-entering them. No user file is
   affected.
3. Enabling writes on an external library lets Atrium delete from the
   user's own folders. It is off by default and the toggle carries a
   warning.
4. A misconfigured reverse proxy can make every request look local and
   weaken rate limiting and first-run protection. The wizard tests and
   reports what address the server sees.
5. Bucket lifecycle rules or versioning set by the user on S3 can delete
   or multiply blobs outside Atrium's knowledge. The storage test warns
   about lifecycle rules where the API exposes them.
6. Plain HTTP on a local network exposes tokens to anyone on that
   network. The apps show a persistent indicator for insecure connections.
7. The admin privacy boundary has the two limits listed in 6.3.
