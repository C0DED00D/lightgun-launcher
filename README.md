# Lightgun Launcher

A full-screen game menu for two-player light gun setups on Windows (built and tested with Sinden light guns on a TV).
You shoot a tile to start a game, play with both guns, and hold one button to return to the menu. No keyboard or
mouse needed.

It is not an emulator and ships no games or emulators. You point it at the emulators and games you own, and it
handles what lightgun setups keep running into:

- **Two guns, two players.** Each gun's raw-input device is found at every launch (paths change when guns are
  replugged) and written into each emulator's config through templates, so gun 1 is always player 1.
- **The white border.** The launcher draws the Sinden border over every game. It can draw it around a centred
  4:3 picture instead of the screen edges, so guns mapped over the whole 16:9 screen still reach the picture's
  edges. It also keeps games from covering the border (the 1-px "keep composited" trick, borderless sizing, z-order).
- **Hold to quit.** Holding the quit button (Sinden D-pad down = F11) for 3 s closes the game cleanly, so it can
  save NVRAM and calibration. A banner shows the progress. The key can also be passed to the game as another key
  (for example Down, for menus).
- **Window wrangling.** Borderless full screen or 4:3 on a black backdrop. Front-end games (TeknoParrot) are
  followed through their child process and pulled back in front of loader consoles. Resizing waits for games that
  set their resolution from the window. The render child window is fitted for gun hooks that map over it.
- **Input workarounds.** Relay gun keys as software key presses, send software copies of keys, turn each gun's
  trigger into a key, tap keys after launch (for boot-time calibration screens), and warn when a gun sends the
  other player's keys.
- **Sinden control.** Starts and stops its own Sinden copy cleanly (force-killing Sinden can drop the guns off
  USB), pins which gun is player 1, has a recoil on/off tile, and a centre target for Sinden's calibration.
- **Session recordings.** Every game gets a folder with an event log (gun buttons with aim position, aim
  drop-outs, focus changes, how it ended), periodic and on-shot screenshots, frame-rate and motion checks, and
  copies of the emulator logs. Problems from a play test can be checked afterwards.
- **Helper tools** for the hacks lightgun games often need: in-memory code patches, holding values in PS3
  memory (for example infinite credits), PS2/PS3 memory search, scripted MAME service menus, an aim-test rig that
  makes a Sinden gun report an exact position, and a DXGI screen capture and frame-rate meter.

## Quick start

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run `powershell -File build.ps1`.
   This builds `app\LightgunLauncher.exe` and the tools in `tools\bin\`.
2. Put your emulators in `emulators\` and your games in `roms\` / `games\`, or anywhere else: every path is
   set in the config.
3. Copy `config\games.example.json` to `config\games.json` and add your games. Each emulator type has an
   example entry, and [docs/configuration.md](docs/configuration.md) lists every option.
4. Optional: add menu art as `config\art\<game id>.png`.
5. Start `app\LightgunLauncher.exe`.

`config\games.json`, `config\settings.json`, your art, `emulators\`, `roms\` and `games\` are in `.gitignore`.
Your own setup never ends up in the repository.

## Menu controls

| Input | Does |
|---|---|
| Shoot a tile | Start the game (a greyed tile shows which file is missing) |
| Hold the quit key (default F11 = Sinden D-pad down) for 3 s in a game | Back to the menu |
| CALIBRATE tile | Centre target and live per-gun offset, for Sinden's own calibration |
| GUN RECOIL tile | Toggle recoil (rewrites the launcher's Sinden settings and restarts Sinden) |
| EXIT tile (shoot twice) or Esc | Close the launcher and its Sinden copy |

Red reticle = gun 1, blue = gun 2, white = the mouse.

## Command line

| Option | Does |
|---|---|
| `--run <id>` | Start a game straight away |
| `--menu-run <id>[,<id>...]` | Show the menu for 10 s, then start the game through the tile path. The next id starts 10 s after the previous game ends |
| `--no-sinden` | Don't start Sinden (for aim tests that drive the guns' serial ports) |

## Docs

- [docs/configuration.md](docs/configuration.md): every `games.json` option and template placeholder
- [docs/emulators.md](docs/emulators.md): per-emulator notes (what works, what needs a patched build)
- [docs/tools.md](docs/tools.md): helper tools and how to use them for common hacks

## Layout

```
launcher\        the launcher (C#, WinForms, .NET 10)
config\          games.example.json, templates\ (emulator configs with placeholders), art\
tools\           helper tools (source); built into tools\bin\
docs\            documentation
app\             build output + launcher.log + sessions\ (not in git)
```

## License

MIT, see [LICENSE](LICENSE). Emulators, gun software and games keep their own licenses and are not included.
