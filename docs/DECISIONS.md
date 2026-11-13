# Atrium decisions

Each non-obvious choice, the alternatives and the reasons. Newest at the
bottom. "Owner" decisions were made by the product owner on 2026-11-10;
"Proposed" decisions are the engineer's. At the Phase 0 review the owner
delegated the open points to the engineer ("fix everything you think
needs fixing"), so the Proposed decisions stand and D-017 to D-019 were
added. Any of them can still be overruled.

## D-001 License: AGPL-3.0 (Owner)

- Alternatives: undecided with strictest constraints; MIT or Apache-2.0;
  proprietary.
- Reasons: standard for self-hosted products; keeps hosted forks open.
- Consequences: GPL dependencies are allowed on the server, and the
  shipped FFmpeg may be a GPL build. App Store distribution of AGPL code
  works only while the project owner holds the copyright to all client
  code and grants Apple's terms separately, which requires a contributor
  license agreement before accepting outside contributions to the clients.
  See LICENSES.md.

## D-002 Managed files are content-addressed blobs (Owner)

- Alternative: mirror the user's folder tree on storage.
- Reasons: deduplication, atomic and instant rename and move, identical
  behavior on disk and S3, no drift between disk and database.
- Consequences: storage is not browsable without Atrium. Mitigated by
  metadata backups copied into each storage location, a grace period
  before blobs are deleted, and an export tool. Existing folders stay
  as they are through external libraries.

## D-003 Minimum hardware: ARM64 and x64, 2 GB RAM (Owner)

- Alternatives: 4 GB floor; x64 only.
- Consequences: multi-architecture images, streamed I/O everywhere,
  hardware profiles for job limits, software transcoding limited on the
  small profile.

## D-004 Admins cannot read users' personal content (Owner)

- Alternatives: admin sees everything; configurable at setup.
- Consequences: admin endpoints expose sizes and counts only. Not
  end-to-end encryption: disk access and password reset remain ways in,
  and both are stated to users. Password resets are logged and shown to
  the affected user.

## D-005 Encryption at rest: reserved, not built for 1.0 (Owner)

- Alternatives: optional per-location encryption in the first release;
  never.
- Consequences: storage locations carry a format version and blobs are
  addressed by plaintext hash, so encryption can be added per location
  later without moving existing data. Until then the documentation
  recommends disk or bucket encryption.

## D-006 HTTPS through an optional Caddy profile (Owner)

- Alternatives: plain HTTP and documentation only; certificates handled
  inside the server.
- Reasons: one opt-in service gives automatic certificates without
  security-sensitive code in the server.
- Consequences: the server speaks HTTP and trusts forwarding headers only
  from configured proxies. Native Windows and Linux installs (Phase 11)
  need their own answer.

## D-007 Platform players on mobile and TV (Owner)

- Alternatives: libVLC or mpv; platform first with a second engine later.
- Reasons: best HDR, hardware decoding, battery life, AirPlay and
  picture-in-picture; no copyleft code inside store builds.
- Consequences: Apple clients need remux to HLS for MKV, and ASS or PGS
  subtitles are converted or burned in where the player cannot render
  them. The player core stays engine-independent so a second engine
  remains possible.

## D-008 One portable metadata backup format (Proposed)

- Decision: backups are an archive of line-delimited JSON per table plus
  metadata files, readable into SQLite or PostgreSQL.
- Alternatives: native tools (`VACUUM INTO`, `pg_dump`).
- Reasons: `pg_dump` must match the server version and would have to ship
  in the image; one format means one restore path to test and a free
  migration path between databases.
- Cost: slower than native snapshots on very large databases; must be
  taken in one read snapshot.

## D-009 One process, one container (Proposed)

- Decision: API, web app, workers and transcode manager in one process.
  One instance per database.
- Alternatives: separate worker and transcoder containers; a message
  broker.
- Reasons: install in minutes, modest hardware, fewer failure modes. The
  module boundaries allow splitting later if needed.

## D-010 Cross-user deduplication is invisible to clients (Proposed)

- Decision: "do I already have this hash" answers only for the asking
  user's files. Content already stored by someone else is uploaded in
  full and collapsed on the server.
- Alternative: skip the upload whenever any user has the content.
- Reasons: otherwise any user can test whether a specific file exists on
  the server. Security outranks performance.

## D-011 Names are case-preserving, unique ignoring case (Proposed)

- Decision: siblings cannot differ only by letter case or Unicode
  normalization form.
- Alternative: fully case-sensitive names.
- Reasons: matches Windows and macOS expectations, avoids confusable
  names, and keeps exports to case-insensitive filesystems lossless.

## D-012 Setup token for creating the first admin (Proposed, revised in Phase 1)

- Decision: creating the first admin needs a token that the server prints
  to its log at startup. The only exception is a request made on the
  server itself (loopback address, no forwarding headers).
- Original decision: private network addresses were also exempt, so home
  installs had no extra step.
- Why it changed: the server cannot tell a private address from a public
  one reliably. Behind Docker's port mapping, rootless containers or a
  reverse proxy, a request from the internet can arrive with a private
  source address. The exemption would then be open exactly on the
  installs it was meant to protect, such as a VPS.
- Alternatives: never require a token; exempt private addresses.
- Cost: every normal install has one extra step, copying the token from
  `docker compose logs`. Security outranks ease of installation in the
  priority order.

## D-013 First public release after Phase 7, with a release gate (Owner, gate Proposed; superseded by D-030)

- Owner decision: 1.0 after Phase 7 so the media library promise is met
  at launch.
- Addition, adopted under the owner's delegation: a release gate pulling
  minimal backup, security review, health and documentation work ahead of
  Phases 11 and 12. See ROADMAP.md.
- Reasons: publishing a storage product without tested restore or a
  security review would put safety and security behind schedule, against
  the priority order.

## D-014 SHA-256 for content hashes (Proposed)

- Alternative: BLAKE3.
- Reasons: in the .NET base library, hardware accelerated on both target
  architectures, supported as a part checksum by S3. BLAKE3 would add a
  native dependency.

## D-015 Access tokens are short-lived signed tokens plus an in-memory revocation set (Proposed)

- Alternative: opaque tokens looked up in the database on every request.
- Reasons: no database read per request on small hardware, while
  revocation is still immediate because there is exactly one server
  process.

## D-016 Server-sent events for live updates (Proposed)

- Alternative: WebSockets.
- Reasons: one-directional updates are all that is needed; plain HTTP
  passes through reverse proxies and tunnels with less configuration.

## D-017 Job queue core moves from Phase 3 to Phase 2 (Proposed)

- Alternative: timers in Phase 2, replaced by the queue in Phase 3.
- Reasons: trash retention, upload cleanup and blob collection delete
  data and must survive restarts from the first day they exist. Throwaway
  timers would be the least tested code touching deletion.
- Cost: Phase 2 grows; Phase 3 shrinks by the same amount.

## D-018 Serbian in Latin and Cyrillic, Cyrillic generated (Proposed, superseded by D-022)

- Decision: translators maintain Latin. Cyrillic is produced by
  transliteration at build time with an exception list. Clients follow
  the device's script setting, falling back to Latin.
- Alternatives: Latin only; two hand-maintained translations.
- Reasons: both scripts are in everyday use; Latin to Cyrillic is nearly
  mechanical, so one source avoids two translations drifting apart.
- Cost: the exception list needs review by a native speaker before 1.0.

## D-019 Replaced files keep earlier versions (Proposed)

- Decision: replacing content creates a `NodeVersion`; versions are kept
  by age and count, count toward quota, and can be restored.
- Alternative: send the replaced file to the trash.
- Reasons: with content-addressed blobs a version is one extra row. The
  trash alternative leaves two unrelated items with the same name and
  loses the link between them, and adding versions after release would
  need a data migration. Sync clients that replace files often (Phase 4)
  make accidental overwrites likely.
- Cost: version retention job, four endpoints and a details-panel section
  in Phase 2.

## D-020 Timestamps are stored as UTC milliseconds, enums by name (Proposed)

- Decision: every timestamp column is a 64-bit integer of milliseconds
  since the Unix epoch on both databases. Enum columns hold the member
  name as text.
- Alternatives: native types (`timestamptz` on PostgreSQL, ISO text on
  SQLite); enums as integers.
- Reasons: SQLite cannot compare or sort the text form EF Core uses for
  `DateTimeOffset`, so queries by time would fail or run in memory. One
  representation keeps the schema, the queries and the portable backup
  (D-008) identical on both databases. Names survive reordering of enum
  members; integers do not.
- Cost: timestamps are not human-readable in a database console, and
  precision is one millisecond.

## D-021 Container tests run only in CI (Owner)

- Decision: tests that need PostgreSQL or an S3 server in a container are
  tagged `Category=Containers` and run in GitHub Actions. Locally,
  `dotnet test --filter "Category!=Containers"` runs everything else.
- Alternatives: Podman or Docker Desktop on the development machine.
- Reasons: Docker Desktop needs a paid license on a company laptop and
  Podman needs WSL2, which company policy may block.
- Cost: a PostgreSQL-only failure shows up after a push, not before. A
  snapshot test that needs no database still catches a missing migration
  for either provider locally.

## D-022 Serbian in Latin script only (Owner, 2026-11-11)

- Decision: the product ships English and Serbian in Latin script.
  Cyrillic is removed from the whole project: no translation file, no
  converter, no language option.
- Supersedes D-018, which shipped both scripts with Cyrillic generated
  from Latin.
- Reasons: the owner does not need a Cyrillic interface. Dropping it
  removes the converter, its hand-written exception lists and the
  native-speaker review they needed before 1.0.
- Kept on purpose: the language tag stays `sr-Latn`, because platforms
  format plain `sr` in Cyrillic.

## D-023 Own job queue; jobs are claimed by a conditional update (Proposed, Phase 2)

- Decision: the queue is Atrium's own code over the `Jobs` table. A
  worker claims a job with `UPDATE ... WHERE id = ? AND state = 'Queued'`
  and owns it only if one row changed. The same code runs on SQLite and
  PostgreSQL.
- Alternatives: Hangfire or Quartz.NET; `FOR UPDATE SKIP LOCKED` on
  PostgreSQL, as DESIGN.md first said.
- Reasons: both libraries bring their own schema, which the portable
  backup (D-008) and the per-provider migrations would have to carry, and
  neither has the lease, checkpoint and resource-class model in DESIGN.md
  5. With one server process (D-009) there are only a few workers, so
  `SKIP LOCKED` would buy nothing over a conditional update, and one code
  path means one behavior to test on both databases.
- Details: priority is the one enum stored as a number, because the queue
  is ordered by it. A run interrupted by shutdown goes back to the queue
  without using an attempt; a crash does use one, so a job that brings the
  server down stops being retried. Uniqueness of the dedupe key is
  enforced by the database through a column that is set only while the
  job is active.

## D-024 Own tus implementation on server and client (Owner, 2026-11-12)

- Decision: the tus protocol is implemented in the server and in
  `packages/shared`, with no new dependency.
- Alternatives: tusdotnet and tus-js-client (both MIT).
- Reasons: the core protocol is small. tusdotnet's storage model does not
  fit staging into content-addressed blobs or S3 parts of at least 5 MB,
  and one client in the shared package serves web and React Native alike.

## D-025 Deleting a user is scheduled and can be cancelled (Owner, 2026-11-12)

- Decision: an admin deletes a user by typing the user's name. The
  account is disabled and all its sessions are revoked at once. A job
  removes the user's personal files 30 days later; until then the admin
  can cancel the deletion. The admin sees how many files and how much
  space are affected, never their names (D-004).
- Alternatives: immediate deletion, recoverable only from a metadata
  backup within the blob grace period; handing the files to another user.
- Reasons: a mistaken deletion must be reversible without a restore.
  Handing files to another user would let an admin read them, against
  D-004.

## D-026 Portable metadata backup in Phase 2; first tag at Alpha (Proposed, Phase 2; tag timing revised by D-030)

- Decision: the portable backup (D-008), its schedule, the copy into each
  storage location and the command-line restore are built in Phase 2.
  The initial migration keeps being regenerated until the first tagged
  build, which is Alpha at the end of Phase 3; from then on migrations
  are only added.
- Alternatives: build the backup in Phase 3 or with the release gate.
- Reasons: from Phase 2 on, file names and folders exist only in the
  database (D-002). Losing it without a backup loses every file in a
  usable form, so the backup has to exist as soon as files do.

## D-027 The change feed moves to Phase 4 (Proposed, Phase 2)

- Decision: `GET /changes` and its change log are built with the mobile
  app in Phase 4, not in Phase 2.
- Alternatives: build the log now so that history exists from the start.
- Reasons: its only consumers are sync and offline copies on mobile, and
  their needs (what a change entry carries, how long the log is kept,
  what a client does when its cursor is too old) are best settled with
  them. A client that starts syncing lists the tree once and follows the
  feed from then on, so no history is lost by starting the log later.
- Also decided in this PR: a folder listing pages by offset. Keyset
  paging over several sort orders is more code than a folder of a few
  thousand items needs; it can change without breaking clients, because
  the cursor is opaque.

## D-029 How the risks found in Phase 2 are handled (Proposed, delegated by the owner, 2026-11-12)

The owner asked to settle the Phase 2 risks as the engineer sees best.

1. **Backups next to the files.** Backups leave out signed-in devices
   and their tokens (`Sessions`, `RefreshTokens`, `PairingRequests`,
   `LinkCodes`): they are worthless for recovery, since everyone signs in
   again, and should not sit in a storage location. Accounts with their
   Argon2id password hashes and the encrypted secrets stay, because
   without them nobody could sign in after a restore. Alternative
   considered: encrypting the copies with the server key; rejected,
   because the copies exist for the case where `/config` and its key are
   lost.
2. **Resuming the wrong file after a reload.** The web app records a
   fingerprint of the file's content (its size and a hash of the first
   and last mebibyte) once the upload exists, and continues only when the
   file chosen again has the same fingerprint, folder, name, size and
   modification time. Otherwise the upload starts over; the old one
   expires on the server after a day.
3. **No chunk checksum on plain HTTP.** The shared package carries its
   own SHA-256, used wherever the platform has none: browsers on plain
   HTTP, and React Native in Phase 4. Every chunk is now checked on
   every platform.
4. **S3 content is not read back after an upload.** Accepted as is: the
   service verifies each part's Content-MD5 against the bytes the server
   sent, and the server hashed those same bytes in transit, so a read
   back would only repeat the check at the cost of downloading the file.
5. **Listing and tree walks on very large libraries.** Kept as built and
   measured with 100,000 files under release-gate item 6; keyset paging
   or recursive queries are added only if the measurement asks for them.

## D-028 One tus chunk is one storage part; no local spool (Proposed, Phase 2)

- Decision: every accepted tus chunk is stored as one part of a multipart
  upload on the storage location. The server tells the client its chunk
  size (`Atrium-Chunk-Size`, at least 16 MB, more for very large files so
  10,000 parts suffice), and every chunk but the last must be at least the
  provider's minimum part size (5 MB on S3, 1 byte on disk). A chunk needs
  a `Content-Length`; one that breaks off halfway is discarded and sent
  again. The SHA-256 is computed while chunks pass through; if a restart
  loses it, the assembled content is read back once.
- Alternatives: buffer chunks of any size in a local spool file and flush
  5 MB parts to S3, as DESIGN.md 4.1 first suggested; tusdotnet's stores.
- Reasons: the offset the server reports is then always backed by parts
  the storage holds durably, with no second copy on local disk to keep
  consistent with the database across crashes, and nothing to lose when
  the cache is deleted. Atrium's own clients choose their chunk size, so
  the constraint costs nothing.
- Cost: a generic tus client that sends the whole file in one request
  works, but loses everything sent so far when that request breaks off.
  An S3 upload holds one chunk in memory while it is sent (16 MB by
  default).

## D-030 No Alpha, Beta or tagged builds; one release after Phase 12 (Owner, 2026-11-12)

- Decision: there are no Alpha or Beta milestones and no version tags
  while Atrium is developed. The first release, and with it the first
  tag, comes when every phase up to and including Phase 12 is done.
- Supersedes the release plan of D-013 (1.0 after Phase 7, then 1.1 to
  2.0) and the "first tag at Alpha" part of D-026.
- Consequences:
  - D-026 applies until the release: whenever the schema changes, the
    initial migration of each database is generated again instead of a
    new migration being added. A database made by an earlier development
    build cannot be upgraded; it has to be created again. The owner is
    warned before a change that needs a new demo database.
  - The release gate of D-013 becomes the list of release requirements
    in ROADMAP.md. Most of it now falls into Phases 11 and 12 naturally;
    items already built (backup and restore, integrity report) stay.
  - The release workflow still publishes an image only for a version
    tag, so nothing is published before the release.
- Reasons: the owner is the only user until the release, so a
  checkpoint release would only add upgrade paths to maintain.

## D-031 Photo and video location is left out (Owner, 2026-11-12)

- Decision: Phase 3 reads no GPS coordinates from photos or videos. There
  are no place names, no search by location and no map.
- Alternatives: coordinates plus offline place names from GeoNames
  (CC BY 4.0), as first planned; coordinates only.
- Reasons: the owner does not need to know where a photo was taken. Not
  storing coordinates also means they cannot leak through Atrium.
- Consequences: the original files are untouched, so their EXIF still
  holds whatever location the camera wrote, and a download gives the
  file as it is. Adding location later is an additive column and one
  re-extraction job. Removing location data from files given out through
  share links is a question for Phase 8.

## D-032 Media libraries for Phase 3 (Owner, 2026-11-12)

- Image decoding and thumbnails: libvips through NetVips. Alternatives:
  SkiaSharp, FFmpeg only, ImageSharp. libvips decodes at reduced size
  while reading (shrink-on-load) and streams, so a thumbnail of a large
  photo needs little memory on a 2 GB device. In the image it is the
  distribution's libvips, built with libheif, so iPhone HEIC photos
  decode; the NetVips.Native binaries, which lack HEIC, are used only to
  run the tests on Windows. ImageSharp's license restricts commercial use.
- EXIF and container metadata: MetadataExtractor. Alternatives: ExifTool
  as a process (needs Perl in the image), an own parser.
- FFmpeg and ffprobe: the distribution's package in the image (the .NET
  10 base image is Ubuntu 24.04). Alternatives: jellyfin-ffmpeg, static
  builds. Phase 3 only probes, grabs frames and makes short previews;
  the choice is revisited for hardware transcoding in Phase 5.
- Licenses and flags are in LICENSES.md.

## D-033 Media data model (Proposed, Phase 3)

- **External library is a folder path, not a storage location.** A
  storage location gets an identity file written into it (DESIGN.md
  6.8), and Atrium must not write anything into a read-only external
  folder. So an external library carries its own absolute path and no
  storage location; an unmounted share is caught by the missing-folder
  guard instead (4.4). S3 prefixes as external libraries can come later.
- **`InTrash` on every node of a trashed subtree.** Only the node a user
  deletes carries `TrashedAt`. The timeline and search list nodes across
  a whole library and must leave out everything inside a trashed folder;
  walking up the tree for each row would not scale. The flag is set on
  the subtree in the same transaction as the trash and cleared on
  restore, except below items that were trashed on their own.
  Alternative: a recursive query per listing.
- **Timeline fields on the node.** `MediaKind`, `CapturedAt` and a
  diacritic-free `SearchKey` live on `Node` and are set by the database
  context whenever a node is saved, so no code path can forget them.
  `MediaInfo` holds the technical details, keyed by blob (shared by
  duplicates) or, in an external library, by node. Alternative: join
  `MediaInfo` for every timeline page, which needs an OR across two keys.
- **No `Derivative` table.** Thumbnails are files in the cache named by
  the content (the blob hash, or a hash of the external file's path, size
  and time) and the variant. A file that exists is ready; deleting the
  cache is always safe and just causes regeneration. Only a failure is
  remembered, in `MediaInfo`, so a broken file is not retried forever.
  Alternative: an index table, as DESIGN.md first said, which can drift
  from the cache directory.

## D-034 Capture time is local clock time (Proposed, Phase 3)

- Decision: the timeline is ordered and grouped by the local clock time
  at which a photo was taken, stored with a zero offset. EXIF gives that
  time directly (`DateTimeOriginal`), with the offset when the camera
  recorded one. Without EXIF (screenshots, many videos), the file's
  modification time is converted to the server's time zone, which is the
  `TZ` setting of the container.
- Alternative: store true instants and convert to the viewer's zone.
  Most EXIF has no offset, so the instant is unknown; and a holiday photo
  should stay on the day it was taken wherever it is viewed.
- Until metadata is read, the modification time in UTC is used, so a new
  photo appears on the timeline at once and moves to its exact place
  when its metadata job has run.
