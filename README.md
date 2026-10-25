# DS5 BT Haptics

**DualSense HD haptics over Bluetooth on Windows, without a dongle.**

A software-only take on [DS5Dongle](https://github.com/awalol/DS5Dongle) by awalol. Instead of a
Raspberry Pi Pico acting as a USB bridge, this program presents a **virtual wired DualSense** to
Windows (via [usbip-win2](https://github.com/vadimgrn/usbip-win2)) and forwards everything to your
Bluetooth controller, including the HD haptics that PC games only send to a wired controller.

```
Game ──USB audio ch 3/4──▶ virtual USB DualSense (usbip-win2)
                                │  USB/IP on 127.0.0.1:3240
                                ▼
                       ds5-bt-haptics bridge ──report 0x32 (3 kHz haptics)──▶ DualSense (Bluetooth)
                                ▲             ──report 0x31 (light bar, triggers, rumble)──▶
                                └──────────── input report 0x31 ◀────────────────────────
```

> Status: experimental, tested on one Windows 11 PC with a standard DualSense. The app is
> currently German only. Not affiliated with Sony or with the DS5Dongle project.

## Features

- Tray app with installer: status, battery, haptics strength, start with Windows; sets up
  usbip-win2 and HidHide for you and reconnects on its own
- HD haptics from games that support the DualSense on PC (tested: *007 First Light*, which uses
  Sony's `libScePad`)
- Adaptive triggers, light bar, player LEDs and classic rumble (output report passthrough)
- Buttons, sticks, touchpad, motion sensors (input report passthrough, ~440 Hz)
- `loopback` mode: turns whatever Windows is playing into haptics; works with any game, no driver needed
- No kernel driver of its own, no test signing: uses the signed usbip-win2 driver

Not yet supported: controller speaker / headphone audio (channels 1/2, needs Opus encoding) and the
controller microphone (currently delivers silence).

## Install

1. Download **`DS5BtHaptics-Setup.exe`** from the [latest release](https://github.com/yyusvf/ds5-bt-haptics/releases/latest)
   and run it. It installs for the current user, no admin rights needed; the .NET 8 Desktop
   Runtime is downloaded if missing.
2. The app opens next to the clock. If something is missing it shows one step at a time:
   - **usbip-win2**: downloaded from its official GitHub release, checksum and signature verified,
     then its own setup runs (it briefly restarts the USB hubs; a restore point doesn't hurt).
   - **HidHide**: same, from its official release.
   - **HidHide einrichten**: allows the app to see the Bluetooth DualSense and hides it from games,
     so they only see the virtual wired one. Windows asks for admin rights once.
3. Turn on the controller (PS button). Done. The app starts with Windows and lives in the tray.

Don't run other DualSense tools (DSX, DS4Windows) at the same time. Uninstalling offers to make
the Bluetooth controller visible to games again.

## Usage

Click the tray icon for the flyout:

- **Bridge** switch: off turns the controller back into a plain Bluetooth gamepad
- Status of the controller (battery), the virtual USB controller and haptics, plus **Testen**,
  which plays a test pattern through the virtual controller's audio device, like a game
- Settings: haptics strength (live), lower latency, start with Windows, appearance

- **Games with their own Bluetooth haptics** (e.g. *Cyberpunk 2077*, found automatically in Steam
  libraries; more can be added): while one runs, the bridge pauses and the game drives the real
  Bluetooth controller itself. Otherwise both paths would fight over the haptics. The flyout asks
  once to allow such games in HidHide. **Trotzdem aktivieren** on the controller card turns the
  bridge on anyway until the game exits.

The dot on the tray icon: green = ready, yellow = waiting for the controller, red = setup needed.
The bridge reconnects on its own when the controller is switched off and on again.
**Start games after the controller is connected.** If a game stays silent, disable Steam Input
for it and restart the game.

The log is in `%LOCALAPPDATA%\DS5BtHaptics\bridge.log` (it contains your controller's Bluetooth
address, so think twice before posting it).

### Command line

The setup also installs `cli\ds5-bt-haptics.exe` (also available as a zip):

| Command | What it does | Needs |
|---|---|---|
| `bridge` | the bridge without the app (console, Ctrl+C stops) | usbip-win2, HidHide |
| `devtest` | plays a test pattern on the virtual controller's audio device (ch 3/4), like a game | running bridge |
| `loopback` | Windows audio → haptics, live | nothing |
| `test` | test pattern directly over Bluetooth (left, right, both, sweep, clicks) | nothing |
| `wav <file>` | audio file → haptics (4-ch files: ch 3/4) | nothing |
| `list` | detected controllers and their output report IDs | nothing |
| `selftest` | protocol checks incl. a simulated usbip client | nothing |

Options: `--gain 1..4`, `--single`, `--buf 16..127`, `--latency <ms>`, `--no-attach`, `--real-mac`, `-v`

The CLI needs its own HidHide entry (*Applications* in the HidHide Configuration Client).

## How it works / lessons learned

- **Bluetooth haptics**: report `0x32` (142 bytes) carries 64-byte frames of 8-bit stereo audio at
  3 kHz plus a CRC32 seeded with the BT header byte `0xA2`, exactly as DS5Dongle sends it. Game audio
  arrives as 48 kHz 4-channel USB audio; channels 3/4 are resampled to 3 kHz.
- **USB/IP server**: a minimal USB/IP 1.1.1 server emulates the DualSense's USB descriptors, HID
  endpoints, UAC1 controls and isochronous audio endpoints. Isochronous URBs are completed in real
  time (1 packet = 1 ms).
- **MAC spoofing**: if the virtual pad reports the real controller's Bluetooth address in feature
  report `0x09`, Steam / GameInput think the same controller was plugged in by cable and drop the
  Bluetooth link (Win32 error 1167). The bridge reports a locally administered variant instead.
- **Timer throttling**: Windows 11 ignores `timeBeginPeriod` for processes without a visible window,
  so waits snap to 15.6 ms. Audio completions then arrived in bursts and Windows kept resetting the
  audio endpoint. The bridge uses a high-resolution waitable timer and opts out of power throttling.
- Steam sends ~100 identical output reports per second; unchanged reports are forwarded at most every
  500 ms so the haptics stream keeps priority on the Bluetooth link.

## Building

```
./build-release.ps1
```

Needs the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php); writes
`dist/DS5BtHaptics-Setup.exe`. `DS5BtHaptics` is the bridge and CLI, `DS5BtHaptics.App` the tray
app (WPF), which compiles the bridge sources in. Its tray shell and theme come from
[ysfUtil](https://github.com/yyusvf/ysfUtil).

## Credits and license

- [DS5Dongle](https://github.com/awalol/DS5Dongle) by awalol: Bluetooth haptics protocol, USB
  descriptors and report translation were ported from its firmware. Thank you!
- [usbip-win2](https://github.com/vadimgrn/usbip-win2), [HidHide](https://github.com/nefarius/HidHide),
  [NAudio](https://github.com/naudio/NAudio)

This project is MIT licensed, see [LICENSE](LICENSE). `DS5BtHaptics/Protocol.cs` is derived from
DS5Dongle's MPL-2.0 file `src/audio.cpp` and stays under MPL-2.0. Details in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Deutsche Kurzanleitung: [README.de.md](README.de.md)
