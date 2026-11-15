# Shortcuts & FAQ

## Keyboard

**Fullscreen player** (both modes — keys are ignored while typing in a text field):

| Key | Action |
|---|---|
| `→` / `↓` | Next song (in a setlist session: everyone follows) |
| `←` / `↑` | Previous song |
| `Esc` | Exit the player |
| `+` / `=` | Transpose up 1 semitone (shared in a session) |
| `−` / `_` | Transpose down 1 semitone |
| `R` | Random song (single-song mode) |
| `PageDown` / `PageUp` | Step the chart one column |

**Everywhere:**

| Key | Action |
|---|---|
| type in the search box | live results; `↑`/`↓` to move, `Enter` opens, `Esc` closes |
| `Tab` | full keyboard reach — every control is focusable |
| `Esc` | closes a chord diagram / menu and returns focus |

The whole app is keyboard-accessible (Lighthouse a11y 100 on every route);
`prefers-reduced-motion` is honoured, and contrast is WCAG-verified in both themes.

## FAQ

### Why is the library empty on a fresh install?
On purpose. Lyrics are copyrighted, so AhChord ships content-free — you bring
your own charts. The [screenshots](../screenshots/README.md) show public-domain
lyrics with the project's own chord arrangements.

### The capo changed the chords on screen — did my chart change?
No, and it never will: capo and transposition are display-only transforms; the
stored ChordPro text is the single source of truth
([the model](library-and-songs.md#the-key-capo--transposition-model)).

### I forgot my password.
There is no email on an account, so there is no recovery mail. If you're signed
in anywhere: Settings → Account → change password. If you're not, on your own
server you own the database — the practical reset is a `psql` visit (the
[deployment guide](../DEPLOYMENT.md#backup--restore) covers DB access), e.g.
delete the user row and register again.

### Can I use the same account on my phone and laptop?
Yes — that's the usual band-of-one setup: host a follow session on the laptop,
join it from the tablet, and the tablet mirrors what you browse.

### What happens to a live session if the server restarts?
Clients reconnect and re-join automatically. Session *positions* live in memory,
so after a restart the host presses Next and the band re-converges. Songs,
playlists, accounts are all in Postgres and unaffected.

### Do I need a local LLM?
No. Paste/upload import, the player, sessions — none of it touches a model.
The LLM only powers Import → Convert; with none configured that one tab reports
*Not configured* and everything else works. Any OpenAI-compatible endpoint works
(Ollama, LM Studio, …) — see [Settings → LLM conversion](settings.md#llm-conversion).

### Where's the Android app published?
On your server — Settings → About serves the current APK
([Mobile & offline](mobile-and-offline.md)). Self-hosted distribution is the
design; there is no store listing.

### How do I back this up?
One `pg_dump` is the whole band's library
([backup & restore](../DEPLOYMENT.md#backup--restore)).

### Can I expose this to the internet?
No — AhChord is set up for a private LAN, and should not be connected to
the internet.

### Is the import a scraper?
No — paste-based by design. The app converts text *you* pasted
([Import](import.md)); it fetches nothing from chord sites.
(The URL path of Convert fetches the URL you give it, strips HTML, and hands it
to your model.)
