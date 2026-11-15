---
layout: home
titleTemplate: false
title: AhChord — self-hosted chord charts for the whole band
hero:
  name: AhChord
  text: Self-hosted chord charts for the whole band
  tagline: One Docker container. ChordPro on every screen — phone, tablet, laptop, TV — with the setlist synced in real time.
  image:
    src: /AhChord/player-setlist.png
    alt: Full-screen setlist player with inline chords, setlist pager and layout controls
  actions:
    - theme: brand
      text: User guide
      link: /docs/user/
    - theme: alt
      text: Deployment guide
      link: /docs/DEPLOYMENT
    - theme: alt
      text: GitHub
      link: https://github.com/BunnyDad18/AhChord
features:
  - title: ChordPro is the source of truth
    details: >-
      Plain-text charts with chords in square brackets — portable, printable, yours forever.
      The capo and transpose are display-only (frets 0–6): a gig-key change never rewrites
      the stored chart.
  - title: The band plays together
    details: >-
      Live setlist sync over SignalR: on a gig night every device follows the same song, in
      the same key. Fullscreen player, follow sessions, random-song mode, keyboard control.
  - title: Runs on your network
    details: >-
      One Docker container plus Postgres — no cloud, no third-party accounts. The Android
      app, the PWA and the TV all talk to your own server, and the library works offline.
  - title: Bring your charts
    details: >-
      Paste ChordPro, upload .cho/.txt files, or convert a pasted chord page with your own
      local LLM. Search the library, keep collections, run setlists.
---
