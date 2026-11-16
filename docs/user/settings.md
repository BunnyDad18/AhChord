# Settings

**Settings** (sidebar) has five tabs.

## Account

Your display name (shown in sessions and the shell), library stats (songs,
playlists), **change password**, and sign out. There is no email on an account
and no password-recovery mail — change your password here, and see the
[FAQ](shortcuts-and-faq.md#i-forgot-my-password) for the reset story.

## Appearance

- **Theme** — dark (the default) or light.
- **App font size** — S / M / L.
- **Chord diagrams** — default to guitar finger positions or piano keys
  (you can still switch per tooltip; the last choice sticks).
- **App logo** — a small pick-your-own set.

All of it applies instantly and persists on your account.

## LLM conversion

Powers **Import → Convert** (a chord page → ChordPro against a local
OpenAI-compatible model — Ollama, LM Studio, …).

- **Your settings** — a base URL + model (+ key) just for you.
- **Band default** (admin only) — the fallback for members without their own.
- The line at the top shows what's **in use** and its source:
  *your settings* → *band default* → the server's `.env` file. **Not configured**
  anywhere? In-app conversion is simply off; paste/upload import is unaffected.
- **Test** probes the endpoint before you commit to it.

The model runs on hardware you control; no chart text leaves your network
unless you point the URL at a cloud endpoint.

## Bug reports

Bugs are reported on GitHub: **[file an issue](https://github.com/BunnyDad18/AhChord/issues/new)**
— the template asks for what you did, what happened, and what you expected. The
old in-app **Report a bug** button was retired 2026-11-16; this tab lists the
reports submitted through it before that, and the admin marks resolved ones (on
a gated public instance, triage is admin-only).

## About

The server version, and the **Android app** row: current published app version
and the **Download the Android app (.apk)** link — the install path described in
[Mobile & offline](mobile-and-offline.md#the-android-app).
