# Setlists, playlists & sessions

Three containers for songs, from everyday to gig-night:

| Thing | What it is |
|---|---|
| **Playlist** | An ordered list of songs. Add a song by searching (songs already in it show **Added** and can't be double-added); reorder with the up/down buttons; removing a song from a playlist never touches the library. |
| **Setlist** | A playlist in *performance mode* — it carries a **gig date** and drives the setlist player and band session. |
| **Collection** | A folder for playlists: **Worship / Events / Home / Other** — e.g. one collection per venue, one playlist per gig. |

## On the gig night

1. Open the setlist → **Play setlist** (fullscreen player).
2. Everyone opens the same setlist's player (phone, tablet, laptop, TV).
3. The player header shows **N in session**; the band turns pages together —
   see [The player → Band sessions](player.md#band-sessions-setlist-sync).
4. The **Playlists** page shows a *live sessions* bar, so a device that's late
   finds the running session with one tap (**Join session · N**).

## Follow sessions (browsing together)

Sessions aren't only for setlists. The sidebar **Sessions** panel:

- **Start session** — you host. Nothing to configure.
- Everyone who **Join**s follows *what you are looking at*: songs you open,
  pages you browse, the setlist player if you're in one. Joined devices navigate
  themselves.
- Host shows "Your session is live · N following"; **Stop** ends it; **Leave**
  un-follows. If the host disconnects, the session ends.

The sidebar rail carries a count of live sessions, so you can see the room is
gathering. Sessions are discovered from the server (all devices talk to the same
server, so "on the network" = "on this server").

## House rules worth knowing

- **Deleting a playlist keeps its songs; deleting a collection keeps its
  playlists** (they become unassigned).
- A setlist lives while at least one participant is connected; the last player
  closing retires the session.
- Session state is in memory by design: a server restart clears sessions
  (charts, playlists, accounts are all in the database — only live session
  positions reset).
