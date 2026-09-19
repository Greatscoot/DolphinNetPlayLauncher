DOLPHIN NETPLAY LAUNCHER
========================
Release: 0.11.0

Dolphin NetPlay Launcher is a Windows frontend for Dolphin NetPlay. It simplifies Host and Join
setup while leaving Dolphin itself in charge of emulation, networking, compatibility, and updates.

The GitHub README.md is intentionally kept as a concise introduction. This file is the fuller
reference guide.

Dolphin NetPlay Launcher does not provide Dolphin or game files.


QUICK INSTALL
-------------
Requirements:
- Windows
- A current Dolphin installation containing Dolphin.exe and DolphinTool.exe
- Your own compatible GameCube/Wii game files for games you want to host

1. Keep the supplied Dolphin NetPlay Launcher folder together.
2. Recommended: place it inside your Dolphin folder.
3. Run DolphinNetPlayLauncher.exe.
4. Confirm or choose Dolphin.exe.
5. If Dolphin has not been set up yet, let the launcher open it once, finish Dolphin setup,
   add your game folders, then close Dolphin.

The launcher reads the game folders already configured in Dolphin.


FRESH-START FIELD DEFAULTS
--------------------------
On a fresh launcher configuration:
- Nickname starts as: Player
- Traversal room code starts blank
- Direct-IP address starts blank
- Direct port defaults to 2626
- Join mode defaults to Traversal
- Show in Server Browser defaults off
- Public session name starts blank
- Public password starts blank
- Region defaults to North America (NA)

Dolphin NetPlay Launcher does not import old nickname, Direct-IP, room-code, session-name, or
session-password values from an existing Dolphin profile. Values entered in the launcher can be
remembered by the launcher for later use.


PORTABLE DOLPHIN
----------------
If you intentionally use Dolphin in portable mode, portable.txt belongs beside Dolphin.exe.
Otherwise Dolphin may use its normal Windows user-data folder.


HOST
----
1. Open Games and choose the game you want to host.
2. Select Host and enter your nickname.
3. Optional: enable Show in Server Browser and enter the public-session details you want.
4. Choose Host.

Hosting uses Dolphin's Traversal Server. Dolphin NetPlay Launcher opens Dolphin's NetPlay
interface, selects the game, and creates the lobby.


JOIN
----
You do NOT need to select a local game before joining. The host determines the game used by
the session.

You can join with:
- Sessions — choose a compatible public session, then use it for Join.
- Traversal — enter or paste the host's room code.
- Direct IP — enter the host's IP address and port.

Then choose Join. Dolphin NetPlay Launcher opens Dolphin's NetPlay interface and connects.


DURING AUTOMATIC DOLPHIN SETUP
------------------------------
While "Setting up Dolphin NetPlay..." is visible, avoid clicking, typing, moving the mouse,
or using the controller. The launcher is briefly controlling Dolphin's NetPlay setup window.
Dolphin takes over normally once the lobby opens.


GAMES LIBRARY
-------------
Games reads the GameCube/Wii folders configured in Dolphin and supports:
- Search
- Grid and list views
- Cover artwork with Dolphin banner fallback
- Basic game metadata
- Controller navigation and paging

If Games is empty, open Dolphin, make sure the game folders appear in Dolphin's own game list,
close Dolphin, and try again.

The first Games open in a launcher session may take longer while the library is loaded. Later
openings are much faster.


PUBLIC SESSIONS / PUBLIC HOSTING
--------------------------------
Sessions shows compatible public Dolphin NetPlay sessions. By default, the launcher filters for
sessions using the same Dolphin version.

Selecting a session prepares its connection information for Join. It does not launch a local game.

When hosting, Show in Server Browser uses Dolphin's public-index settings. You can set a session
name, region, and optional password.


CONTROLLER NAVIGATION
---------------------
Controller navigation uses SDL3. On-screen prompts can use Xbox, PlayStation, or Switch-style
labels.

Common controls:
- D-pad / Left Stick — navigate
- South face button — select
- East face button — back
- LB / RB — page through Games where applicable
- Start / Options / + — main Host/Join action
- Select / Create / - — Clear Game
- Configurable face-button shortcut — Games or Paste
- R3 — Sessions

Controller options are under Options -> Controller.

When Dolphin's tracked NetPlay lobby is in the foreground, the launcher can provide limited
controller shortcuts for lobby controls. It does not send launcher navigation input into gameplay
or unrelated applications.


CONTROLLER INPUT POLLING
------------------------
Options -> Controller -> Controller Input Polling offers:
- 60 Hz (baseline)
- 120 Hz
- Match monitor (max 240 Hz)

Match monitor is the recommended default when it feels good on your system. This setting affects
launcher input detection only while Dolphin NetPlay Launcher has focus. It does not increase
launcher controller polling during Dolphin gameplay, and it does not change the animated-background
refresh rate.


STEAM ROM MANAGER
-----------------
Steam ROM Manager can create a separate Dolphin Netplay Steam collection whose entries pass a
selected ROM to Dolphin NetPlay Launcher.

Illustrated guide:
Documentation\Dolphin-NetPlay-Launcher-SRM-Setup-Guide-v6.pdf

Quick text reference:
SRM-INSTRUCTIONS.txt

Quick setup:
1. Clone your working Dolphin parser.
2. Point it at the parent folder containing your GameCube/Wii folders.
3. Set the executable to DolphinNetPlayLauncher.exe.
4. Set arguments to exactly "${filePath}" — do NOT use Dolphin's normal -e argument.
5. Use a separate Steam collection such as Dolphin Netplay.
6. Append " Netplay" to ${fuzzyTitle} if you want the shortcuts to be easy to distinguish.
7. Parse, exclude games you do not want in the NetPlay collection, and save the app list to Steam.


DOLPHIN UPDATES
---------------
Check for Dolphin Update invokes Dolphin's own updater. Dolphin NetPlay Launcher does not download
replacement Dolphin builds itself.

The launcher waits for Dolphin's update flow and requests normal, graceful closes when appropriate.
It does not force-kill Dolphin.


SETTINGS AND DIAGNOSTICS
------------------------
Options include:
- Dolphin selection
- Automatic close behavior
- Close grace periods
- Appearance/themes
- Interface sounds
- Controller settings
- Games preferences
- Diagnostics

Settings are stored in config.ini beside the launcher.

For diagnostic information, use:
Options -> Diagnostics -> Copy Diagnostic Info

The launcher also keeps DolphinNetPlayLauncher-Diagnostics.log beside the launcher.

Traversal room codes and Direct-IP targets are not written to the diagnostic log, and Windows
profile paths are redacted where appropriate.


DEFAULT PRESENTATION
--------------------
Fresh installs and Reset Options use:
- Adventure Blue
- Outfit interface font
- Animated Gradient accents
- Animated theme background
- Classic UI (Lokif CC0) interface sounds

Existing saved preferences are preserved.


TROUBLESHOOTING
---------------
Dolphin was not found:
Choose another Dolphin.exe, or use Options -> Change Dolphin. The selected installation must
contain both Dolphin.exe and DolphinTool.exe.

Dolphin configuration is missing:
Let the launcher open Dolphin once, complete Dolphin's setup, then close Dolphin. Dolphin creates
its own configuration; the launcher does not fabricate one.

Games is empty:
Make sure your game folders appear in Dolphin's own game list. Dolphin NetPlay Launcher reads
those same configured folders.

The wrong Dolphin library/settings appeared:
Check which Dolphin user-data folder that installation is using. If you intentionally use portable
Dolphin, place portable.txt beside Dolphin.exe.

NetPlay automation cannot control Dolphin:
Windows can block a normal application from automating an elevated application. If Dolphin is
running as administrator while the launcher is not, the launcher should detect the mismatch and
offer to restart itself elevated.

Dolphin stays open after NetPlay:
Check Automatically close Dolphin when NetPlay lobby closes and its grace-period setting.
The launcher requests a normal close rather than killing Dolphin.


KNOWN ISSUES
------------
1. CONTROLLER INPUT CAN OCCASIONALLY STOP RESPONDING IN DOLPHIN'S NETPLAY LOBBY WHEN LAUNCHED
   THROUGH STEAM

This has been observed intermittently, most often while Joining. The NetPlay connection itself can
continue to work normally even when lobby-controller input is unavailable.

If this happens, use the mouse or keyboard to interact with or close Dolphin's NetPlay lobby.
Controller input normally becomes available to Dolphin NetPlay Launcher again after Dolphin closes.
In some Steam/Steam Input return cases, recovery can take roughly 5–6 seconds.

The launcher intentionally does not use aggressive controller reacquisition during gameplay because
prior experiments could interfere with otherwise working Host/lobby controller behavior.

2. FAILED-JOIN RESULT DIALOGS MAY ALSO IGNORE CONTROLLER INPUT

When a Join attempt fails, Dolphin may show connection-result dialogs while controller input is
temporarily unavailable. Dolphin NetPlay Launcher validates the exact Dolphin result dialog and,
if no usable controller acknowledgement arrives for 4 seconds, automatically confirms that dialog
so recovery can continue. This fallback is limited to validated failed-Join result dialogs.


NETPLAY COMPATIBILITY
---------------------
Dolphin's normal NetPlay compatibility rules still apply. Players should use compatible Dolphin
builds and matching game data as required by Dolphin.


BUILD FROM SOURCE
-----------------
The source repository includes DolphinNetPlayLauncher.cs and Build.bat.

On Windows, run Build.bat. It builds the launcher with the installed .NET Framework C# compiler and
obtains the required SDL3 runtime when needed.

Package-Release.bat creates the clean end-user ZIP after a successful build.


CREDITS / ATTRIBUTION
---------------------
Created and designed by Scott Drury.

Dolphin NetPlay Launcher was developed with OpenAI's ChatGPT generating the application's code
from the creator's requirements, feedback, testing, and design decisions.

The launcher uses SDL3 for controller input and includes the Outfit font and public interface
sound assets under their respective licenses. See THIRD-PARTY-NOTICES.txt and the bundled license
files.

This project is unofficial and is not affiliated with or endorsed by the Dolphin Emulator project.
