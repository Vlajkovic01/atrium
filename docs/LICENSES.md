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

## Approved for Phase 1

Approved by the owner on 2026-11-10. A version appears once the package
is actually in the repository.

| Component | Version | License | Use |
|---|---|---|---|
| Microsoft.AspNetCore.OpenApi | 10.0.14 | MIT | OpenAPI document generation |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | MIT | In-memory server for integration tests |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | Test host |
| xunit | 2.9.3 | Apache-2.0 | Test framework |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Test runner adapter |
| Microsoft.EntityFrameworkCore (Relational, Sqlite, Design) | 10.0.15 | MIT | Database access and migrations; brings SQLitePCLRaw (Apache-2.0) and SQLite (public domain) |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL License | PostgreSQL provider |
| dotnet-ef | 10.0.15 (tool) | MIT | Generates migrations at development time |
| Testcontainers.PostgreSql | 4.16.0 | MIT | PostgreSQL container in tests |
| NSec.Cryptography with libsodium | not yet added | MIT; ISC | Argon2id password hashing |
| AWSSDK.S3 | not yet added | Apache-2.0 | S3-compatible storage provider |
| Microsoft.AspNetCore.Authentication.JwtBearer | not yet added | MIT | Access token validation |
| Testcontainers for .NET (S3 module) | not yet added | MIT | S3 container in tests |
| pnpm | 12.18.0 (tool) | MIT | JavaScript workspace manager |
| Vite | not yet added | MIT | Web build |
| React Router | not yet added | MIT | Web routing |
| TanStack Query | not yet added | MIT | Web data fetching |
| i18next, react-i18next, ICU plugin | not yet added | MIT | Translations runtime |
| openapi-typescript, openapi-fetch | not yet added | MIT | Generated API client |
| qrcode.react | not yet added | ISC | QR codes for pairing and phone linking |
| Vitest | not yet added | MIT | Web unit tests |
| Playwright | not yet added | Apache-2.0 | Web end-to-end tests |
| ESLint, Prettier | not yet added | MIT | Lint and formatting |

TOTP is implemented in the server without a library.

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
