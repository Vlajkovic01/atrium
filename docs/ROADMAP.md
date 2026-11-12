# Atrium roadmap

Status: Phase 0 design, revised 2026-11-10 after the owner's review.

One phase at a time. Each phase ends with all tests passing, a summary of
what was built, known risks and open questions, and a stop for approval.

## Releases

| Milestone | After phase | Audience | Content |
|---|---|---|---|
| Alpha | 3 | Internal only | Web app: files, photos, albums, search |
| Beta | 5 | Internal only | Adds mobile app with camera backup, video streaming |
| **1.0, first public release** | **7** | Public | Adds subtitles, audio tracks, movie and series library |
| 1.1 | 8 | Public | Sharing, quotas, activity log |
| 1.2 | 9 | Public | Cast, AirPlay, picture-in-picture, player extras |
| 1.3 | 10 | Public | Android TV and Apple TV apps |
| 2.0 | 11 and 12 | Public | Operations tooling, hardening, native installers |

Phases 1 to 7 form the first public release (D-013). Alpha and Beta are
checkpoints for running Atrium on real hardware with real files, not
releases.

## Release gate for 1.0

Phases 11 and 12 come after 1.0, but a storage product cannot be published
without some of their content. These items are pulled forward and must be
true before 1.0, whatever phase they were scheduled in:

1. Metadata backup runs on schedule and before migrations, and restore is
   documented and tested from the command line.
2. Upgrade from every earlier tagged build migrates cleanly on SQLite and
   PostgreSQL.
3. Fault-injection tests pass: no file lost or half-written when the
   server is killed during upload, scan or jobs.
4. A security review of authentication, sessions, pairing, signed URLs,
   path handling and the setup flow, with findings fixed. The full OWASP
   ASVS review remains in Phase 12.
5. Health endpoints and the storage integrity report exist.
6. Runs acceptably on the minimum hardware: 4 ARM64 cores, 2 GB RAM, with
   a library of 100,000 files. The one-million-file test remains in
   Phase 12.
7. Installation guide, reverse proxy and tunnel guides, and a backup guide
   are written.
8. `docs/LICENSES.md` is complete, and the image's FFmpeg build and its
   source offer are in order.

The gate was accepted at the Phase 0 review (D-013). Each item is
scheduled into the phase where it is cheapest: items 1 to 3 and 5 grow
with Phases 1 to 3, items 4 and 6 to 8 are done at the end of Phase 7.

## Phases

| # | Phase | Result |
|---|---|---|
| 0 | Design | These documents |
| 1 | Foundation | Skeleton, auth and users, sessions, device pairing server side, local and S3 storage providers, setup wizard, Docker Compose, CI |
| 2 | Files | Job queue core, tus uploads, folders, rename, move, download, trash, file versions, deduplication, web file browser |
| 3 | Media processing | Media jobs and small-device tuning of the queue, metadata extraction, thumbnails, previews, timeline, albums, search |
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

- Phase 0 and Phase 1: done.
- Phase 2: built, awaiting the owner's approval. Besides the planned
  scope it delivered the portable metadata backup and command-line
  restore (D-026), scheduled user deletion (D-025) and the storage
  integrity check, which cover release-gate items 1 (backup, restore)
  and 5 (integrity report) in part. The change feed moved to Phase 4
  (D-027).

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
  accounts and signing should be set up during Phase 3.
