# Emulator notes

What we learned setting up two Sinden guns with each emulator. Templates are in `config\templates\`, and
`config\games.example.json` has an entry per emulator. Some features need a patched emulator build. They are
marked **patched build**. Those builds are not part of this repository.

General rules that held everywhere:

- **Aspect:** use widescreen only when both the picture and the aim work in it; otherwise 4:3. Never stretched.
- **Border hidden?** If the white border or quit banner disappears over a game, Windows is sending the game's
  full-screen window straight to the display. Use `keepComposited`, `{SCREEN_H_1}` for the window height, or
  `borderInset`.
- **Gun reaches only ~75 % of the picture's width?** The gun hook maps the gun over the whole 16:9 screen while the
  picture is 4:3. Use `borderArea: "4:3"`. For hooks that map over a window, make the window exactly the picture
  (`borderless`, `aspect: "4:3"`, `fitChildWindow`).
- **Admin:** gun hooks generally work without admin rights when the emulator also runs as a normal program. Keep
  `elevate` off unless something proves otherwise.
- **Test in real gameplay.** Attract mode and 1-player runs can look smooth when 2-player gameplay isn't. The
  session recordings measure frame rate during play.

## MAME

- Gun input via raw input (`-mouse`/lightgun provider, or a `ctrlr` file mapping each gun's mouse).
- MAME maps guns over the whole screen, so use `borderArea: "4:3"` for 4:3 games.
- `postLaunchKeys` with `Space` dismisses the start-up info screens.
- `tools\mamectl` drives service menus by script (free play, difficulty, lives).

## PCSX2 (GunCon 2)

- Official PCSX2 aims every GunCon 2 with the one system pointer, so it is **one gun only**.
- **Patched build** (two guns): a raw-mouse input source that feeds each configured mouse into its own pointer
  (`[RawMouse] Pointer1/Pointer2`), with GunCon 2 using the pointer named in its `Pointer` setting. The template
  is written for it.
- **Patched build** (optional): "calibration shot" hits the screen centre, plus a `CenterShot` binding. Together
  with `postLaunchKeys` this passes boot-time gun calibration screens without anyone aiming.
- Vulkan was much faster than Direct3D 12 in 2-player split screen on our GPU.
- Infinite credits / settings: PCSX2 cheats (`.pnach`) in `emulators\pcsx2\cheats`; find addresses with
  `tools\ps2mem`.

## RPCS3 (PS Move / GunCon 3)

- PS Move via the "Raw Mouse" handler gives each gun its own Move (`raw_mouse.yml` with each gun's device,
  `gem_mouse.yml` for buttons).
- GunCon 3: `guncon3.yml`. If the handler ignores the gun's mouse button while keys work, use `triggerKeys` and
  map those keys to the trigger in `raw_mouse.yml`.
- **Patched build** (optional): a `Shake` binding (pump = shake for reload) and steadier reloads.
- Game patches (`patches\<TITLEID>_patch.yml` + `patch_config.yml`) or `tools\rpcs3hold` for infinite credits.
- `quitKeyAlso: "Down"` lets the quit button work menus too.

## Flycast (NAOMI / Dreamcast)

- `RawInput = yes`. Ports are assigned by Flycast's own device ids (`{P1MOUSE_ID}`), not full paths.
  `{FLYCAST_OTHER_DEVICES}` switches off every other mouse and keyboard so they can't take a player's port.
- Per-device button mappings live in `mappings\<device name> [<hardware id>]_arcade.cfg`. The example writes
  them with `{P1KBD_HWID}` etc.
- **Patched build** (optional): software key presses go to a "Software Keyboard" device
  (`maple_raw_keyboard_software`). Use it with `copyKeys` when a gun's own key isn't picked up.

## Supermodel (Model 3)

- `-input-system=rawinput`. Supermodel numbers mice in reverse order of Windows' list; `{P1MOUSE}` / `{P2MOUSE}`
  follow that.
- Run windowed at `{SCREEN_W}` × `{SCREEN_H_1}`: a frameless full-screen GL window hides the border.
- Calibrate guns in the game's own test menu; it is saved in NVRAM.

## Model 2 Emulator + DemulShooter

- DemulShooter (`-target=model2 -rom=<set>`) reads each gun by device (`config.ini` with `{P1MOUSE_PATH}`).
- Model 2 Lua scripts can draw the border (`border: "emulator"`) and hide crosshairs.
- If a game's trigger still reads the shared Windows mouse (every gun fires for P1), a code path is not covered by
  the hook. `tools\mempatch` can redirect it in memory without touching the exe, which DemulShooter checksums.

## Cxbx-Reloaded (Chihiro) + DemulShooter

- DemulShooter maps the gun over the emulator's render window, so make the window exactly the 4:3 picture:
  `borderless`, `aspect: "4:3"`, `fitChildWindow`, `borderArea: "4:3"`.
- Per-game saved settings (EEPROM / backup RAM) can be copied in with `binary` templates when one board
  serves several games.

## TeknoParrot

- Run `TeknoParrotUi.exe --profile=<Profile>.xml` with `windowProcess` set to the game's process.
- Write the profile from a template so gun device paths stay current. TeknoParrot's cached
  `InputBindings\<Profile>.json` overrides the profile, so delete it with `deleteBeforeLaunch`.
- Bind Start/coin with `DevicePath` `null` (= a key from software) and relay the guns' keys with `relayKeys`.
- `keepForeground` keeps the game in front of the loader console.
- Some full-screen games come up see-through or minimize on focus loss: `refreshWindowOnStart`, `keepForeground`.
- Games that set their 3D resolution from the window: `borderlessDelayMs`.

## Sinden software

- Keep a copy for the launcher (`emulators\sinden`) so it can edit its settings and restart it. A separate install
  you run yourself is never touched.
- Never force-kill it: the guns can drop off USB until replugged. The launcher closes it with WM_CLOSE.
- With slot A/B on "Auto", Sinden gives slot A to whichever gun Windows lists first, which can swap the
  players. `sindenSettings` pins them.
- An off-screen trigger pull is turned into a pump/reload press by Sinden.
