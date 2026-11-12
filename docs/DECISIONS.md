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

## D-013 First public release after Phase 7, with a release gate (Owner, gate Proposed)

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

## D-026 Portable metadata backup in Phase 2; first tag at Alpha (Proposed, Phase 2)

- Decision: the portable backup (D-008), its schedule, the copy into each
  storage location and the command-line restore are built in Phase 2.
  The initial migration keeps being regenerated until the first tagged
  build, which is Alpha at the end of Phase 3; from then on migrations
  are only added.
- Alternatives: build the backup in Phase 3 or with the release gate.
- Reasons: from Phase 2 on, file names and folders exist only in the
  database (D-002). Losing it without a backup loses every file in a
  usable form, so the backup has to exist as soon as files do.
