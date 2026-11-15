# Getting started

Ten minutes from an empty server to playing your first chart. You need a running
AhChord server — if yours isn't up yet, the [deployment guide](../DEPLOYMENT.md)
walks through `docker compose up`. The app is at `http://<your-server>:5001`
(by default; behind a reverse proxy it may be an `https://` address).

## 1. Make an account

Open the app and pick **Register**. Any username and password — there is no email,
no verification, nothing to check. Registration is open by default; if your server
runs with registration closed, the administrator gives you an account
(see `SEED_ADMIN_*` in the [deployment guide](../DEPLOYMENT.md)).

Sign in, and the dashboard greets you with an empty library and onboarding hints —
that's by design. **AhChord ships no songs**: lyrics are copyrighted, so the library
starts empty and you bring your own charts.

## 2. Add a song

Go to **Import** (sidebar). Two paths work with no extra setup:

- **Paste ChordPro** — paste a chart written in ChordPro (chords in `[square
  brackets]`). You get a live preview and editable guesses for title/artist/key;
  pick a target playlist (optional) and import.
- **Upload file** — drag `.cho` / `.txt` files in, one at a time or in a batch.

There is no chart in ChordPro format? The **Convert** tab turns a pasted chord
page into ChordPro using a local LLM — see
[Import](import.md); it needs the server to have a model configured.

Not sure what ChordPro looks like? Two paragraphs in [ChordPro basics](chordpro-basics.md).

## 3. Play it

Open the song from **Songs** (search box up top works everywhere) and press **Play**.
You're in the fullscreen player: a big **Next**, prev/next, Esc to leave. On a song
page you can also press **Play a random song** from the dashboard.

Try, right there in the player:

- **Capo** — pick fret 0–6; the shapes on screen change, the stored chart does not
  (it's a display transform, [the model explained](library-and-songs.md#the-key-capo--transposition-model)).
- **+ / −** — transpose a semitone at a time; the chip shows the key you'll hear.
- **Hover any chord** — a diagram: guitar finger positions or piano keys.

## 4. Make it yours

**Settings → Appearance**: dark (default) or light theme, app font size, and the
default chord diagram (guitar/piano). Settings persist per user and follow you to
every device signed in with that account.

## 5. Put it on your devices

- **Phone/tablet/TV (Android)** — the APK is served by your own server:
  **Settings → About → Download the Android app**. [Mobile & offline](mobile-and-offline.md).
- **Any browser** — AhChord is a PWA: use the browser's *Install* / *Add to home
  screen* and it runs standalone, with your recently used library cached for offline.

## Where to go next

- Build a **setlist** with a gig date and run the band in
  [real-time sync](setlists-and-sessions.md#on-the-gig-night)
- Bulk-fill the library from the [Convert + Search tabs](import.md)
- The [shortcuts page](shortcuts-and-faq.md) — the player is fast once you know
  the four keys that matter: `→` `+` `−` `R`
