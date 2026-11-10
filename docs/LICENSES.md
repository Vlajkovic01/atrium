# Licenses

Atrium is licensed under AGPL-3.0 (D-001). The license text is added to
the repository root in Phase 1.

Every dependency is recorded here before it is added. Nothing in this
file is legal advice; the flags mark where a lawyer's review is worth
having before public distribution.

## Flags

1. **FFmpeg build options.** FFmpeg is LGPL-2.1-or-later by default and
   becomes GPL when built with GPL components such as libx264 and libx265.
   A GPL-3.0 build is compatible with shipping beside an AGPL-3.0 server,
   and requires offering the corresponding FFmpeg source. A build with
   `--enable-nonfree` (for example libfdk-aac) cannot be redistributed at
   all and must never be used in an image or installer.
2. **Codec patents.** H.264, HEVC and AAC are covered by patent pools
   independent of software licenses. Distributing encoders and decoders
   can carry obligations in some countries, for commercial distribution in
   particular. Needs review before 1.0.
3. **AGPL and the Apple App Store.** Apple's terms conflict with the GPL
   family when the code has copyright holders who have not agreed to
   Apple's terms. The iOS and Apple TV apps can be published as long as
   the project owner holds copyright on all client code. Accepting outside
   contributions to `/mobile`, `/tv` or `/packages/shared` therefore needs
   a contributor license agreement first. Client apps must not include
   third-party GPL or AGPL code.
4. **S3 test server.** MinIO is AGPL-3.0 and is used only as a container
   in tests, never distributed. Its community edition's maintenance status
   should be checked at Phase 1; another S3-compatible server can replace
   it in tests if needed.

## Approved: the stack named in the project brief

| Component | License | Use |
|---|---|---|
| .NET 10, ASP.NET Core, EF Core | MIT | Server |
| SQLite | Public domain | Default database |
| Microsoft.Data.Sqlite | MIT | SQLite driver |
| PostgreSQL | PostgreSQL License | Optional database, separate container |
| Npgsql and its EF Core provider | PostgreSQL License | PostgreSQL driver |
| FFmpeg, ffprobe | LGPL-2.1+ or GPL-2+/3 by build; see flag 1 | External processes |
| React | MIT | Web |
| React Native | MIT | Mobile |
| react-native-tvos | MIT | TV |
| TypeScript | Apache-2.0 | All clients |
| Caddy | Apache-2.0 | Optional HTTPS container (D-006) |
| MinIO | AGPL-3.0 | Test container only; see flag 4 |

## Candidates, not yet approved

Listed so the choices are visible early. Each will be proposed with
alternatives at the start of the phase that needs it.

| Need | Phase | Candidates | License notes |
|---|---|---|---|
| Argon2id | 1 | Konscious.Security.Cryptography; libsodium binding | MIT; ISC and MIT |
| S3 client | 1 | AWS SDK for .NET; Minio .NET client | Apache-2.0 both |
| tus server | 2 | tusdotnet; own implementation | MIT |
| Image decoding and thumbnails | 3 | libvips through NetVips; SkiaSharp; ImageSharp; FFmpeg only | LGPL-2.1 and MIT; MIT; Six Labors Split License, which restricts some commercial use and is flagged; no new dependency |
| EXIF | 3 | MetadataExtractor | Apache-2.0 |
| Offline place names | 3 | GeoNames data | CC BY 4.0, attribution required |
| Translations runtime | 1 | i18next; FormatJS | MIT; BSD-3-Clause |
| HLS in browsers | 5 | hls.js | Apache-2.0 |
| Native video | 5 | react-native-video (Media3, AVPlayer) | MIT; Media3 is Apache-2.0 |
| Speech-to-text | 6 | whisper.cpp as an external process | MIT; model weights MIT |
