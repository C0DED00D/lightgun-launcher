# Configuration (`config\games.json`)

Start from `config\games.example.json`. The file is JSON with `//` comments and trailing commas allowed. Names are
case-insensitive. Paths are relative to the launcher root (the folder that holds `config\`) unless they are absolute.

## Top level

| Option | Default | Meaning |
|---|---|---|
| `p1Gun`, `p2Gun` | `VID_16C0&PID_0F02`, `VID_16C0&PID_0F01` | Text found in each gun's raw-input device path. Gun 1 is player 1 |
| `p1Name`, `p2Name` | `Gun 1`, `Gun 2` | Names used in the menu and messages |
| `sinden` | none | Process to start with the launcher (`exe`, `args`, `delayMs`). It is closed cleanly on exit (WM_CLOSE to its hidden window; kill only as a last resort). An already-running copy is reused. A copy started from a different path is never closed or restarted |
| `sindenConfig` | none | Sinden settings file (`Lightgun.exe.Config`) the launcher may edit. Needed for the recoil tile and `sindenSettings` |
| `sindenSettings` | `{}` | `appSettings` key/value pairs written into `sindenConfig` before Sinden starts (pin slot A/B, button keys) |
| `playerKeys` | `{}` | Key name → player (1/2). A gun that sends the other player's key is logged and shown as a warning |
| `quitKey` | `F11` | Key the guns send for "hold to quit". Swallowed by the launcher; games never see it |
| `quitHoldMs` | `3000` | How long to hold it |
| `quitProgressFile` | `%TEMP%\lightgun_quit.txt` | While quitting, the hold progress (0..1) is written here so an in-game mod can draw its own banner. Empty = off |
| `borderPercent` | `2.0` | White border thickness, % of screen height |
| `screenCaptureTool` | `tools\bin\dupshot\dupshot.exe` | Used for session screenshots and frame-rate checks |
| `menu` | | See below |
| `recording` | | See below |
| `games` | `[]` | The games, in menu order |

### `menu`

| Option | Default | Meaning |
|---|---|---|
| `title`, `subtitle` | | Header text |
| `columns` | `5` | Tiles per row |
| `recoilTile` | `true` | Show GUN RECOIL (only when `sindenConfig` is set). The choice is kept in `config\settings.json` |
| `calibrateTile` | `true` | Show CALIBRATE |
| `calibrationHelp` | Sinden steps | Lines shown on the calibration page |

### `recording`

Each game gets `app\sessions\<time>_<id>\` with `session.log`, `summary.txt`, screenshots and emulator logs.
Read them with `tools\sessions\lastsession.ps1`.

| Option | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Off = no recordings |
| `screenshotEverySeconds` | `5` | Periodic screenshots (0 = none) |
| `screenshotOnButton` | `true` | Screenshot just after each gun button/key press |
| `smoothnessCheckEverySeconds` | `30` | Measure presented fps and new pictures per second for a few seconds (0 = never) |
| `copyEmulatorLogs` | `true` | Copy `emulatorLogs` and the game's `logs` when the game ends (only files written during the game) |
| `keepSessions` | `25` | Older session folders are removed |
| `emulatorLogs` | `[]` | Log files copied for every game |

Set the environment variable `LIGHTGUN_NO_SESSION_FPS=1` while a script measures the screen itself. Two screen
captures at once starve each other.

## Games

### Basics

| Option | Meaning |
|---|---|
| `id` | Unique id. Used for `--run`, art (`config\art\<id>.png`) and session folder names |
| `title`, `year`, `players`, `platform`, `system`, `emulator`, `menuEmulator`, `note` | Tile text. `menuEmulator` defaults to `emulator` up to any ` + ` |
| `run` | The emulator: `exe`, `args`, `cwd`, `env` (extra environment variables), `hidden`, `elevate` |
| `helpers` | Programs started before the emulator (gun hooks, memory patchers). Same fields as `run`, plus `delayMs` = wait after starting. Killed when the game ends |
| `templates` | `{ "from", "to", "binary" }`: files written before each launch with placeholders filled in. `binary: true` copies the file as-is (e.g. saved settings / NVRAM per game). `from` and `to` may contain placeholders |
| `requires` | Files or folders that must exist; `*` wildcards allowed in the file name. Missing = greyed tile |
| `verify`, `verifyMessage` | Optional check command (e.g. `mame -verifyroms <set>`) run in the background; non-zero exit greys the tile |
| `kill` | Extra process names to kill when the game ends |
| `closeWaitMs` | How long the emulator gets to close itself (and save) before it is killed. Default 4000 |
| `deleteBeforeLaunch` | Files deleted before each launch (e.g. a front-end's cached bindings that override yours) |
| `logs` | Extra log files for this game's session recording |

### Window and border

| Option | Meaning |
|---|---|
| `borderless` | Strip the window frame and fit it to the screen (kept that way while the game runs; gives up if the game keeps snapping back) |
| `aspect` | With `borderless`: `"4:3"` = full-height centred window on a black backdrop |
| `border` | `"overlay"` (default; launcher draws it), `"emulator"` (the emulator or game draws it itself), `"none"` |
| `borderArea` | `"4:3"` = draw the overlay border around the centred 4:3 picture. Sinden reports aim relative to the border, so a gun hook that maps over the whole screen then reaches the picture's edges, and the black side bars count as off-screen |
| `keepComposited` | Keep a 1-px window border (and with `borderless`, stop 1 px short of the bottom). A frameless window covering the whole screen goes straight to the display and hides the launcher's border and banner |
| `borderInset` | With `borderless`: shrink the window by the border thickness, so the border sits beside the picture (for games on their own display layer) |
| `fitChildWindow` | With `borderless`: remove the menu bar and stretch the largest child window (render surface) over the client area |
| `borderlessDelayMs` | With `borderless`: wait after the window appears before resizing (for games that size their 3D picture from the window at start-up) |
| `windowProcess` | Process whose window is the game, when `run` is a front-end that starts it. The launcher also waits for it if the front-end exits first |
| `keepForeground` | With `borderless`: whenever another program takes the foreground for a second, bring the game back. Also restores the game if it minimizes itself |
| `refreshWindowOnStart` | With `borderless`: minimize + restore once, 2 s after the window appears (for games that come up see-through) |

### Input

| Option | Meaning |
|---|---|
| `postLaunchKeys` | `[{ "delayMs", "key", "center" }]`: keys tapped into the game after launch, each after its delay. `center: true` puts the pointer at the screen centre first (a gun that hasn't reported yet follows the system pointer) |
| `quitKeyAlso` | The quit button also presses this key in the game (e.g. `"Down"` for menus); holding still quits |
| `triggerKeys` | `{ "1": "F7", "2": "F6" }`: each gun's trigger also presses/releases this key |
| `relayKeys` | Gun keys swallowed and re-sent as software key presses (for front-ends that bind software keys) |
| `copyKeys` | Like `relayKeys`, but the real key also reaches the game |

Key names are .NET `Keys` names: `D1`…`D9` for the number row, `F1`…`F24`, `Space`, `Return`, `Escape`, `Up`,
`Down`, `Left`, `Right`, `NumPad0`…, letters `A`…`Z`.

## Placeholders

Filled into template files (contents and `from`/`to` paths), `run`/`helpers` `exe` and `args`, and
`deleteBeforeLaunch`. In `.xml` templates the values are XML-escaped.

| Placeholder | Value |
|---|---|
| `{ROOT}` | Launcher root folder |
| `{P1GUN}`, `{P2GUN}` | `p1Gun` / `p2Gun` |
| `{P1MOUSE_PATH}`, `{P2MOUSE_PATH}` | Full raw-input device path of each gun's mouse |
| `{P1KBD_PATH}`, `{P2KBD_PATH}` | Same for each gun's keyboard (button keys) |
| `{P1MOUSE_HWID}` … `{P2KBD_HWID}` | Hardware id part of the path, e.g. `VID_16C0&PID_0F02&MI_02&Col02` (Flycast mapping file names) |
| `{P1MOUSE_ID}` … `{P2KBD_ID}` | Flycast device ids (path without `\\?\HID#`, `= [ ]` → `_`) |
| `{FLYCAST_OTHER_DEVICES}` | `maple_raw_… = -1` lines switching off every other mouse and keyboard in Flycast |
| `{P1MOUSE}`, `{P2MOUSE}` | 1-based mouse numbers as Supermodel counts them (reverse device order, remote-desktop mice skipped) |
| `{SCREEN_W}`, `{SCREEN_H}`, `{SCREEN_H_1}` | Primary screen size; `_1` = height minus one |
| `{W43}`, `{H43}` | Largest 4:3 area on the screen |

## Files the launcher writes

- `app\launcher.log`: everything, including every gun button press with aim position.
- `app\sessions\`: session recordings.
- `config\settings.json`: menu choices (recoil).
- Every template `to` file, at each launch.
- `sindenConfig`: `sindenSettings` and recoil.
