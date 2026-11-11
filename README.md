# Atrium

Self-hosted personal cloud and media server: files, photos and video
streaming on your own storage, with web, mobile and TV apps.

Atrium is in early development. There is no installable release yet.

## Documentation

- [Design](docs/DESIGN.md): architecture, data model, storage, security, media pipeline
- [API sketch](docs/API.md)
- [UX](docs/UX.md): screens and flows for web, mobile and TV
- [Roadmap](docs/ROADMAP.md)
- [Decisions](docs/DECISIONS.md)
- [Licenses](docs/LICENSES.md)

## Repository layout

| Path | Content |
|---|---|
| `server/` | Server: C# on .NET 10, ASP.NET Core |
| `web/` | Web app: React with TypeScript |
| `mobile/` | Android and iOS app: React Native |
| `tv/` | Android TV and Apple TV app: react-native-tvos |
| `packages/shared/` | Generated API client, domain logic and player core shared by all clients |
| `deploy/` | Docker Compose and packaging |
| `docs/` | Design documents |

Paths appear as the phases in the roadmap reach them.

## Building the server

Requires the .NET 10 SDK.

```
cd server
dotnet test --filter "Category!=Containers"
dotnet run --project src/Atrium.Api
```

Tests in the `Containers` category start PostgreSQL in Docker. Plain
`dotnet test` runs them too and is what CI does.

The server keeps its database and keys in the directory named by
`ATRIUM_CONFIG_DIR` (default `.atrium` in the working directory). SQLite
is the default database; set `ATRIUM_DB_PROVIDER=postgres` and
`ATRIUM_DB_CONNECTION` to use PostgreSQL.

## Building the clients

Requires Node.js 24 or newer and pnpm.

```
pnpm install
pnpm run typecheck
pnpm run test
```

`packages/shared/src/api/schema.d.ts` is generated from the OpenAPI
contract in `packages/shared/openapi/atrium-v1.json` and committed. The
contract itself is exported from the server by running the server tests
once with `ATRIUM_UPDATE_OPENAPI=1`; then run `pnpm run generate`. CI
fails if the generated file is out of date.

The interface is translated into English and Serbian (Latin script), in
`packages/shared/src/i18n/locales`.

### Web app

For development, start the server and the web app's dev server; the dev
server forwards API calls to the server:

```
dotnet run --project server/src/Atrium.Api --urls http://localhost:5000
pnpm --filter @atrium/web run dev
```

Opened from the same machine, the setup wizard does not ask for the setup
token. From another machine it does; the server prints the token to its
log at startup.

End-to-end tests build the web app, start the real server on a fresh
config directory and drive it in a browser:

```
pnpm --filter @atrium/web run build
pnpm --filter @atrium/web exec playwright install chromium
pnpm --filter @atrium/web run e2e
```

Where Playwright's browser cannot be downloaded, use one that is already
installed by setting `ATRIUM_BROWSER_CHANNEL` to `msedge` or `chrome`.

`pnpm --filter @atrium/web run screenshots` walks through the same app
and saves a picture of every screen into `screenshots/`.

## Contributing

`master` is always releasable and changes only through pull requests that
pass CI. Work happens on short-lived branches named after the phase and
topic, for example `phase-1/auth`.

## License

[AGPL-3.0](LICENSE). Third-party licenses are listed in
[docs/LICENSES.md](docs/LICENSES.md).
