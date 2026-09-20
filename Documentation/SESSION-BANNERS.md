# Session and Friend Game Banners

Dolphin NetPlay Launcher shows Dolphin-style game banners by default in **Public Sessions** and **Join → Friends** when matching local artwork is available, while keeping the text needed to understand and join a lobby.

## Turn banner mode on

Open **Options → Appearance → NetPlay Browser** and choose:

- **Banners** — the default presentation; adds local 3:1 game artwork where Dolphin NetPlay Launcher can match it.
- **Plain text** — keeps the compact text-only presentation.

Plain text remains available at any time. Missing artwork never blocks a session or Friend entry; that row simply falls back to text.

## What banner mode shows

### Public Sessions

Public Sessions keeps its existing three-line row and adds a larger banner on the left when artwork is available. The row still shows the session name, game title, region, player count, lobby state, and password state. When the advertised game name contains a launcher-style **Game ID / Revision**, those values are split out and kept as text rather than being hidden by the artwork.

### Join → Friends

Friend rows become slightly taller in banner mode. They can show:

- the member's generated or custom profile badge;
- Friend name and Ready / Loaded / In game / version state;
- game title plus Game ID / Revision when known;
- a compact game banner on the right.

The plain-text mode keeps the smaller established Friends rows.

## Where banners come from

Dolphin NetPlay Launcher uses this priority:

1. For normal Dolphin NetPlay names with a Game ID, a matching native/custom banner is read directly from Dolphin's existing **gamelist.cache**. You do **not** need to open the launcher's Games panel first.
2. For nonstandard names, launcher title/path metadata is used as a local fallback when available.
3. A PNG in Dolphin NetPlay Launcher's **SessionBanners** override folder.
4. No image — normal text fallback.

Use **Options → Appearance → NetPlay Browser → Open Banner Folder** to create/open the override folder.

Useful override names are:

```text
SessionBanners\GM4E01.png
SessionBanners\RMGE01.png
SessionBanners\Some Custom Hack Name.png
```

A Game ID filename is preferred when the advertised lobby provides one. Exact game-title filenames are useful for hacks, translations, randomizers, or public sessions whose name does not map cleanly to a local library entry.

## Performance behavior

Sessions and Friends are owner-drawn WinForms lists. Banner mode deliberately follows the project's existing performance rules:

- no banner-file reads during row painting;
- no image decoding/resizing during controller navigation paint;
- banners are normalized/cached during the existing background refresh work;
- Game-ID matching reads Dolphin's cache directly instead of requiring a startup Games-library scan;
- unrelated Dolphin banner pixel payloads are skipped while searching the cache;
- cached thumbnails are reused by both Sessions and Friends;
- missing art immediately uses text instead of blocking the UI.

If you add a manual override while the launcher is already open, use **Refresh** in Sessions/Friends so the next background refresh can load it.

## Why there is no automatic banner downloader yet

Automatic download is intentionally deferred. Native Dolphin banners are a specific 3:1 type of artwork, while many public game-art services primarily provide covers or other shapes. Dolphin NetPlay Launcher should not silently substitute unrelated art or create a fragile naming/downloading dependency just to fill every row.

A future downloader can be added after a reliable source, naming policy, cache behavior, and redistribution expectations are established. The local Dolphin cache plus the manual override folder provide a deterministic fallback today.

## Presentation details

- Friends banner rows use **36 px** rows so the profile badge, game information, and selection highlight remain readable.
- Public Sessions banner rows use **86 px** rows with four text lines and a compact 108×36 banner so Game ID/revision, region, player count, state, and password information remain visible.
- Plain-text mode keeps the compact presentation.
- Theme/font refreshes preserve the correct Friends row height for the selected Plain/Banners mode.

Games absent from the local Dolphin cache—or games for which Dolphin has not populated banner data—fall back to text. `SessionBanners` overrides remain the local artwork fallback.
