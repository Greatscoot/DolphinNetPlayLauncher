Dolphin NetPlay Launcher UI sounds

Built-in sound themes
---------------------
Adventure
  Original Dolphin NetPlay Launcher fantasy-console-style interface sounds.

Royal
  Original Dolphin NetPlay Launcher plucked/harpsichord-style interface sounds.

ClassicUI
  Uses the user's approved subset of Lokif's "GUI Sound Effects" pack,
  published under CC0 / Public Domain.

Custom sound themes
-------------------
Create a folder under Sounds. The folder name becomes the Sound style name.
A valid theme contains all 11 semantic WAV files. The same semantic cue set is used for
equivalent mouse and controller actions; a custom theme therefore does not need separate
mouse/controller assets:

  navigate.wav
  switch.wav
  library_open.wav
  library_close.wav
  stage_game.wav
  use_game.wav
  launch.wav
  confirm.wav
  cancel.wav
  clear_game.wav
  error.wav

Use Options -> Appearance -> Validate Folder... for a detailed check.

Dolphin NetPlay Launcher accepts uncompressed PCM WAV files in 8-bit or 16-bit, mono or stereo,
from 8 kHz through 192 kHz and converts them in memory for the shared
low-latency overlapping audio pool.

Only distribute sounds you have the right to share.

Interaction semantics
---------------------
The launcher maps the same semantic cue to equivalent mouse and controller actions.
Checkbox/radio toggle changes use switch.wav, neutral Refresh actions use navigate.wav,
Cancel/Close actions generally use cancel.wav, and ordinary affirmative/commit actions use confirm.wav.
For the three bundled themes, routine Switch/selection actions use the softer bundled cue while true Error actions use the sharper error cue. Custom themes keep the normal semantic meaning: switch.wav is Switch and error.wav is Error.
Options is intentionally quieter: ordinary Options buttons and non-terminal controller selections use
navigate.wav, while checkboxes/radios use switch.wav. Options OK uses use_game.wav and Options Cancel uses
error.wav. The sound-theme Test action
keeps its own self-owned sample. Options and Friend Group openers use navigate.wav (the same neutral cue as
Refresh) and emit it before the synchronous modal begins, so it must not replay after that modal closes.
The loaded-Friend LAN/Internet route toggle also uses navigate.wav. Double-clicking a Library List/Grid game
and committing it uses use_game.wav, matching the explicit Use Game action.

Main Mode (Host/Join) and Connection (Traversal/Friends/Direct IP) selectors use the Switch semantic, so bundled themes now give them the softer old-Error waveform. The Host -> PUBLIC HOSTING checkboxes are explicit exceptions and use navigate.wav.
