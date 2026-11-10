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
dotnet test
dotnet run --project src/Atrium.Api
```

## Contributing

`master` is always releasable and changes only through pull requests that
pass CI. Work happens on short-lived branches named after the phase and
topic, for example `phase-1/auth`.

## License

[AGPL-3.0](LICENSE). Third-party licenses are listed in
[docs/LICENSES.md](docs/LICENSES.md).
