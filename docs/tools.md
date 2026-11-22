# Tools

Source in `tools\<name>`. `build.ps1` builds them into `tools\bin\<name>\`. All run as a normal user. The
memory tools only touch the emulator process you name.

## dupshot: screen capture and smoothness

DXGI desktop duplication, so it also captures OpenGL / flip-model windows that GDI screenshots show as black.
The launcher uses it for session screenshots and frame-rate checks.

```
dupshot <out.bmp>                                   screenshot of the primary screen
dupshot fps <seconds>                               presented fps, 99th-percentile and longest frame gap
dupshot motion <seconds> <outDir|-> <x0> <y0> <x1> <y1>   per-frame change in a region: repeats vs new pictures
dupshot latency <vkHex> <x0> <y0> <x1> <y1> [n]     time from a key press until the region changes
```

## aimrig: make a gun report an exact position

Sends a Sinden gun the same 7-byte position message the Sinden software sends over its USB serial port. The
gun then reports that point to Windows as its own aim. No button, recoil, calibration or firmware messages are
ever sent. Close the Sinden software first (it holds the ports), or start the launcher with `--no-sinden`.

```
aimrig point <gun 1|2> <x 0..1> <y 0..1> [holdMs]   aim a gun at a screen fraction
aimrig watch [ms]                                   print both guns' raw mouse reports
aimrig probe <gun>                                  aim at three points while watching that gun
aimrig peek <process> <offset hex> <u8|i16|i32|f32> [count]
--p1 <id> --p2 <id>                                 USB id text of each gun (default Sinden 0F02 / 0F01)
```

Uses: check that a game's shots land where the gun points across the whole picture (centre and edges), run a
game's own gun calibration precisely, and test player 1 / player 2 separation (one gun aimed, the other elsewhere).
`mame_aim.lua` (MAME `-autoboot_script`) logs the lightgun values MAME receives, to compare with aimrig's points.

## mempatch: patch an emulator's code in memory

```
mempatch <process> <offset>=<expected hex>:<new hex> [...]
```

Waits for the process, then writes each patch only if the current bytes equal `<expected>`, so a different build
is never touched. The exe on disk is unchanged, which matters for hooks that checksum it. Run it as a
`helpers` entry with `delayMs: 0`. Typical use: turn a branch so the game always takes the input path a gun hook
redirects.

## rpcs3hold: keep values in PS3 memory

```
rpcs3hold <guardAddr>=<guardValue> <addr>=<value> [<addr>=<value> ...]
```

All hex. Addresses are guest addresses and values are big-endian 32-bit. While the guard word matches (for example
the loaded program's ELF entry point), every target is re-written whenever it changes, so it never touches
another program on the same disc. Typical use: infinite credits where the game has no free-play option. Run it
as a `helpers` entry. It waits for RPCS3 and exits with it.

## ps2mem / ps3mem: find values in emulated memory

```
ps2mem scan <u8|u16|u32> <value>     ps3mem scan <u8|u16|u32> <value>
ps2mem next <value>                  ps3mem next <value>
ps2mem list                          ps3mem list
ps2mem read <addr> [count]           ps3mem read <addr> [count]
ps2mem write <u8|u16|u32> <addr> <v> ps3mem write <u8|u16|u32> <addr> <v>
```

Classic scan / narrow down: scan for the credit count, change it in the game, `next <new value>`, repeat. Then
test with `write`. ps2mem reads PCSX2's exported `EEmem`; ps3mem reads RPCS3's guest mapping (values
big-endian). Turn the result into a PCSX2 `.pnach` cheat, an RPCS3 patch, or an `rpcs3hold` helper.

## mamectl: drive MAME service menus by script

```
powershell -File tools\mamectl\mamectl.ps1 -Action start -Game <set> [-MameDir <dir>]
powershell -File tools\mamectl\mamectl.ps1 -Action cmd -Commands 'press 10 Service Mode','wait 60','snap'
powershell -File tools\mamectl\mamectl.ps1 -Action stop
```

`ctl.lua` runs inside MAME. Commands: `list` (all input fields to `fields.txt`), `press <frames> <field>`,
`set <value> <field>` (e.g. DIP switches), `clear <field>`, `wait <frames>`, `snap`, `exit` (saves NVRAM).
Use it to set free play, difficulty and lives repeatably.

## sessions\lastsession.ps1: read session recordings

```
powershell -File tools\sessions\lastsession.ps1 [-count 5] [-all] [-sessions <dir>]
```

Summary, event log highlights, screenshot list and copied emulator logs of the newest sessions. Read it after
every play test before guessing what went wrong.
