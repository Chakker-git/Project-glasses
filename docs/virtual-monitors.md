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

Learned the hard way: follow this order. Getting it wrong can leave Windows drawing your taskbar on
a monitor you can't see, or make the whole PC sluggish.

1. **Make a real screen your main display first** (laptop panel or a physical monitor): Settings →
   System → Display → select it → "Make this my main display". Never make the glasses or a virtual
   monitor the main display.
2. Install VDD. If `winget` is missing (common on managed laptops), download **Virtual Driver Control**
   (x64) from the [releases page](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases),
   run it as administrator and click Install.
3. Set the monitor count to **2** in Virtual Driver Control. Start with two; add more later if
   performance is fine.
4. In Display Settings, for each virtual monitor:
   - **Advanced display → refresh rate 60 Hz.** VDD offers up to 240 Hz and Windows may pick a high
     rate; on integrated graphics that makes everything lag. GlassesDesk warns at startup if one is above 60 Hz;
   - resolution **1920 × 1080**, scale 100%;
   - if Windows set them to **Duplicate** another screen, choose **Extend these displays**.
5. Arrange them: virtual monitors side by side (or one above the other); the glasses' own display
   off in a corner so the mouse doesn't wander onto it.
6. Check with `GlassesDesk.exe --list` (prints the displays and exits), then run `GlassesDesk.exe`
   and press **Ctrl+Alt+0** for pixel-perfect size. The panel straight ahead is your main display if
   it's a virtual one, otherwise the middle virtual monitor.

## Safety rules

- Don't change the monitor count, reload or disable the driver while a virtual monitor is the main
  display. Switch the main display to a real screen first.
- Quit GlassesDesk (Ctrl+Alt+Q) before changing VDD settings.
- When you're done with the glasses, disable the driver (Device Manager → Display adapters →
  Virtual Display Driver → Disable device, or the switch in Virtual Driver Control). The monitors
  otherwise stay active, including after a reboot, and windows can end up where you can't see them.
- Windows that land on the glasses' own display are moved to the centre panel automatically while
  GlassesDesk runs. When it isn't running, put the glasses on (they're an ordinary monitor then)
  or use Win+Shift+Left/Right to move a window across.

## Recovering if the screen goes wrong

1. **Win+Ctrl+Shift+B** resets the graphics driver.
2. **Ctrl+Shift+Esc** → File → Run new task → `explorer` restarts the taskbar and desktop.
3. **Win+P**, Up arrow to the top ("PC screen only"), Enter: forces everything onto the laptop
   screen, even when you can't see the menu.
4. Win+X, M opens Device Manager: disable or uninstall "Virtual Display Driver".

## Later

- Let GlassesDesk enable the driver on start and disable it on exit, so the virtual monitors only exist
  while you're wearing the glasses (needs an elevated helper or a scheduled task).
- Option to change the monitor count from GlassesDesk (write the XML, send `RELOAD_DRIVER`).
