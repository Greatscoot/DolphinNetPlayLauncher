DOLPHIN NETPLAY LAUNCHER
========================
Release: 0.11.0

This is the first final 0.11.0 release. It includes Match Monitor high-refresh controller input,
responsive Games/Sessions navigation, Options/font safety, the periodic SDL-refresh double-input
fix, public Sessions, Steam ROM Manager support, and the completed release documentation.

Start with README.md for the main install, usage, troubleshooting, and Known Issues guide.

QUICK INSTALL
-------------
1. Keep the supplied Dolphin NetPlay Launcher folder together.
2. Recommended: place it inside your Dolphin folder.
3. Run DolphinNetPlayLauncher.exe.
4. Confirm or choose Dolphin.exe.
5. If Dolphin has not been set up yet, let the launcher open it once, finish Dolphin setup,
   add your game folders, then close Dolphin.

HOST / JOIN
-----------
HOST: Open Games, choose a game, select Host, then Host.

JOIN: A local selected game is NOT required. Use Sessions, a Traversal room code, or Direct IP.
The host determines the game used by the NetPlay session.

CONTROLLER INPUT
----------------
Options -> Controller -> Controller Input Polling offers:
  - 60 Hz (baseline)
  - 120 Hz
  - Match monitor (max 240 Hz)

Match monitor is the recommended default when it feels good on your system. Launcher polling is
focus-gated and does not increase controller polling during Dolphin gameplay.

STEAM ROM MANAGER
-----------------
Illustrated guide:
Documentation\Dolphin-NetPlay-Launcher-SRM-Setup-Guide-v6.pdf

Quick text reference:
SRM-INSTRUCTIONS.txt

KNOWN ISSUE
-----------
When launched through Steam, controller input can occasionally stop responding in Dolphin's
NetPlay lobby, most often while Joining. Use mouse/keyboard for the lobby if needed. Controller
input normally returns after Dolphin closes, sometimes after a short delay. Failed-Join result
dialogs can also temporarily miss controller input; the launcher has a narrowly scoped automatic
fallback for validated failed-Join result dialogs.

See README.md -> Known Issues for details.

Dolphin NetPlay Launcher does not provide Dolphin or game files.

DEFAULT PRESENTATION
--------------------
Fresh installs and Reset Options use Adventure Blue, Outfit, Animated Gradient accents,
an animated theme background, and Classic UI (Lokif CC0) interface sounds. Existing saved
preferences are preserved.

CREDITS / ATTRIBUTION
---------------------
Created and designed by Scott Drury.

Dolphin NetPlay Launcher was developed with OpenAI's ChatGPT generating the application's code
from the creator's requirements, feedback, testing, and design decisions.

The launcher uses SDL3 for controller input and includes the Outfit font and public interface
sound assets under their respective licenses. See THIRD-PARTY-NOTICES.txt and the bundled
license files. This project is unofficial and is not affiliated with or endorsed by Dolphin.
