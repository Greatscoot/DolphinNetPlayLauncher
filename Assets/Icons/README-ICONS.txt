DOLPHIN NETPLAY LAUNCHER - ICON ASSETS
======================================

This folder contains the source application icon and the runtime theme-logo PNGs.

Build-time application icon:
- DolphinNetPlayLauncher.ico
  Embedded into DolphinNetPlayLauncher.exe by Build.bat.

Runtime theme/logo assets:
- DolphinNetPlayLauncher-icon.png              Generic fallback
- DolphinNetPlayLauncher-icon-adventure.png    Adventure Blue
- DolphinNetPlayLauncher-icon-dark.png         Dark
- DolphinNetPlayLauncher-icon-indigo.png       GameCube Indigo
- DolphinNetPlayLauncher-icon-light.png        Light / non-dark default
- DolphinNetPlayLauncher-icon-oled.png         OLED Black
- DolphinNetPlayLauncher-icon-spice.png        GameCube Spice Orange

The runtime PNG filenames and theme mapping are unchanged from the older layout;
the assets now live under Assets\Icons so the repository root stays cleaner.

Clean Windows release packages include the seven runtime PNGs in Assets\Icons.
The .ico is embedded into the EXE and does not need to be shipped as a loose
runtime file.
