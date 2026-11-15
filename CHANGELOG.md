# Changelog

All notable changes to **AhChord** are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).


> **Per-task flow** (see `AGENTS.md` → Per-task workflow): every completed piece of
> work — task, feature, or fix, no matter how small — gets a dated entry under
> `[Unreleased]` and is committed. Promote `[Unreleased]` to a version heading when a
> release tag is cut (e.g. `[0.1.0] - 2026-11-12`).

---

## [Unreleased]

### Fixed
- 2026-11-15 — **Release publishing survives tag moves** — the release job now deletes any existing release for the tag before creating its own; moving a pre-release tag used to race GitHub's release re-creation and fail the publish step (`gh release create: already exists`).
- 2026-11-15 — **API suite no longer flakes on CI** — Hangfire's static log provider could outlive a disposed test host and poison a later fixture's job enqueues with `ObjectDisposedException: LoggerFactory` (500s from `POST /api/songs` in `SongTagEndpointsTests`). The fixture now releases the binding on dispose (the AHC-9 static-side-effect class, test seam).

### Changed
- 2026-11-15 — **One release version everywhere** (ST-127) — the release tag is the product version: `/api/health`, `/api/info` (Settings → About), the GHCR image tag and the APK all report `0.5.0`; the commit-count version derivation is retired.
- 2026-11-15 — **The GitHub repo has a real history and a PR-gated publish** (ST-127) — one "Initial launch" commit, then one squash-merged PR per publish batch (CI gates the PR); tags are cut from main after merge. The 12×-moved `v0.4.19` tag, the disconnected `v0.1.0`, and 13 draft releases are gone; `v0.5.0` is the first release of the new order.
- 2026-11-15 — **The Android shell checks for updates at startup only** — no more re-check on every resume; opening the app is the moment an update is offered, tabbing back from the launcher is not.
- 2026-11-15 — **The release strategy revolves around GitHub** (ST-126) — the pipelines own every artifact: `release.yml` builds the GHCR image and the signed APK, deploys are a pinned `compose pull` of the release image, and the in-app update serves the CI-built APK. Local APK builds and source-image deploys are retired (strategy in `docs/RELEASES.md`).

---

## [0.4.19] - 2026-11-15

### Added
- 2026-11-15 — **Android APK 0.4.19** (MT-06) — the phone app ships the renamed conversion surface (paste-only Convert); the in-app update on the band server carries installed phones forward. Server deploys first, the APK second.
- 2026-11-14 — **GitHub Pages user guide site** (ST-114) — the user guide, deployment guide and screenshots, with search, at [bunnydad18.github.io/AhChord](https://bunnydad18.github.io/AhChord/). **Development docs are no longer published**: `scripts/publish-github.sh` (the ST-113 guard) is the only path to the public repo — one squashed commit with the task queue, gate rules, architecture/roadmap/journal and `AGENTS.md` stripped, the README trimmed, and the AHC-5 privacy sweep run.
- 2026-11-14 — **Tags** (ST-106) — songs carry **multiple artists** and **multiple genres**, plus free **tags** shared by songs and setlists: chip inputs with autocomplete in the editor (artists reorderable), filter chips on the songs list, tag chips on setlists, and the collections page is now a **tag browser** (collections retire into tags — old collection links keep working). Genre names are normalised per word, so `pop` and `Pop` are one genre.
- 2026-11-14 — **Key from the chords** (ST-106) — a song's key is detected from the chart's own chords (capo and metadata never move it); the editor shows the detection confidence, a **Detect from chords** button, and a hand-set key stays yours until you clear it.
- 2026-11-13 — **Support the project** — PayPal and Ko-fi links in the README.
- 2026-11-12 — **Follow sessions are general** (ST-86/ST-87) — press **Start session** in the sidebar and joined devices follow whatever the host browses (a song or a setlist), live; live sessions are listed with a **Join** row.
- 2026-11-12 — **Fullscreen button** in the sidebar foot, top bar, and player dock, where the Fullscreen API is available (ST-105).
- 2026-11-12 — **"Chords" toggle in the player dock** — hide chord glyphs for singers and sight-reading; per-device, display-only (ST-105).
- 2026-11-12 — **"Clear imported (N)"** retires imported conversions from the queue in one click, and the queue converges live without a reload (ST-105).
- 2026-11-12 — **Docker image on GHCR** with a `docker-compose.prod.yml` overlay — updating is `docker compose pull && docker compose up -d` (ST-104).
- 2026-11-12 — **Android APKs published on GitHub Releases** (the in-app update path is unchanged) (ST-104).
- 2026-11-12 — **User guide** (`docs/user/`) for players and band members, plus **CONTRIBUTING.md** and issue templates (ST-100).
- 2026-11-09 — **App version surfaced** — a quiet version line in the sidebar foot; Settings → About names the APK version beside the download row.
- 2026-11-08 — **Android app: JWT in the Android Keystore** (encrypted at rest, an existing session migrates transparently); cleartext HTTP scoped to private/LAN addresses (MT-02).
- 2026-11-07 — **Fit-to-height columns in the player** on PC/tablet with ‹ › paging and mouse-wheel paging; phones keep vertical scroll with an auto-hiding dock bar (ST-83/ST-84).

### Changed
- 2026-11-15 — **Convert takes any chord page** (ST-115) — the converter is source-agnostic: paste a chord page's text and convert it with the server's LLM, review, import. The **URL-fetch path is removed** — the app fetches no pages (paste is the contract, and one server-side fetch surface disappears with it). Renamed across the API (`/api/import/convert*`), the queue, the UI and the docs; the prompt ships at `docs/prompts/chord-page-to-chordpro.md` for the manual path.
- 2026-11-15 — **README is feature-led** — what the app is for, the hero screenshot (a long song in columns), a **GHCR quick start** (one compose file, `docker compose pull`, no clone), the three required config variables, and the docs site as the documentation entry point.
- 2026-11-14 — **Search and filters know the tag lists** (ST-106): searching finds songs by any artist, genre, or tag (not just titles), the artist/genre filter dropdowns read the tag tables, and the songs list groups versions by **title** — so multi-artist versions of one song sit in one row.
- 2026-11-13 — **`CHANGELOG.md` is now the concise, user-facing change log** — the full per-commit engineering journal moved to `docs/CHANGELOG-journal.md` (dev repo) (verbatim), and internal task provenance (user quotes, dev-box detail, session narrative) was scrubbed from the public docs surface (README, SECURITY, E2E checklist, Mobile README, ROADMAP).
- 2026-11-12 — **Public-demo security gates are config, and the defaults keep the LAN product** (ST-90): `REGISTRATION_OPEN`, `SEED_ADMIN_*`, `RATE_LIMIT_*`, `LIBRARY_WRITES_ADMIN_ONLY`, `JWT_STAMP_CHECK`, `TRUSTED_PROXY_IPS`, `LLM_ALLOW_PRIVATE_NETWORK` — each documented in the README and SECURITY.md.
- 2026-11-12 — **Player polish** (ST-84) — centred title, one-row control bar on phones, safe-area insets for the Android system bars, one hover convention, ghost paging arrows, quieter CCLI/copyright footers, bug-report button moved into the dock.
- 2026-11-12 — **Sidebar collapse is a thin grip on the panel's edge** and animates; foot-row spacing evened (ST-85).
- 2026-11-12 — **Change-password returns a fresh token** — your device stays signed in; with `JWT_STAMP_CHECK=true` the other devices sign out, which is the control working (ST-90).
- 2026-11-10 — **README "What it looks like"** — screenshots made from public-domain charts with our own arrangements (provenance and licensing rules in `docs/screenshots/README.md`).

### Fixed
- 2026-11-15 — **Background work now enqueues through an injected job client** (ST-117, found in the ST-115 review) — all eight Hangfire enqueues moved off the static `BackgroundJob` client, so the invariant holds in the product, not only in the fixtures.
- 2026-11-15 — **Conversion input cap counts bytes, not characters** (ST-122, found in the ST-115 review) — a CJK-heavy paste passed the "max 512 KB" cap at up to ~3× the bound; the cap is the UTF-8 byte length now, pinned by tests.
- 2026-11-15 — **Docs tell the truth again** (ST-118/ST-119/ST-120/ST-121/ST-124, found in the ST-115 review) — the convert endpoint's request shape in the architecture reference is `{content}` only; the serial-queue mechanism is documented as Hangfire's `llm` queue (not a semaphore); the deployment prerequisites say Node 22 (the CI floor); the changelog's ST-93 entry describes what actually shipped; the import guide describes the prompt's real section headings (`{comment: …}`).
- 2026-11-15 — **Manual prompt can no longer drift** (ST-123, found in the ST-115 review) — the Convert tab's embedded prompt is a tested copy of `docs/prompts/chord-page-to-chordpro.md`: editing one side without the other turns CI red.
- 2026-11-14 — **Tag filter filters online too** (ST-112, found in the ST-106 review) — the songs list sent the tag filter under the wrong query key, so chip filters silently returned the whole library on the server (offline agreed, online did not); the wire format is now pinned by tests.
- 2026-11-14 — **Build gate on clean checkouts** (ST-111, found in the ST-106 review) — a nullable DTO read in the tag endpoint tests broke the zero-warnings Release build on a fresh clone, which CI's incremental-free build catches and a dev box does not.
- 2026-11-13 — **Songs list keeps its position on Back** — the position save was a race with the list's skeleton render; the restore now settles in a bounded window (ST-105 follow-up).
- 2026-11-12 — **`[[G]]` double-bracket chords** from tab converters render as chords, not lyric text; stored chart text untouched (ST-105).
- 2026-11-12 — **SSRF on the LLM settings surface** (ST-88) — member-supplied LLM/test URLs are checked against the SSRF policy; `LLM_ALLOW_PRIVATE_NETWORK=false` refuses private/internal targets.
- 2026-11-12 — **Queue box shows only while work runs**, 3-column pages no longer show an inner scrollbar, the "conversions running" banner, search-bar height, setlist toggle, gig-date field starting empty, and a labelled **Report a bug** row on phones (ST-105).
- 2026-11-10 — **A clean checkout runs the full suite** — the app boots with the connection string the checkout supplies, and the test fixture substitutes Hangfire's storage, so tests never dial the database (ST-93).
- 2026-11-10 — **Gate defaults** (ST-90 review) — `JWT_STAMP_CHECK` is off by default so a LAN deploy signs nobody out; the import rate limiter covers only the routes that queue work, not polling; a database outage during the stamp check answers 401, not 500.
- 2026-11-09 — **CORS echoes origins with credentials** — the Android app negotiates the SignalR hub (MT-02).
- 2026-11-09 — **Sessions hosted by your own account are joinable** on a second device — laptop hosts, tablet follows (ST-87).
- 2026-11-08 — **The phone connects over HTTPS** — the APK bundles the deployment's live root CA (MT-02).
- 2026-11-07 — **Player top bar stays put** when a capo/transpose is set, and flow pages pack with no scrollbar inside a page (ST-83/ST-84).

### Removed
- 2026-11-10 — **Demo seed content is gone — a fresh install starts with an empty library** (ST-91). Lyrics are copyrighted, so the app ships content-free.

## [0.1.0] - 2026-11-12

Initial public release (MIT) — the complete self-hosted chord-chart product:

- **ChordPro as the source of truth** — paste, edit, and play standard `.cho` content; chords and sections render from the stored text.
- **Capo & transpose are display-only** (frets 0–6 / ±12 semitones) — a display transform never rewrites the stored chart.
- **Guitar + piano chord diagrams** (hand-rolled SVG), inline/above chord modes, multi-column layout, font scaling.
- **Playlists, setlists, collections**, a shared library, per-user recents, and a **fullscreen player** with keyboard + touch navigation.
- **Real-time band sync** (SignalR) and general **follow sessions**.
- **Import** — paste ChordPro, upload `.cho`/`.txt`, in-app **LLM conversion** of pasted chord pages via any OpenAI-compatible local endpoint (review before save), MusicBrainz + Songsterr search with a wishlist, metadata auto-review proposals.
- **Android app** (phone/tablet/TV) — APK served by the app with in-app updates, Keystore-backed auth, offline library with a write queue, TV d-pad support.
- **Security pass** — authz hardening, SSRF guards, owner-scoped drafts, rate limiting, opt-in public-demo gates; accepted trade-offs documented in SECURITY.md.
- **Ops** — single Docker image + PostgreSQL, GHCR publish, health endpoint, Scalar API docs, a 0-warnings CI gate, 627 API + 134 web tests.
- **Docs** — user guide, deployment guide, E2E checklist, performance notes, and the UG→ChordPro conversion prompt.

The pre-publication internal history (0.2.x/0.3.x) is in
[`docs/CHANGELOG-archive.md`](docs/CHANGELOG-archive.md) and the journal above.
