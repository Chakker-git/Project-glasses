# Project Glasses

World-locked virtual monitors for RayNeo Air 4 Pro glasses on Windows 11.

Right now the glasses act like one plain monitor glued to your face. The goal: several virtual
Windows monitors fixed in space around you. Turn your head and the next monitor slides into
view, and you can drag windows between them.

## How it will work

| Stage | What | Status |
|---|---|---|
| 1. Sensor probe | Find out whether the glasses expose a motion sensor (IMU) over USB | **done**: `1BBB:AF50`, vendor HID, 64-byte frames |
| 2. Head tracking | Turn on the IMU, decode it, and turn it into head yaw/pitch/roll, with a recenter key to fix drift | **done**: ~500 Hz, ≤1% error per 360° turn |
| 3. Renderer | `GlassesDesk`: full-screen on the glasses, captures every other monitor and draws it as a panel fixed in space | **testing on hardware** |
| 4. Virtual monitors | Virtual Display Driver (MIT, signed) adds fake 1920×1080 monitors; GlassesDesk shows only those when present. See [docs/virtual-monitors.md](docs/virtual-monitors.md) | **ready to install** |
| 5. Polish | Compass drift correction, sharper text, gaze-following cursor | — |

Stack: C# / .NET 8, HidSharp for USB HID, Direct3D 11 + Windows.Graphics.Capture for rendering.

RayNeo says the Air 4 Pro doesn't support 3DoF, but that's only about their software. The glasses
have a 9-axis IMU (accel, gyro, magnetometer) at ~500 Hz on a vendor HID interface. The protocol
comes from two MIT-licensed projects:
[RayNeo-Air-3S-Pro-OpenVR](https://github.com/verncat/RayNeo-Air-3S-Pro-OpenVR) and
[hovera](https://github.com/MegaBadCoder/hovera), a macOS app doing the same thing this project
does for Windows. See `src/GlassesCore/RayNeoProtocol.cs`.

## Layout

- `src/GlassesCore`: protocol decoder, HID connection, orientation filter (shared library)
- `src/GlassesProbe`: diagnostic CLI
- `src/GlassesDesk`: the world-locked monitor app (Direct3D 11 via Vortice, Windows.Graphics.Capture)
- `tests/GlassesCore.Tests`: unit tests (`dotnet test`)

## Running GlassesDesk

Plug in the glasses, then run `GlassesDesk.exe`. It finds the glasses' display (`SmartGlasses`),
draws every other monitor (or only the virtual ones, if any exist) as a panel around you, laid out
the way they're arranged in Windows Display Settings: a monitor placed above another appears above
you, one to the right appears to the right. Panels stay fixed in space as you turn your head, and
the layout rebuilds itself when you change resolution, orientation or arrangement.
The main display sits straight ahead.

Hold still for a couple of seconds at start: tracking calibrates, then the row appears centred
where you're looking.

Hotkeys (Ctrl+Alt + ...):

| Key | Action |
|---|---|
| R | Recenter: the row re-centres on where you're looking |
| H | Level calibration: hold your head level, look straight ahead, press. Fixes tilted panels (the sensor sits ~17° nose-down in the frame). Runs automatically on first launch |
| Shift+Up / Shift+Down | Fine-tune tilt (if panels lean when you turn your head) |
| Shift+Left / Shift+Right | Fine-tune roll (if panels look rotated straight ahead) |
| Up / Down | Panel size |
| 0 | Pixel-perfect panel size: one desktop pixel per glasses pixel when looking straight at a panel (sharpest) |
| PgUp / PgDn | Field of view. Line up a panel corner with a real object behind it and turn your head slowly: the corner should stay on the object. If it overshoots or lags, adjust |
| Left / Right | Motion prediction (ms) |
| F | Text filter: sharp / smooth / plain. Compare edge shimmer while moving your head |
| S | Jitter stabilizer on/off |
| Q | Quit (saves settings and gyro calibration) |

Options: `--list` (show displays), `--glasses N`, `--center N` (panel index straight ahead),
`--fov`, `--width`, `--gap`, `--predict-ms`. Settings persist in `%LOCALAPPDATA%\ProjectGlasses\settings.json`.

Tip: in Windows Display Settings, put the glasses' display somewhere the mouse won't wander onto
(e.g. diagonally off a corner). The cursor only shows on the panels.

## Running the probe

### Get the exe

Option A, no install: open the repo's **Actions** tab on GitHub, pick the latest `build` run,
download the `ProjectGlasses-win-x64` artifact and unzip it.

Option B, from source: install the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`), then
from the repo folder run `dotnet run --project src/GlassesProbe -- list`.

### Run it

Plug the glasses into the laptop, then in a terminal in the folder with the exe:

```
GlassesProbe.exe list
GlassesProbe.exe imu
```

- `imu` turns on the glasses' motion sensor and prints live yaw/pitch/roll 4x per second.
  Keep still for the first 3 seconds while it calibrates. `R` recenters, `M` logs the current angles, `Q` quits.
  It sends three commands to the glasses: device info, IMU on, and IMU off on exit.

- `list` shows every HID and USB device and flags the likely glasses with `*`.
- `dump` reads the flagged devices for 30 seconds while you follow head-movement prompts
  (still → turn left/right → nod → tilt). It ends with a summary of which bytes moved with which motion.
- If nothing gets flagged, find the glasses' vendor ID (`VID_xxxx`) in the USB section of `list` and
  run `GlassesProbe.exe dump xxxx`.

Each run saves its output to a `probe-*.txt` file next to the exe, plus a `.csv` of raw reports per device.
Send the `.txt` files back for analysis.

`list` and `dump` are read-only.
