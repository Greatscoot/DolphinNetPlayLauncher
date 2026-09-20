# Dolphin NetPlay Launcher 0.12.0 — Friend Groups Update

**Release date:** September 20, 2026

0.12.0 adds **Friend Groups**, making repeat Dolphin NetPlay sessions easier to organize without exchanging a new room code every time.

## What's new

- **Friend Groups** — save the people you regularly play with and see when a configured Friend is hosting.
- **Multiple saved groups** — switch groups directly from Host or Join.
- **`.dnlgroup` sharing** — export a ready-to-use Friend Group, send it to the group, and let each person choose their identity when importing it.
- **Portable member badges** — use generated initials or custom profile pictures that can travel with the `.dnlgroup` file.
- **Friend Host integration** — use the active group's configured session name, password, and region when hosting.
- **Friend Join integration** — load a recognized Friend into the normal Join workflow without manually exchanging a room code.
- **Same-network / LAN overrides** — save a local Direct-IP route for specific Friends on the same network.
- **LAN / Internet route switching** — temporarily switch a loaded Friend between the saved LAN route and the advertised Internet route.
- **Friend identity / Nickname sync** — the active Friend identity can automatically populate the main Nickname field.
- **Game banners** — Friends and Public Sessions can display Dolphin-style game banners, with Plain text mode still available.
- **Friends-first Join workflow** — when Friend Groups are configured, Friends becomes the default Join connection while Traversal, Direct IP, and Public Sessions remain available.

## `.dnlgroup` files

A `.dnlgroup` can include the group name, region, shared NetPlay password, member/session-name list, and optional custom member badges.

The importing PC chooses **which member they are**. That identity is excluded from its own Friend roster, while the other members become Friend targets.

Because the shared password travels with the file, treat a `.dnlgroup` as a **shared group secret** and only send it to the intended people. Per-PC LAN mappings are not included.

## Documentation

- `Documentation/Dolphin-NetPlay-Launcher-Friend-Groups-Guide-v1.pdf` — illustrated Friend Groups guide
- `Documentation/FRIEND-GROUPS-AND-DNLGROUP.md` — detailed Friend Groups reference
- `Documentation/Dolphin-NetPlay-Launcher-SRM-Setup-Guide-v6.pdf` — Steam ROM Manager setup guide
- `README.md` — project overview

Dolphin NetPlay Launcher does not include Dolphin or game files.

Dolphin NetPlay Launcher is unofficial and is not affiliated with or endorsed by the Dolphin Emulator project.
