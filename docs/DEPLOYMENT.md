# Deployment Guide

AhChord runs as **one app container + one Postgres container**. The app
serves the API, the API docs, and the built React UI from a single origin on
port `5000` — no CORS, no separate static host.

## Prerequisites

| For | You need |
|-----|----------|
| Running the app | Docker + Docker Compose (v2) |
| Local development | .NET 10 SDK, Node 22 (the CI floor), Docker (for Postgres) |
| Optional chord-page conversion (ST-14) | Any OpenAI-compatible local LLM (Ollama / LM Studio) |

## Fresh clone → running app

```bash
git clone <your-repo> ahchord && cd ahchord

# 1. Configure
cp .env.example .env
#    - POSTGRES_PASSWORD: set your own
#    - JWT_SECRET:        generate a long random string, e.g. `openssl rand -hex 32`
#    (defaults in .env.example are fine for a private LAN)

# 2. Build + start (first build pulls node:20-alpine + dotnet:10 images and
#    runs the multi-stage build — a few minutes on a typical machine)
docker compose up -d --build

# 3. Watch it come up
docker compose logs -f app        # wait for "Now listening on: http://...:5000"
curl -s http://localhost:5000/api/health   # {"status":"ok","database":"ok",...}
```

Open `http://localhost:5000` and register a user. The library is **empty** on a fresh
install — the app seeds accounts, never content (ST-91: lyrics are copyrighted, the repo is
MIT). Add a chart, restore a backup, or convert a pasted chord page.

- App + API + UI: `http://localhost:5000`
- API docs (Scalar): `http://localhost:5000/scalar`
- Health: `http://localhost:5000/api/health`

> The app **fails fast at startup** if `JWT_SECRET` is missing/short in
> `Production` — that's by design (§Troubleshooting).

## Run the built image (the CD route — ST-104)

CI publishes every release tag as a container image on GHCR, so a server does not
need a toolchain or a source build — `docker-compose.prod.yml` is the overlay:

```bash
cp .env.example .env        # same configuration as above
export COMPOSE_FILE=docker-compose.yml:docker-compose.prod.yml

# Public repo images need no login; while the package is private, use a GitHub PAT
# with read:packages:  echo <pat> | docker login ghcr.io -u <you> --password-stdin
docker compose pull app
docker compose up -d
```

Update = `docker compose pull app && docker compose up -d` (pin a version with
`AHCORD_IMAGE_TAG=v0.1.0` in `.env`; `latest` is the default). The overlay carries
no gates and no env defaults — deployment config stays in the base compose + `.env`
(AHC-4: the self-hosted defaults survive every deploy). Source builds
(`docker compose up -d --build`) stay exactly as they were.

## Reverse proxy + TLS (public self-host)

Point the proxy at the app on `:5000`. The UI, API, **and the SignalR
WebSocket** (`/hubs/session`) all live under the same origin, so one rule
covers everything.

### Caddy (recommended — automatic HTTPS)

```
music.example.com {
    reverse_proxy 127.0.0.1:5000
}
```

Caddy handles WebSocket upgrades automatically.

### Nginx

```nginx
server {
    listen 443 ssl http2;
    server_name music.example.com;
    # ssl_certificate / ssl_certificate_key: your certs

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

        # SignalR (ST-25): WebSocket upgrade + long-lived SSE/poll
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600s;
        proxy_buffering off;
    }
}
```

Notes:
- If the app is bound to `127.0.0.1:5000`, remove the `"5000:5000"` port
  mapping in `docker-compose.yml` so it's only reachable through the proxy.
- HSTS: `add_header Strict-Transport-Security "max-age=31536000" always;`
  (recommended once TLS is stable).
- JWT is a Bearer token (and, for WebSockets, the `access_token` query
  param) — keep TLS on; tokens in URLs are the standard SignalR mechanism but
  should not cross the wire unencrypted.

## Backup & restore

All user data lives in Postgres (`ahchord`). The container's own
filesystem holds nothing user-specific (real-time session state is
in-memory and intentionally resets on restart — ST-25).

```bash
# Backup (while the app keeps running; dump is consistent)
docker compose exec postgres pg_dump -U ahchord -Fc ahchord > backup-$(date +%F).dump

# Restore into a fresh/emptied database
docker compose exec -T postgres createdb -U ahchord -O ahchord ahchord \
  || docker compose exec -T postgres dropdb -U ahchord ahchord
docker compose exec -T postgres pg_restore -U ahchord -d ahchord backup-2026-09-14.dump

# Or restore into a wiped volume
docker compose down -v
docker compose up -d postgres            # fresh empty volume
docker cp backup-2026-09-14.dump ahchord-postgres-1:/tmp/
docker compose exec -T postgres pg_restore -U ahchord -d ahchord /tmp/backup-2026-09-14.dump
docker compose up -d app
```

Verify: open the app, confirm a known song + setlist survived, and that
`GET /api/songs/recent` returns data.

> Suggested cadence: nightly `pg_dump -Fc` + copy off-box. The database is
> small (a personal song library), so full dumps are cheap.

## Updating to a new version

**Image deploy** (the CD route, ST-104 — see §Run the built image):

```bash
export COMPOSE_FILE=docker-compose.yml:docker-compose.prod.yml
docker compose exec postgres pg_dump -U ahchord -Fc ahchord > pre-update.dump
docker compose pull app && docker compose up -d
```

**Source deploy:**

```bash
git pull
docker compose up -d --build     # rebuild image, recreate app container
docker compose exec postgres pg_dump -U ahchord -Fc ahchord > pre-update.dump  # (before pulling, ideally)
```

EF Core migrations run automatically at app startup
(`Database.AutoMigrate`); back up first anyway. Data is preserved — only the
`app` container is recreated.

## Troubleshooting

**1. Port 5000 already in use / app never comes up**
```bash
docker compose logs app | tail -20     # look for "address already in use"
lsof -nP -iTCP:5000 -sTCP:LISTEN       # see who owns it
```
Fix: stop the other process, or change the host port in `docker-compose.yml`
(`"5000:5000"` → `"8080:5000"`) and point the proxy at the new port.
(Inside the container the app always listens on 5000.)

**2. Health check fails / “database” not ok**
```bash
docker compose ps                      # postgres healthy? app healthy?
docker compose logs postgres | tail -20
curl -s http://localhost:5000/api/health
```
The app waits for Postgres (`depends_on: service_healthy`); if Postgres is
down, `/api/health` returns **503** with `"database":"error"` and data
endpoints return 500 (the UI shows the error banner with a retry button).
Restart Postgres and the app recovers **without a restart** (verified):
`docker compose start postgres`.

**3. “Where are the sample songs?” after a fresh start**
There are none — by design (ST-91). A fresh database starts **empty**: the app seeds accounts
only, never content, because ChordPro lyrics are copyrighted works and this repo is MIT. The
Songs page says “No songs yet.”; fill the library by adding a chart, restoring a backup, or the
chord-page conversion. Startup seeding is accounts: the oldest user is promoted to admin when
no admin exists, plus `SEED_ADMIN_USERNAME` / `TEST_USER_PASSWORD` when configured.
```bash
docker compose exec postgres psql -U ahchord -d ahchord -c "SELECT count(*) FROM \"Songs\";"
```
0 rows on a fresh install is correct. On an install that had content, you restored a backup to
an empty volume, or you're looking at a different database — and the UI only loads data for
authenticated users.

**4. `JWT_SECRET must be set…` crash loop in Production**
Expected fail-fast: `.env` is missing `JWT_SECRET` (or it's < 32 chars) and
`ASPNETCORE_ENVIRONMENT=Production`. Set a proper secret (`openssl rand -hex 32`)
and `docker compose up -d app`. (In `Development` the app falls back to a
documented insecure dev key and prints a warning.)

**5. LLM unreachable / conversion slow** — in-app conversion (ST-14) needs an
OpenAI-compatible LLM: configure it in Settings → LLM conversion (the
`.env` trio `LLM_BASE_URL`/`LLM_MODEL`/`LLM_API_KEY` is the global
fallback). A queued draft fails with a readable error if the endpoint is
down; the manual path always works — “Copy prompt + content” puts the
bundled prompt + your page text on the clipboard for any chat LLM.

**6. `docker compose up --build` hangs on an image pull** (e.g. stuck on
`load metadata for node:20-alpine`)
```bash
docker info | grep -i proxy        # a daemon-level proxy can hang large blob downloads
curl -sS -o /dev/null -w '%{http_code}\n' https://mcr.microsoft.com/v2/   # 200 = registry itself is fine
```
If the registries respond to `curl` but Docker stalls, the daemon's
`HTTP(S)_Proxy` is the culprit. Workaround (used in this project): pull the
base images straight into the daemon with skopeo (bypasses the daemon
proxy), then build:
```bash
brew install skopeo
skopeo copy --override-os linux --override-arch arm64 \
  docker://mcr.microsoft.com/dotnet/sdk:10.0 docker-daemon:mcr.microsoft.com/dotnet/sdk:10.0
skopeo copy --override-os linux --override-arch arm64 \
  docker://mcr.microsoft.com/dotnet/aspnet:10.0-alpine docker-daemon:mcr.microsoft.com/dotnet/aspnet:10.0-alpine
docker compose up -d --build
```
(Or clear the proxy in Docker Desktop → Settings → Resources → Proxies.)

## Uninstall

```bash
docker compose down -v        # -v deletes the Postgres volume (all data!)
rm -rf .env node_modules WebApi/bin WebApi/obj Frontend/node_modules Frontend/dist 2>/dev/null
```
