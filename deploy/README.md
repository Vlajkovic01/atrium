# Installing Atrium with Docker

Atrium is in early development. There is no released image yet; until the
first release, the commands below build the image from this repository.

## What you need

- A machine that stays on: a NAS, a mini PC, a home server or a VPS.
  64-bit ARM or x86, at least 2 GB of memory.
- Docker with the Compose plugin, version 2.20 or newer.

## Start

In this folder:

```
docker compose up -d
```

Then open `http://<address of the machine>:8080` in a browser. The setup
wizard asks for a **setup token**. It proves that you are the person who
controls the server. Read it with:

```
docker compose logs atrium
```

The token changes every time the server starts and stops mattering once
the first admin account exists. If you prefer to choose it yourself, set
`ATRIUM_SETUP_TOKEN` in `.env` before starting (see `.env.example`).

The wizard then creates the admin account and asks where files should be
stored. The default is the folder `/data` inside the container, which is
the `data` volume.

## Where your data is

| Volume | Path in the container | What it holds | Back it up? |
|---|---|---|---|
| `config` | `/config` | Database, server keys, backups | **Yes.** Without it, file names and folders are lost. |
| `data` | `/data` | The files themselves | **Yes.** |
| `cache` | `/cache` | Thumbnails and other rebuildable data | No. Safe to delete. |

Back up `config` and `data` together. The files are stored in Atrium's
own layout, named by their content; the names and folders live in the
database in `config`.

### Storing files in a folder of your choice

Replace the `data` volume with a bind mount in `docker-compose.yml`:

```yaml
    volumes:
      - config:/config
      - /mnt/storage/atrium:/data
      - cache:/cache
```

The server runs as an unprivileged user (ID 1654), so the folder must be
writable by that user:

```
sudo chown 1654 /mnt/storage/atrium
```

### Network shares and external disks

Mount the share or disk on the host first, then bind-mount it as above.
If it is ever not mounted when Atrium checks it, Atrium marks the storage
location as offline and writes nowhere else. It does not treat the empty
mount point as the location.

## Options

All options are set in a `.env` file next to `docker-compose.yml`. Copy
`.env.example` to `.env` and uncomment what you need.

### PostgreSQL

SQLite is built in and is the right choice for most installs. To use
PostgreSQL instead, uncomment the four PostgreSQL lines in `.env`. Choose
this before the first start; moving an existing install between databases
is not supported yet.

### HTTPS

Phones refuse plain HTTP to anything outside your home network, and a
password sent over plain HTTP can be read by anyone on the way. To reach
Atrium from outside, use one of:

- **The built-in HTTPS service.** If a domain name points at this machine
  and ports 80 and 443 are reachable, uncomment the HTTPS lines in `.env`.
  Certificates are obtained and renewed automatically.
- **Your own reverse proxy.** Point it at port 8080 and set
  `ATRIUM_TRUSTED_PROXIES` to the proxy's address, so Atrium believes its
  forwarding headers and nobody else's.
- **A tunnel service.** No ports are opened on your router. Set
  `ATRIUM_TRUSTED_PROXIES` to the address the tunnel client connects
  from.

## Updating

```
docker compose pull
docker compose up -d
```

The server updates its database on start. Before it does, it takes a
backup of the database into `/config/backups`.

## Stopping and removing

```
docker compose down
```

This keeps all volumes. `docker compose down --volumes` deletes them,
including every file stored in the `data` volume.
