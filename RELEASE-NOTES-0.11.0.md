# Dolphin NetPlay Launcher 0.11.0

**Released:** September 18, 2026

Dolphin NetPlay Launcher 0.11.0 is the first final release of the 0.11 line. It packages the controller, Games, Sessions, lifecycle, performance, presentation, and documentation work validated during the 0.11 pre-release cycle.

## Highlights

- Controller-friendly Dolphin NetPlay **Host** and **Join** flows.
- **Traversal room code** and **Direct IP** connections.
- Integrated **Games** library with search, grid/list views, artwork, metadata, and controller navigation.
- **Public Sessions** browser plus optional public server-browser hosting controls.
- **Steam ROM Manager** integration for a dedicated NetPlay collection.
- SDL3 controller navigation with **60 Hz**, **120 Hz**, and **Match Monitor** launcher polling options.
- Dolphin version/update integration using Dolphin's own updater.
- Themes, interface sounds, first-run setup, portable-Dolphin handling, and privacy-redacted diagnostics.

## 0.11 improvements

- Fresh launcher configurations no longer import saved nickname, Direct-IP/Traversal targets, or public-session name/password values from an existing Dolphin profile. Nickname starts as `Player`; connection/public-host fields start blank.
- Fresh installs and Reset Options now use the intended showcase presentation by default: **Adventure Blue**, **Outfit**, **Animated Gradient** accents, animated background, and **Classic UI (Lokif CC0)** interface sounds. Existing saved preferences are not overwritten.
- Significantly improved repeated Games open/close performance and controller navigation responsiveness.
- Improved Sessions scrolling and selection behavior.
- Hardened launcher lifecycle/focus return after Host and Join sessions.
- Hardened failed-Join result handling without broadly automating unrelated Dolphin dialogs.
- Prevented periodic SDL device refreshes from manufacturing duplicate Accept edges while a face button remains held.
- Fixed an Options/font-lifetime crash exposed during high-refresh controller-polling testing.
- Removed temporary performance/resource/input instrumentation used during pre-release validation while keeping the normal diagnostic log.
- Refreshed the public README, quick-start, Steam ROM Manager instructions, Known Issues, and changelog.
- Added a GitHub-ready README showcase with short GIF/MP4 demonstrations of Steam Host, Games grid view, Games list view, and the public Sessions browser.
- Cleaned runtime diagnostic wording so public logs describe behavior without internal release-candidate labels.

## Known limitations

- When launched through Steam, controller input can intermittently stop responding in Dolphin's NetPlay lobby, most often while Joining. Mouse/keyboard remains a workaround.
- Controller availability after Dolphin closes can occasionally take roughly 5–6 seconds to return.
- The first Games open in a launcher process is heavier than later opens because initial library/load/layout work occurs there.

See `README.md` for installation, usage, troubleshooting, controller settings, Steam ROM Manager setup, and detailed Known Issues.

Dolphin NetPlay Launcher is unofficial and is not affiliated with or endorsed by the Dolphin Emulator project. Dolphin and game files are not included.
