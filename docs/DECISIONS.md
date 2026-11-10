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

## D-012 Setup token for first-run requests from public addresses (Proposed)

- Decision: setup from loopback or private addresses needs no token;
  requests from public addresses or through a proxy need the token from
  the server log.
- Alternatives: always require the token; never require it.
- Reasons: a fresh install on a VPS could be claimed by a stranger.
  Home installs keep the no-friction path.

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

## D-018 Serbian in Latin and Cyrillic, Cyrillic generated (Proposed)

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
