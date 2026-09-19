# Dolphin NetPlay Launcher

> **Current release:** 0.11.0 — released September 18, 2026.

Dolphin NetPlay Launcher is a Windows frontend for **Dolphin NetPlay**. It simplifies Host and Join setup while leaving Dolphin itself in charge of emulation, networking, compatibility, and updates.

> **Unofficial project:** not affiliated with or endorsed by the Dolphin Emulator project. Dolphin NetPlay Launcher does not include Dolphin or game files.

> **AI-assisted development:** Dolphin NetPlay Launcher was developed with OpenAI's ChatGPT generating the application's code from my requirements, feedback, testing, and design decisions.

## What it does

- Makes **Host** and **Join** setup controller-friendly.
- Supports Dolphin **Traversal room codes** and **Direct IP** connections.
- Includes a searchable **Games** library and **Public Sessions** browser.
- Works standalone or through **Steam ROM Manager**.
- Uses Dolphin's own NetPlay and updater rather than replacing them.

## Why make this?

I made Dolphin NetPlay Launcher because I wanted an easier way to play Dolphin NetPlay with friends who do not use it often enough to remember all of the setup. Instead of walking everyone through the same Dolphin menus and connection steps again each time we play, I wanted a controller-friendly front end that could handle the repetitive parts for us.

It is not meant to replace Dolphin or its NetPlay implementation. The goal is simply to make getting everyone into a Dolphin NetPlay session feel more like starting a normal multiplayer game.

## Showcase

### Host a game from Steam

Launch a game from your Steam library, choose **Host**, and Dolphin NetPlay Launcher handles opening Dolphin and setting up the NetPlay lobby.

<p align="center">
  <a href="Documentation/Media/dolphin-netplay-steam-host-demo.mp4">
    <img src="Documentation/Media/dolphin-netplay-steam-host-demo.gif"
         alt="Hosting a Dolphin NetPlay session from Steam"
         width="850">
  </a>
</p>

### Browse your game library

<p align="center">
  <a href="Documentation/Media/dolphin-netplay-library-grid.mp4">
    <img src="Documentation/Media/dolphin-netplay-library-grid.gif"
         alt="Browsing the game library in grid view"
         width="850">
  </a>
</p>

### Switch to list view

<p align="center">
  <a href="Documentation/Media/dolphin-netplay-library-list.mp4">
    <img src="Documentation/Media/dolphin-netplay-library-list.gif"
         alt="Browsing the game library in list view"
         width="850">
  </a>
</p>

### Browse public NetPlay sessions

<p align="center">
  <a href="Documentation/Media/dolphin-netplay-sessions-browser.mp4">
    <img src="Documentation/Media/dolphin-netplay-sessions-browser.gif"
         alt="Browsing public Dolphin NetPlay sessions"
         width="850">
  </a>
</p>

## Quick Install

You need Windows, a current Dolphin installation containing `Dolphin.exe` and `DolphinTool.exe`, and your own game files for games you want to host.

1. Extract the entire Dolphin NetPlay Launcher folder. Do not move only the EXE.
2. Recommended: place the launcher folder inside your Dolphin folder:

```text
Dolphin\
├─ Dolphin.exe
├─ DolphinTool.exe
└─ DolphinNetPlayLauncher\
   └─ DolphinNetPlayLauncher.exe
```

3. Run `DolphinNetPlayLauncher.exe`.
4. Confirm the Dolphin installation it finds, or choose another `Dolphin.exe`.
5. If Dolphin has never been run, let the launcher open it once. Finish Dolphin's first-run setup, add your game folders, then close Dolphin.

The launcher reads the game folders already configured in Dolphin.

## During automatic Dolphin setup

While **Setting up Dolphin NetPlay...** is visible, avoid clicking, typing, moving the mouse, or using the controller. The launcher is briefly controlling Dolphin's NetPlay setup window. Dolphin takes over normally once the lobby opens.

## Controller Navigation

Controller navigation uses SDL3. On-screen prompts can use Xbox, PlayStation, or Switch-style labels.

Common controls:

- **D-pad / Left Stick** — navigate
- **South face button** — select
- **East face button** — back
- **LB / RB** — page through Games where applicable
- **Start / Options / +** — main Host/Join action
- **Select / Create / -** — Clear Game
- Configurable face-button shortcut — Games or Paste
- **R3** — Sessions

Controller options are under **Options → Controller**.

## Steam ROM Manager

Dolphin NetPlay Launcher supports **Steam ROM Manager** so you can create a separate Dolphin NetPlay collection in Steam and launch selected games directly into the launcher.

For setup, use the included illustrated guide:

**`Documentation/Dolphin-NetPlay-Launcher-SRM-Setup-Guide-v6.pdf`**

A shorter text reference is also included as **`SRM-INSTRUCTIONS.txt`**.

## Troubleshooting

- **Dolphin was not found:** choose another `Dolphin.exe`, or use **Options → Change Dolphin**.
- **Dolphin configuration is missing:** let Dolphin run once, complete its setup, then close it.
- **Games is empty:** make sure your game folders already appear in Dolphin's own game list.
- **Wrong Dolphin library/settings:** verify which Dolphin user-data folder or portable installation you are using.
- **NetPlay automation cannot control Dolphin:** avoid running Dolphin elevated unless the launcher is elevated too.
- **Dolphin stays open after NetPlay:** check **Automatically close Dolphin when NetPlay lobby closes** and its grace-period setting.

## Known Issues

### Controller input can occasionally stop responding in Dolphin's NetPlay lobby when launched through Steam

This has been observed intermittently, most often while **Joining**. The NetPlay connection itself can continue to work normally even when lobby-controller input is unavailable.

If this happens, use the mouse or keyboard to interact with or close Dolphin's NetPlay lobby. Controller input normally becomes available to Dolphin NetPlay Launcher again after Dolphin closes. In some Steam/Steam Input return cases, recovery can take roughly **5–6 seconds**.

The launcher intentionally does not use aggressive controller reacquisition during gameplay because prior experiments could interfere with otherwise working Host/lobby controller behavior.

### Failed-Join result dialogs may also ignore controller input

When a Join attempt fails, Dolphin may show connection-result dialogs while controller input is temporarily unavailable. Dolphin NetPlay Launcher validates the exact Dolphin result dialog and, if no usable controller acknowledgement arrives for 4 seconds, automatically confirms that dialog so recovery can continue.

## Full documentation

For detailed Host/Join behavior, Games and Sessions usage, portable Dolphin, controller polling options, Dolphin updates, settings/diagnostics, NetPlay compatibility, build instructions, and attribution, see **`README.txt`** included with the project.
