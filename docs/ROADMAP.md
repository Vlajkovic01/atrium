# Atrium roadmap

Status: revised 2026-11-12 (D-030, D-031).

One phase at a time. Each phase ends with all tests passing, a summary of
what was built, known risks and open questions, and a stop for approval.

## Release

There are no intermediate releases, no Alpha or Beta and no version tags
while Atrium is developed (D-030). The first release, and the first tag,
comes after Phase 12, once the release requirements below are met.

Until then the initial database migration is generated again whenever
the schema changes (D-026). A database created by an earlier development
build cannot be upgraded and is created again.

## Release requirements

All of these must be true before the first release. They were first a
gate pulled ahead of a release after Phase 7 (D-013); with one release
at the end, most of them fall into Phases 11 and 12, and the items that
protect files keep growing with every phase.

1. Metadata backup runs on schedule and before migrations, and restore is
   documented and tested from the command line. Built in Phase 2.
2. The initial migration is frozen at the release tag. From then on,
   every later build upgrades a database of every earlier release cleanly,
   on SQLite and PostgreSQL, and CI tests it.
3. Fault-injection tests pass: no file lost or half-written when the
   server is killed during upload, scan or jobs.
4. A security review of authentication, sessions, pairing, signed URLs,
   path handling, external libraries and the setup flow, with findings
   fixed. Part of the OWASP ASVS review in Phase 12.
5. Health endpoints and the storage integrity report exist. The integrity
   report was built in Phase 2.
6. Runs acceptably on the minimum hardware: 4 ARM64 cores, 2 GB RAM, with
   a library of 100,000 files, and the one-million-file test of Phase 12.
7. Installation guide, reverse proxy and tunnel guides, and a backup guide
   are written.
8. `docs/LICENSES.md` is complete, and the image's FFmpeg build and its
   source offer are in order.

## Phases

| # | Phase | Result |
|---|---|---|
| 0 | Design | These documents |
| 1 | Foundation | Skeleton, auth and users, sessions, device pairing server side, local and S3 storage providers, setup wizard, Docker Compose, CI |
| 2 | Files | Job queue core, tus uploads, folders, rename, move, download, trash, file versions, deduplication, web file browser |
| 3 | Media processing | Media jobs and small-device tuning of the queue, metadata extraction, thumbnails, previews, timeline, albums, search by name, date and type, external photo libraries. No location (D-031) |
| 4 | Mobile app | Native Android and iOS app (React Native), installed from a store or an APK: connect, browse, upload, camera backup, offline, biometric lock |
| 5 | Video streaming | Capability detection, direct play, HLS, hardware transcoding, players, synced position |
| 6 | Subtitles and audio | Tracks, conversion, burn-in, styling, providers, speech-to-text, translation |
| 7 | Movie and series library | Indexing, name parsing, metadata providers, seasons, continue watching |
| 8 | Sharing and multi-user | Share links, shared albums and folders, quotas, activity log |
| 9 | Player extras | Chromecast, AirPlay, picture-in-picture, background audio, speed, shortcuts |
| 10 | TV apps | Native Android TV / Google TV and Apple TV apps (react-native-tvos), pairing sign-in, home, details, player, platform rows |
| 11 | Operations | Admin dashboard, health, backup and restore UI, library relocation, Windows service, Linux packages |
| 12 | Hardening | ASVS review, scale and low-power tests, documentation |

## Status

- Phases 0, 1 and 2: done. Besides its planned scope, Phase 2 delivered
  the portable metadata backup and command-line restore (D-026),
  scheduled user deletion (D-025) and the storage integrity check, which
  cover release requirements 1 and 5 in part. The change feed moved to
  Phase 4 (D-027).
- Phase 3: in progress.

## Sequencing notes

- The job queue core (persistence, leases, retries, priorities, progress)
  is built in Phase 2, one phase earlier than the brief, because trash
  retention, upload cleanup and blob collection already need it and
  temporary timers would be written only to be thrown away (D-017).
  Phase 3 adds media jobs, concurrency classes and hardware profiles.
- Device pairing is built on the server in Phase 1 and gets its approval
  screens on the web then. Its first real client is the TV app in
  Phase 10, so Phase 1 includes a test client that exercises the flow.
- Mobile store review and the TV store programs have lead times; store
  accounts and signing should be set up well before the release.
