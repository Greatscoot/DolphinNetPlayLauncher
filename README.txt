DOLPHIN NETPLAY LAUNCHER
========================
Current release: 0.12.0 — Friend Groups Update
Previous release: 0.11.0

Dolphin NetPlay Launcher is a Windows frontend for Dolphin NetPlay. It simplifies Host and Join
setup while leaving Dolphin itself in charge of emulation, networking, compatibility, and updates.

The GitHub README.md is intentionally kept as a concise introduction. This file is the fuller
reference guide.

Dolphin NetPlay Launcher does not provide Dolphin or game files.


WHY MAKE THIS?
--------------
I made Dolphin NetPlay Launcher because I wanted an easier way to play Dolphin NetPlay with friends who do
not use it often enough to remember all of the setup. Instead of walking everyone through the same Dolphin
menus and connection steps again each time we play, I wanted a simple way to handle the repetitive parts.

It also solves a specific problem when using Steam as a frontend for Dolphin games through Steam ROM Manager.
Launching an individual Dolphin game from Steam normally starts that game directly. To use NetPlay instead,
you would otherwise need to open Dolphin separately -- either by adding Dolphin itself to Steam or launching
Dolphin.exe directly -- then navigate through the NetPlay setup before hosting or joining.

Dolphin NetPlay Launcher bridges that gap. A game selected through Steam can be passed to the launcher first,
where you can choose Host or Join and let the launcher handle the NetPlay setup in Dolphin. Friend Groups make
repeat sessions even simpler by letting you find the friend who is hosting and join without exchanging room
codes or walking everyone through the process again.

The normal Host/Join flow can also be handled entirely with a controller, avoiding the need to switch back to
mouse and keyboard just to navigate Dolphin's NetPlay setup.

Dolphin NetPlay Launcher is not meant to replace Dolphin or its NetPlay implementation. The goal is to make
getting into Dolphin NetPlay more convenient.


HOW DOES IT WORK?
-----------------
Dolphin NetPlay Launcher does not implement NetPlay itself. Dolphin still handles the actual connection,
emulation, synchronization, game launch, and NetPlay session. The launcher automates the setup needed to
reach Dolphin's existing NetPlay lobby.

When you choose Host or Join, Dolphin NetPlay Launcher:
1. Identifies the selected game with DolphinTool.exe when a local game needs to be staged for hosting.
2. Writes the required NetPlay settings to Dolphin's configuration, including nickname, Traversal/Direct-IP
   details, and public-session settings. Hosting also stages the selected game for Dolphin's Host page.
3. Starts Dolphin normally.
4. Waits for Dolphin's main window, then opens Tools -> Start NetPlay.
5. Detects Dolphin's NetPlay Setup window and performs the appropriate Host or Connect action.
6. Waits for Dolphin's real NetPlay lobby. At that point Dolphin has taken over and setup automation is done.
7. Optionally provides the launcher's controller shortcuts for useful lobby actions and the normal return
   lifecycle afterward.

WHY UI AUTOMATION?
Dolphin exposes useful command-line and configuration functionality, but it does not currently expose a
supported command-line action for opening NetPlay directly into a Host or Join session. Dolphin NetPlay
Launcher therefore combines Dolphin's normal configuration files with narrowly scoped Windows UI automation
for the final setup steps. In practical terms, it performs the same Dolphin menu/button actions a person would
otherwise perform manually. Automation is limited to the Dolphin process launched for that session, validates
the expected Dolphin windows before acting, and stops driving setup when the actual NetPlay lobby appears.

It is a little unconventional, but without a NetPlay command-line/API entry point in Dolphin there is not
currently a cleaner supported route to the same seamless workflow. Regardless, it works.

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


FRIEND GROUPS / .DNLGROUP QUICK START
--------------------------------------
Friend Groups are designed for repeat NetPlay with the same people. They let Dolphin NetPlay Launcher
recognize configured friends in Dolphin's public NetPlay lobby and prepare the correct Join target without
passing a new room code around every time.

A Friend Group stores:
- Group name
- Region
- Shared NetPlay password
- Your session name for that group
- The other members' session names
- Per-PC LAN overrides for that active group (local only; not shared)

You can save multiple Friend Groups and switch the active group from the dropdown in either Host or
Join -> Friends. Both dropdowns select the same single active group. When Join -> Friends is active, or Host
is using the active Friend Group, Nickname automatically changes to your identity in that group. Switching
groups updates Nickname to that group's identity. The field remains editable; the automatic match is only a
convenience.

SHARING A GROUP:
1. Open Options -> Friends and create/select the group.
2. Add every member, including yourself as the local identity.
3. Choose Export active group... to create a .dnlgroup file.
4. Send that file only to the intended group.
5. Each person imports it (or drags it onto the main launcher), chooses which member they are, and the group
   is added to their local group dropdown. Nobody edits themselves in or out of the shared file. If the launcher
   is running as Administrator, Windows may block drag/drop from normal File Explorer; use Import .dnlgroup...
   instead.
6. When a member hosts, choose Join -> Friends, select the correct group, highlight that person, load them,
   then use Join.

IMPORTANT SECURITY NOTE:
A .dnlgroup file intentionally contains the shared NetPlay password so it can be used on another computer.
It may also contain the small custom badge images assigned to group members, including your own identity.
Set your own badge before export if you want the shared file to arrive with a complete set of profile pictures.
It is a portable group profile, not encrypted secret storage. Do not post it publicly, and only include images
you are comfortable sharing.

SAME-NETWORK / LAN FRIENDS:
If someone in the group is on the same home network and needs Direct IP, configure that Friend under
Options -> Friends -> Same-network / LAN connection on the joining PC. Discovery still happens through the
Friend Group. When that Friend is loaded, Route: LAN is the default. If that same person is currently hosting
from another network, toggle the main Friends route button to Route: Internet for that join instead of deleting
the saved LAN mapping. LAN mappings are not included in .dnlgroup files. A router port-forward can make
Traversal happen to work for a same-network host in some environments; current testing showed that behavior
following whichever household PC received the forward. Do not treat that as a replacement for the explicit
per-PC LAN Direct-IP override.

Full guides:
Documentation\Dolphin-NetPlay-Launcher-Friend-Groups-Guide-v1.pdf  (visual walkthrough with screenshots)
Documentation\FRIEND-GROUPS-AND-DNLGROUP.md  (full text reference)


NETPLAY BANNERS
---------------
Options -> Appearance -> NetPlay Browser offers:
- Banners — the default presentation; adds cached Dolphin-style game banners to Public Sessions and Join -> Friends when local artwork is available.
- Plain text — keeps the compact text-only presentation if you prefer it.

In banner mode:
- Public Sessions uses a taller four-line banner row so session/game/ID-revision/region/player/state information is easier to read.
- Friends uses 36 px rows with the Friend badge, two-line Friend/game/state text, and a compact banner.
- Game ID and Revision are kept as text when Dolphin NetPlay Launcher can parse them from the advertised name.
- Missing art falls back to text immediately.

Banner source priority:
1. For normal Dolphin NetPlay names, the advertised Game ID is matched directly against Dolphin's existing gamelist.cache (the Games panel does not need to be opened first).
2. Launcher title/path metadata is a fallback for nonstandard names.
3. A manual PNG under SessionBanners, named GAMEID.png or by matching advertised game title.
4. No banner / normal text fallback.

Use Options -> Appearance -> NetPlay Browser -> Open Banner Folder to create/open the override folder.
Automatic downloading is intentionally not enabled yet; a future downloader needs a reliable banner source and
clear naming/redistribution behavior first.

Full guide:
Documentation\SESSION-BANNERS.md


RESPONSIVE MAIN WINDOW
----------------------
The main launcher keeps the accepted shallow transparent header and opaque/card-heavy content architecture.
Vertical resizing is mode-aware: Friends spends extra height on the roster; Host and Traversal/Direct re-center
their fixed active controls between the upper selectors and Dolphin utilities instead of splitting into one large
empty central gap. This is layout/geometry only; the rejected transparency/compositing experiments remain retired.


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
3. For Friend Group hosting, choose the group from the Host Friend Group dropdown. That becomes the same
   active group used by Join -> Friends; its saved session name, shared password, and region are applied to
   the existing public-hosting workflow.
4. Or configure ordinary Show in Server Browser details manually.
5. Choose Host.

Hosting uses Dolphin's Traversal Server. Dolphin NetPlay Launcher opens Dolphin's NetPlay
interface, selects the game, and creates the lobby.


JOIN
----
You do NOT need to select a local game before joining. The host determines the game used by
the session.

You can join with:
- Friends — select the active Friend Group, highlight a hosting friend, load them, then use Join.
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
- Friend Groups, .dnlgroup import/export, custom badges, and per-group LAN routing
- Dolphin selection
- Automatic close behavior
- Close grace periods
- Appearance/themes
- Interface sounds (mouse and controller actions share semantic cues; bundled Switch/selection feedback uses the softer cue, Refresh uses Navigate; Host PUBLIC HOSTING checkboxes also use Navigate; Options OK uses Use Game and Options Cancel uses Error)
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
- Automatic Dolphin close after NetPlay, with a 1.0 second NetPlay-close grace period
- A 1.0 second updater-close grace period
- Automatic return to Dolphin NetPlay Launcher after a failed Join
- Return to Dolphin NetPlay Launcher when Dolphin closes

Existing saved preferences are preserved. Changing a default does not overwrite an already-saved explicit preference.


TROUBLESHOOTING
---------------
MY CONTROLLER ISN'T WORKING IN-GAME!
Dolphin can see controllers differently depending on how it was launched. In particular, a controller exposed
to Dolphin when it is started through Steam may differ from the controller Dolphin sees when it is launched
directly. This comes from Dolphin/Steam's controller handling, not Dolphin NetPlay Launcher.

If your controls work when launching Dolphin one way but not another, open Dolphin using the same method you
normally use for your games and configure the controller there. If you normally launch your Dolphin games
through Steam, configure Dolphin's controls while using that same Steam launch path.

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
