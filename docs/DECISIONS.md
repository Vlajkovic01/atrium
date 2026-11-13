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

## D-035 How an external folder is read (Proposed, Phase 3)

- **Admins add it, grants decide who sees it.** Adding a library checks
  the folder first: an absolute path that exists and can be listed, not
  overlapping Atrium's config or cache directory, a local storage
  location or another external library. A library inside a storage
  location would show Atrium's own blobs, and once writes are possible
  could delete them; two overlapping libraries would show files twice.
  An admin sees a library's photos only by granting access to
  themselves, like anyone else.
- **Symbolic links are neither listed nor followed**, at any step of a
  path, so nothing outside the folder can be reached through one.
  Alternative: follow links that stay inside the folder, which needs the
  same check at every request and gains little.
- **Hidden entries and system folders are skipped**: names starting with
  a dot, `@eaDir`, `#recycle`, `$RECYCLE.BIN`, `Thumbs.db` and similar,
  and Atrium's own `.atrium-trash`.
- **Names are kept exactly as on disk.** Siblings are kept apart by
  their exact name, not the case-folded one, because a case-sensitive
  disk can hold `a.jpg` and `A.jpg` side by side; neither is hidden.
- **Changes are found by scanning**, every six hours and on demand, not
  by watching the folder: change notifications are unreliable on
  network shares and in containers. A scan keeps the folders still to
  visit as its checkpoint and continues after a restart.
- **Vanished files are kept as missing** for the trash retention, with
  their albums, and come back as the same entry if the file returns.
  Before marking anything missing, a scan refuses to trust a folder that
  is gone, suddenly empty, or has lost more than half of its files (and
  at least 50): the library is marked offline instead and nothing
  changes.
- **Content is read from disk on every request**, with its current size,
  and the ETag is a hash of path, size and time, so a file changed after
  the last scan is never served with a stale length.

## D-036 Changing the files of an external library (Owner request, details Proposed, Phase 3)

- **Owner's requirement:** an admin may allow Atrium to rename, move,
  delete and upload in an external library, only after a clear warning
  and typing the library's name; deleting moves into a trash folder
  inside the library with the normal trash retention; changes can be
  switched off at any time.
- **Who may change files** is a second, per-person grant ("can change")
  on top of "can see". Allowing changes for a library does not by itself
  let everyone who sees it delete from it.
- **Enabling proves the folder is writable** by creating Atrium's own
  `.atrium-trash/.uploads` folder in it; a folder mounted read-only is
  refused with an explanation.
- **Disk first, index second, under one lock per library**, shared with
  the scan. A failed index update undoes the disk change. A crash between
  the two leaves the file where the disk change put it, and the next scan
  finds it there: the index can lose track of an album entry, never of a
  file.
- **Trash:** `.atrium-trash/<item id>/<name>` on the same disk, so a
  delete is a rename. The scan neither indexes the trash nor calls trashed
  items missing. A file is removed from disk only when its retention ends
  or someone empties the trash, disk first and row second, and only while
  changes are still allowed: switching changes off stops this too.
- **Uploads** are assembled in `.atrium-trash/.uploads` with the same
  resumable chunks as everywhere, verified, and moved into place.
  "Replace" moves the existing file to the trash; external libraries
  have no versions.
- **Copying** inside an external folder is refused: it would duplicate
  the user's files on their own disk, and it was not asked for.
- **Names** chosen in Atrium follow the usual rules (D-011) and may not
  match a sibling ignoring case; names found on disk are kept as they
  are when an item only moves.
- **Scan state** (online, last scan) is written without bumping the
  library's version, so a scan finishing never makes an admin's edit
  fail with a version conflict.

## D-037 How photos and videos are processed (Proposed, Phase 3)

- **Jobs.** `media.extract` reads one file's metadata and makes its grid
  thumbnail, in the `cpu` class at background priority. It is queued when
  an upload finishes and when a scan finds a new or changed file, and an
  hourly `media.sweep` queues whatever was missed (a restored backup, a
  deleted cache, a crash) in batches of 2,000. A video then gets
  `media.preview`, a four-second silent clip, at idle priority.
- **Facts belong to the content.** One blob shared by several files is
  read once and every such file is dated by it. An external file is read
  again when its size or time changes. A file that cannot be decoded is
  marked unsupported and is not retried until it changes.
- **Sizes.** The grid thumbnail (400 px) is made for every photo and
  video; the viewer image (1,920 px) only when first asked for, so a
  library of 100,000 photos does not fill the cache with images nobody
  opens. Thumbnails are WebP.
- **Nothing private reaches the cache.** Thumbnails are saved without
  any metadata, so no location or camera details from the original are
  kept (D-031); previews are written with `-map_metadata -1`.
- **The user's files are read, never held.** libvips gets a stream that
  Atrium opens with full sharing and closes at once: given a path, it
  kept the file mapped on Windows after it was done, which stopped other
  programs from writing the user's own file. Its cache is turned off for
  the same reason, and to save memory.
- **FFmpeg and ffprobe run as processes** with an argument list, never a
  shell, under a two-minute limit and, for background work, at reduced
  process priority. On S3, they read a temporary copy in the cache.
- **Without the tools** (a development machine) photos still show and
  are dated by their file time; their thumbnails come from FFmpeg if
  libvips cannot read them, as with HEIC on Windows. The image always
  has both, and CI checks it.
- **Thumbnails are served** through signed URLs that stay the same for
  a six-hour window, so the browser can cache them, and inline
  generation is limited to a few at once.
- Alternatives considered: one job doing every file of a library (no
  per-file progress or retry); making every size eagerly (cache space);
  reading by path (the lock described above).
## D-038 Timeline, search and albums (Proposed, Phase 3)

- **One timeline across libraries**: the personal library and every
  library shared with the person, without the trash and without missing
  files. A library can be chosen to see only its photos.
- **Months are counted in the server process** from one indexed column,
  because the month of a stored millisecond count is spelled differently
  on SQLite and PostgreSQL. 100,000 photos are 800 KB of numbers; the
  release requirement 6 measurement decides whether a stored month column
  is needed.
- **Keyset paging** on capture time and ID for the timeline and albums,
  so a page boundary does not move while photos are added; search pages
  by offset, as the folder listing does (D-027).
- **Search** matches every word as a substring of the name, folded to
  lower case without diacritics (`suma` finds `Šuma`, `dj` finds `đ`), with
  `%` and `_` taken literally. Dates are the capture time for photos and
  videos and the modification time for other files. No full-text index
  until a measurement asks for one.
- **Albums hold references.** Adding needs read access to the photo; an
  album shows only what its owner can still see, so a trashed photo
  disappears from it and comes back on restore. Deleting an album never
  touches a photo. Sharing albums is Phase 8.

## D-039 libvips runs in decoder processes, never in the server (Proposed, Phase 3)

- Finding: with Ubuntu's libvips loaded into the server, the server in
  the image died with a segmentation fault seconds after starting, in
  ordinary .NET code, the mark of memory damaged by native code. Not
  loading libvips in the server made the crash go away; the cause inside
  libvips or its many libraries was not pinned down further.
- Decision: thumbnails are made by decoder processes: the same program
  started with `--image-worker`, a few at a time, each answering one
  request per line on standard input and output. One that crashes,
  hangs past two minutes or answers nonsense is killed and replaced; the
  thumbnail request gets 503 and the job is retried later. A process
  ends by itself when the server goes away, because its input closes.
- Reasons: beyond this crash, decoding pictures people upload is the
  most exposed native code in Atrium. A broken or hostile file must not
  be able to take the server, its database connection or its memory
  with it. FFmpeg already runs as separate processes for the same
  reason.
- Cost: each decoder process is a .NET process of a few tens of
  megabytes; their number follows the `cpu` limit plus one. Tests and the
  command-line tools run libvips in their own process.
- `Atrium.Api --check-media` makes a thumbnail both ways and checks
  FFmpeg and ffprobe; CI runs it in the image before starting it.

## D-040 An external library is a folder of any files (Owner correction, 2026-11-13)

- Owner's correction: an existing folder is not a photo library. Every
  file and subfolder in it must be visible and, once allowed, changeable;
  the owner's mention of one folder was an example, not the scope.
- Decision: an external library shows everything in Files, with its own
  entry in the menu under Files and an Open button on the Libraries page.
  Whether its photos and videos also appear on the timeline is an option
  per library, on by default; kind `photos` means on, `folder` off, so no
  schema change was needed. Search and albums reach every library.
- Allowing changes now also chooses who makes them, in the same dialog,
  with the admin doing it suggested: before, changes could be on for a
  library nobody was allowed to change, and Files did not say why it was
  read-only. Files now says why (changes off, or not for you) and where to
  change that; the Libraries page warns when nobody may change a library
  that allows changes. Copy is not offered where it cannot work.
- Texts and examples in the app and the documentation are neutral.
## D-041 Each person's own menu, with views made of rules (Owner, 2026-11-13)

- Owner's decisions: the menu is customizable per person; entries can be
  views chosen by rules; a view only shows files and never moves them;
  Photos stays as a built-in entry that can be hidden; built now, in
  Phase 3. The admin sets the default for people who have not arranged
  their own.
- What a person can do: reorder, hide and rename entries (Files, Photos,
  Trash, Devices, Account, every folder shared with them, their views),
  and gather them under their own group headings. Administration is not
  part of the layout: an admin always sees it, so nobody can lock
  themselves out. "Customize menu" is always at the end of the menu.
- Extended the same day at the owner's request ("more customization, and
  Customize menu does not need to sit in the menu"): any folder the
  person can open and any album of theirs can be pinned from its page
  (`folder:<id>`, `album:<id>`); every entry can take an icon from a
  fixed set; separator lines (`separator:<id>`); groups fold, and the
  folding is remembered; the menu can be compact (icons only, names on
  hover and for screen readers); an admin chooses whether Administration
  sits at the top or the bottom; entries are reordered by dragging as
  well as with the arrows. "Customize menu" left the menu: it is a pencil
  beside Sign out and a card on the Account page. A pin to something the
  person can no longer open (deleted, in the trash, access withdrawn) is
  left out of the menu rather than shown broken. Pins and views stay
  personal: they are not part of an admin's default.
- Views: a name and rules: words in the name, kinds (photo, video,
  document, folder), file endings, libraries, a date range or the last N
  days (capture time for photos and videos, change time for other
  files), a size range, and an order. A view follows new files as they
  arrive, shows only what the person can read, and is listed under their
  menu. At most 50 per person.
- Storage: the layout and the views are settings rows per person
  (`prefs.nav:<user>`, `prefs.views:<user>`, `prefs.nav.default`),
  readable and writable only by their owner through the API and removed
  with the account. Alternative: tables of their own, which would change
  the schema and, under D-026 and D-030, require a new demo database for
  nothing a query needs; tables can still come before the release if
  views ever need to be shared or queried across people.
- New entries (a folder just shared, a view just made) appear after the
  entry that precedes them by default, so a saved arrangement never hides
  something new.

## D-042 Administration is one place with an overview (Owner, 2026-11-13)

- Owner's decisions: one menu entry instead of a link per section, and
  the overview, backup and storage check screens planned for Phase 11
  built now, in Phase 3.
- The menu shows "Administration" once, with a badge counting warnings.
  It opens an overview; every section (People, Storage, Libraries,
  Background work, Backups) is a tab on top of it, and each tab carries
  its own warning count. Earlier addresses (`/people`, `/storage`,
  `/libraries`, `/background-work`) redirect to `/admin/...`.
- The overview (`GET /admin/overview`) is computed on request from what
  the server already records; nothing new is stored. It lists what needs
  attention first, each item naming the section that deals with it:
  unreachable storage, offline folders, jobs failed in the last day, no
  backup or one older than two days (warnings); paused work, accounts
  waiting for deletion, no storage check yet or none for two weeks
  (information). Thresholds follow the schedules: backups daily, checks
  weekly.
- Like the rest of administration it shows counts and sizes, never file
  names. The storage check lists storage keys of missing or damaged
  objects, which are not file names.
- Backups: the page explains that a backup holds the database, not the
  files, makes one on demand and follows its job, and says that restoring
  is a command-line step until Phase 11.

## D-043 The person's own menu under their name, with Administration in it (Owner, 2026-11-13)

- Owner's point: "Customize menu" as a small pencil was too hidden, and
  Administration looked like any other entry. Owner's idea, adopted:
  clicking one's name opens a menu. After trying a separate area for
  Administration above the name (it then showed in two places), the
  owner chose to keep it in that menu, beside Customize menu.
- The name and avatar at the foot of the sidebar (top right on a phone)
  open a menu: Account and settings, Customize menu, Administration for
  admins (with the warning count), Sign out. It follows the menu button
  pattern (arrows, Enter, Escape back to the name). The separate pencil
  and sign-out buttons are gone; a red dot on the name shows warnings.
- Only admins see Administration at all; the server refuses its pages to
  everyone else. An admin who prefers it in the
  main menu chooses "In the main menu" under Customize menu: it then
  becomes the entry `admin`, placed by dragging, grouped, renamed and
  given an icon like any other, but never hidden, and it is not repeated
  under the name. The entry exists only for admins.

## D-044 Any file opens where it is (Owner, 2026-11-13)

- Owner's request: "I cannot look at a PDF, an Excel file and so on;
  support viewing all files, not only pictures." Owner's choices: now, in
  Phase 3; Office files (Word, Excel, PowerPoint) later.
- Opening a file in Files, in a search or in a view shows it in a
  full-window viewer, with the previous and next file of the list on the
  arrow keys, Download, and Details (versions and the rest) one click
  away. Folders still open in Files.
- What the browser shows by itself, with no new dependency and nothing
  converted on the server: pictures (JPEG, PNG, GIF, WebP, AVIF, BMP),
  video and audio the browser plays, PDF in the browser's own viewer,
  and text or code up to 2 MB (shown as text, never run). Anything else,
  including Office files for now, says so and offers the download.
- PDF needs one exception to the content headers: the browser's PDF
  viewer refuses to run in a sandboxed document, so an inline PDF gets
  `default-src 'none'; frame-ancestors 'self'` and `X-Frame-Options:
  SAMEORIGIN` instead of the sandbox. Scripts inside a PDF run in the
  viewer's own isolated process, not in Atrium's origin. Every other type
  keeps the sandbox and `DENY`; HTML and SVG are still never shown.
- Office files later: the candidate is LibreOffice on the server turning
  them into PDF in a separate process, cached like thumbnails (MPL-2.0,
  about 300 MB in the Docker image). Alternatives considered: SheetJS and
  docx-preview in the browser (no PowerPoint, and each client would need
  its own).

## D-045 Notifications kept on the server; confirmations go away by themselves (Owner, 2026-11-13)

- Owner's request: messages such as the one about uploads should go
  away by themselves when not closed, and a list of unread notifications
  should be kept. Owner's choice: kept on the server, so every device
  shows the same list.
- Storage: one settings row per person (`notifications:<user>`), the
  newest 200 for 90 days, removed with the account. No table of its own,
  so no schema change and no new demo database; a table can come later
  if notifications need querying across people. Writes for one person
  are serialized in the process (single server process, D-009).
- Who adds them. The server: a job a person started that failed for good
  (to that person); a backup, storage check, folder scan or account
  deletion that failed for good (to every active admin); a folder that
  went offline (admins, once, not at every scan); damage found by the
  storage check (admins). Work that fails file by file, such as reading
  a photo's details, is left to Background work so one broken folder
  cannot flood everyone. A client may add only what it saw itself:
  uploads that finished or failed, with a link inside the app.
- The web app: a bell beside the logo with the unread count; its list
  marks one read when opened (and goes where it points), marks all read,
  removes one or all. It is checked every minute.
- Going away by themselves: the uploads panel closes ten seconds after
  everything is done unless the pointer is on it, and confirmations such
  as "Saved" after eight seconds; both can be closed at once. Errors stay
  until closed or fixed.
- Alternative considered: notifications in the browser's storage only
  (simpler, but not on other devices and lost with the browser's data).
