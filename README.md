# Project Glasses

World-locked virtual monitors for RayNeo Air 4 Pro glasses on Windows 11.

Right now the glasses act like one plain monitor glued to your face. The goal: several virtual
Windows monitors fixed in space around you. Turn your head and the next monitor slides into
view, and you can drag windows between them.

## How it will work

| Stage | What | Status |
|---|---|---|
| 1. Sensor probe | Find out whether the glasses expose a motion sensor (IMU) over USB, and capture its raw data | **in progress** |
| 2. Head tracking | Decode the IMU packets and turn them into head yaw/pitch/roll, with a recenter hotkey to fix drift | blocked on 1 |
| 3. Virtual monitors | Add 2–4 virtual displays with an open-source Indirect Display Driver so Windows extends the desktop onto them | — |
| 4. Renderer | Full-screen app on the glasses: capture each virtual monitor, draw them as panels on a curved ring, rotate the view against head motion | — |
| 5. Latency polish | Predict head motion a few ms ahead so the panels stay put | — |

Stack: C# / .NET 8, HidSharp for USB HID, Direct3D 11 + Windows.Graphics.Capture for rendering.

**Known risk:** RayNeo says the Air 4 Pro doesn't support 3DoF. That's a software statement.
Earlier RayNeo glasses (Air 2/2s/3s) have an IMU inside despite similar marketing, which is
what stage 1 is checking.

## Stage 1: running the probe

### Get the exe

Option A, no install: open the repo's **Actions** tab on GitHub, pick the latest `build` run,
download the `GlassesProbe-win-x64` artifact and unzip it.

Option B, from source: install the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`), then
from the repo folder run `dotnet run --project src/GlassesProbe -- list`.

### Run it

Plug the glasses into the laptop, then in a terminal in the folder with the exe:

```
GlassesProbe.exe list
GlassesProbe.exe dump
```

- `list` shows every HID and USB device and flags the likely glasses with `*`.
- `dump` reads the flagged devices for 30 seconds while you follow head-movement prompts
  (still → turn left/right → nod → tilt). It ends with a summary of which bytes moved with which motion.
- If nothing gets flagged, find the glasses' vendor ID (`VID_xxxx`) in the USB section of `list` and
  run `GlassesProbe.exe dump xxxx`.

Each run saves its output to a `probe-*.txt` file next to the exe, plus a `.csv` of raw reports per device.
Send the `.txt` files back for analysis.

The probe is read-only: it never sends anything to the glasses.
