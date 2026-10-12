# Virtual monitors

GlassesDesk shows other monitors as panels. To get panels without physical monitors, Windows needs
fake ("virtual") monitors that it extends the desktop onto, created by an Indirect Display Driver
(IddCx). GlassesDesk then captures them like any other monitor.

## Options compared

| Driver | License | Signed | Monitors persist | Control | Verdict |
|---|---|---|---|---|---|
| [VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (VDD) | MIT | Yes (SignPath) | Yes, until disabled | XML config + named pipe, winget package, control app | **Chosen** |
| [Parsec VDD](https://github.com/nomi-san/parsec-vdd) | Parsec's own terms; the driver is meant for Parsec Teams/Warp | Yes | No, an app must ping it every second or monitors vanish | IOCTL API, 16 per adapter | Nice lifecycle, licence unclear for this use |
| Microsoft IddSampleDriver | MIT sample | No | Yes | None | Needs test-signing mode; no |
| Amyuni usbmmidd | Freeware | Yes | Yes | CLI | Old, closed source |

VDD wins: open source, signed (no test-signing mode), actively maintained (2026 activity),
installable with winget, and configurable without writing our own driver.

## How VDD works (from its source)

- Settings live in `C:\VirtualDisplayDriver\vdd_settings.xml`: `<monitors><count>N</count></monitors>`,
  the resolution list (1920x1080@60 is in the defaults) and refresh rates.
- The driver listens on the named pipe `\\.\pipe\MTTVirtualDisplayPipe`; `RELOAD_DRIVER` re-reads the
  XML, so the monitor count can change without a reboot.
- Its monitors report the EDID name **`VDD by MTT`**. GlassesDesk uses that to tell them apart: when any
  are present it shows only those (`--all` shows every non-glasses monitor).
- The monitors stay there until the driver is disabled (Device Manager → Display adapters →
  "Virtual Display Driver" → Disable, or VDD's `toggle-VDD.ps1`). While GlassesDesk isn't running,
  windows can end up on monitors you can't see. That's the main downside; see "Later" below.

## Setup (one-time, needs admin)

1. Install: `winget install --id=VirtualDrivers.Virtual-Display-Driver -e`
   (or the installer from the [releases page](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases)).
2. Set the monitor count to 3 with the Virtual Driver Control app, or edit
   `C:\VirtualDisplayDriver\vdd_settings.xml` (`<count>3</count>`) and reload the driver from the app.
3. Settings → System → Display:
   - set each virtual monitor to **1920 × 1080**, 100% scale (matches the glasses' pixels, so text is 1:1);
   - drag them into a row in the order you want them around you;
   - make the **middle virtual monitor the main display** (taskbar and new windows land there);
   - move the glasses' own display off to a corner so the mouse doesn't wander onto it.
4. Run `GlassesDesk.exe`, then press **Ctrl+Alt+0** for pixel-perfect panel size.

## Later

- Let GlassesDesk enable the driver on start and disable it on exit, so the virtual monitors only exist
  while you're wearing the glasses (needs an elevated helper or a scheduled task).
- Option to change the monitor count from GlassesDesk (write the XML, send `RELOAD_DRIVER`).
