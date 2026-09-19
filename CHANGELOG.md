# Changelog

This changelog summarizes user-facing releases and major milestones.
Internal test builds, release candidates, diagnostic builds, and experimental branches are omitted.

## 0.11.0 — 2026-09-18

### Added
- Public NetPlay Sessions browser with search/filtering and controller navigation.
- Optional public server-browser hosting controls, including session name, region, and password.
- Match Monitor controller-input polling, with 60 Hz and 120 Hz fallback options.
- Improved failed-Join handling and controller-aware Dolphin result-dialog fallback behavior.
- Improved return-to-launcher and return-to-lobby handling around Dolphin/NetPlay lifecycle changes.

### Improved
- Fresh launcher configurations now start with `Player` as the nickname and blank Traversal/Direct-IP/public-session identity fields instead of importing stale NetPlay values from an existing Dolphin profile. Join target history is launcher-owned after the user enters it.
- Fresh-install and Reset Options presentation now defaults to Adventure Blue, Outfit, Animated Gradient accents, an animated theme background, and Classic UI (Lokif CC0) interface sounds. Existing saved preferences remain unchanged.
- Games library responsiveness, selection styling, scrolling, preview loading, and repeated open/close performance.
- Sessions scrolling and selection behavior.
- Controller navigation responsiveness while Games or Sessions is open.
- Main-form lifecycle and focus restoration after Host/Join sessions.
- Theme/font lifetime handling when Options changes are applied.
- Steam/SRM controller behavior, including protection against duplicate Accept input across periodic SDL device refreshes.
- Release packaging, public documentation, diagnostics privacy, and first-run guidance.

### Fixed
- Duplicate controller Accept presses that could occur when a periodic SDL device refresh happened while a face button was still held.
- Options-related `Font.GetHeight()` / invalid GDI font failures exposed during high-refresh polling tests.
- Slow repeated Games opening caused by redundant nested WinForms layout passes.
- Sessions viewport jumps caused by forcing the selected row to the top on every controller move.
- Several failed-Join, Dolphin-focus, updater, and controller-return edge cases discovered during pre-release testing.

### Known limitations
- Under Steam, controller input can still intermittently stop working in Dolphin's NetPlay lobby, most commonly while Joining; mouse/keyboard remains a workaround.
- Controller availability after Dolphin closes can occasionally take several seconds to return.
- The first Games open in a launcher process is intentionally heavier than later opens because it performs initial library/load/layout work.

## 0.10.x

The 0.10 series turned the launcher from a mature local Host/Join utility into a more complete first-run, diagnostics, lobby-control, theming, and public-session experience.

- **0.10.12** — stabilized the Public Sessions path after cross-network testing and moved into release-preparation work.
- **0.10.11** — introduced the first Public NetPlay Sessions browser.
- **0.10.10** — polished user-extensible sound-theme management before the Sessions work.
- **0.10.9** — added user-extensible sound themes so private/copyrighted sets did not need to ship publicly.
- **0.10.8** — expanded theme polish, including OLED work, theme-aware launcher artwork, and interface-sound refinement.
- **0.10.7** — added controller operation for the small set of useful Dolphin NetPlay lobby actions such as Start, Buffer adjustment, and Quit.
- **0.10.6** — added the runtime troubleshooting/diagnostic log and supporting diagnostics workflow.
- **0.10.5 series** — hardened Host/Join against Dolphin startup update prompts and clarified Direct-IP versus IPv6 input limits.
- **0.10.4** — added narrowly scoped controller acknowledgement for Dolphin-owned updater dialogs.
- **0.10.3** — added the Join reminder that a locally selected game is not used when joining another host.
- **0.10.2** — added the first one-time notice explaining that Host/Join automation will control Dolphin.
- **0.10.1** — exposed the currently selected Dolphin installation path directly in the main window.
- **0.10.0** — introduced first-run onboarding, Dolphin discovery/initialization guidance, and clear Host-versus-Join library guidance.

## 0.9.x

The 0.9 series focused on the main launcher experience, predictable controller navigation, visual polish, compatibility, appearance, and startup speed.

- **0.9.8** — moved Games library loading off the normal startup path so Steam/SRM launches could appear quickly.
- **0.9.7** — introduced System / Light / Dark appearance modes and broad theme application across the launcher.
- **0.9.6 series** — hardened elevation/focus behavior carried forward into later builds.
- **0.9.5** — improved Dolphin foreground/focus handling for secondary-machine compatibility while preserving the proven NetPlay-opening sequence.
- **0.9.4** — corrected WinForms minimum-height/client-area sizing behavior.
- **0.9.3** — polished the main layout and made the primary Host/Join action more obvious.
- **0.9.2** — refined controller-prompt placement and the left-side launcher layout.
- **0.9.1** — introduced the first major visual pass over the explicit controller-navigation layout.
- **0.9.0** — replaced generic positional controller navigation with an explicit logical navigation map for the main launcher.

## 0.8.x

The 0.8 series built the modern Games cover-grid experience and made it practical from a controller.

- **0.8.11** — added the controller Select/View shortcut that focuses Clear without immediately clearing the selected game.
- **0.8.10** — added controller access to the Games toolbar from both Grid and List views.
- **0.8.9** — polished grid selection presentation and selected-title styling.
- **0.8.8** — fixed grid reordering caused by bringing FlowLayoutPanel tiles to the front for the controller cursor.
- **0.8.7** — made grid scrolling feel natural instead of repeatedly repositioning the selected tile toward the top-left.
- **0.8.6** — tightened grid cursor/scroll synchronization around WinForms AutoScroll behavior.
- **0.8.5** — corrected cases where fast navigation could move the controller cursor toward an off-screen tile before scrolling caught up.
- **0.8.4** — improved controller entry into Games so the current grid/list content is immediately navigable.
- **0.8.3** — added direct controller navigation in Grid view while preserving the user's preferred library view.
- **0.8.2** — refined grid spacing and added persistent selected-cover framing.
- **0.8.1** — tightened cover-grid spacing and dimensions.
- **0.8.0** — introduced the first Games cover-grid view alongside the established list view.

## 0.7.x

The 0.7 series added richer selected-game artwork and metadata using Dolphin's own cached data.

- **0.7.3** — improved the selected-game header layout so title/metadata did not compete with artwork and action buttons.
- **0.7.2** — added the Clear action for the active game selection.
- **0.7.1** — added an experimental selected-game banner path using Dolphin's `gamelist.cache` data.
- **0.7.0** — added selected-game cover art using Dolphin's existing `GameCovers` cache, with Game ID/revision details and no separate artwork downloader.

## 0.6.x

The 0.6 series established the integrated Games side panel and most of the controller-navigation behavior that later releases refined.

- **0.6.8** — added controller quick-action shortcuts, including Paste behavior in Join mode.
- **0.6.7** — refined controller shortcuts, Games binding, and Back behavior.
- **0.6.6** — improved startup focus and standalone/controller prompts.
- **0.6.5** — increased held-direction library scrolling speed.
- **0.6.4** — added monitor-matched controller-highlight animation and a manual 30–360 Hz animation-rate option.
- **0.6.3** — moved the controller glow into a separate click-through overlay so it could animate consistently across nested controls.
- **0.6.2** — replaced the old hard selection box with the animated rounded controller glow and improved numeric-field navigation.
- **0.6.1** — moved the Games library into a hideable right-side panel in the main launcher.
- **0.6.0** — introduced the first integrated Dolphin-configured Games library, searchable list, resize support, and reset-window-size option.

## 0.5.x

The 0.5 series introduced SDL controller support and made standalone use practical.

- **0.5.2** — added a visible controller focus cursor, deterministic directional navigation, and a packaged standalone GameCube/Wii picker.
- **0.5.1** — improved controller visual feedback, grace-period navigation, and standalone launch behavior.
- **0.5.0** — introduced SDL3 controller navigation, controller selection/testing, and the first Steam/outside-Steam controller support pass.

## 0.4.x

The 0.4 series established updater safety, NetPlay lifecycle auto-close, Options, and diagnostics foundations.

- **0.4.3** — cleaned up Options/diagnostics wording and removed stale or irrelevant paths.
- **0.4.2** — added persistent Options for auto-close, grace period, automation warning, remembered mode, Reset Options, path inspection, and privacy-redacted diagnostic copying.
- **0.4.1** — added graceful Dolphin auto-close after the NetPlay lobby/session ends while explicitly protecting active gameplay.
- **0.4** — hardened Dolphin's update flow so the launcher waits for updater state and avoids closing or interfering with Dolphin at unsafe times.

## 0.3.x

- **0.3.2** — fixed Dolphin user-folder/config resolution across normal and portable installations.
- **0.3** — introduced the redesigned Host/Join interface with clearer game title, Game ID, revision, and mode-specific controls.

## 0.2

- Added Dolphin Release/Development update-track detection and integration with Dolphin's own updater.

---

Earlier and intermediate letter-suffix builds were development/test iterations rather than user-facing changelog entries. Their full history is preserved in the development archive.
