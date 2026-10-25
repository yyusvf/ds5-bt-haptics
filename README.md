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

> Status: experimental, tested on one Windows 11 PC with a standard DualSense. Console output is
> currently German. Not affiliated with Sony or with the DS5Dongle project.

## Features

- HD haptics from games that support the DualSense on PC (tested: *007 First Light*, which uses
  Sony's `libScePad`)
- Adaptive triggers, light bar, player LEDs and classic rumble (output report passthrough)
- Buttons, sticks, touchpad, motion sensors (input report passthrough, ~440 Hz)
- `loopback` mode: turns whatever Windows is playing into haptics; works with any game, no driver needed
- No kernel driver of its own, no test signing: uses the signed usbip-win2 driver

Not yet supported: controller speaker / headphone audio (channels 1/2, needs Opus encoding) and the
controller microphone (currently delivers silence).

## Requirements

- Windows 10 1903+ / Windows 11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- DualSense or DualSense Edge paired via Windows Bluetooth
- For `bridge`: [usbip-win2](https://github.com/vadimgrn/usbip-win2/releases) and
  [HidHide](https://github.com/nefarius/HidHide/releases)

## Setup

1. Install **usbip-win2** (create a restore point first; installation briefly restarts the USB hubs).
2. In the **HidHide Configuration Client**:
   - *Applications*: add `ds5-bt-haptics.exe`
   - *Devices*: tick the **Bluetooth** DualSense and enable device hiding. Do **not** hide the
     virtual USB DualSense that appears later.
3. Don't run other DualSense tools (DSX, DS4Windows) at the same time.

## Usage

Double-click `DS5 Bridge starten.cmd`, or run:

```
ds5-bt-haptics.exe bridge
```

No admin rights needed. A "DualSense Wireless Controller" with its own audio device appears; games
see it as a wired controller. **Start the game after the bridge.** Close the window to stop.

| Command | What it does | Needs |
|---|---|---|
| `bridge` | software dongle: game haptics, adaptive triggers, light bar | usbip-win2, HidHide |
| `devtest` | plays a test pattern on the virtual controller's audio device (ch 3/4), like a game | running `bridge` |
| `loopback` | Windows audio → haptics, live | nothing |
| `test` | test pattern directly over Bluetooth (left, right, both, sweep, clicks) | nothing |
| `wav <file>` | audio file → haptics (4-ch files: ch 3/4) | nothing |
| `list` | detected controllers and their output report IDs | nothing |
| `selftest` | protocol checks incl. a simulated usbip client | nothing |

Options: `--gain 1..4`, `--single`, `--buf 16..127`, `--latency <ms>`, `--no-attach`, `--real-mac`, `-v`

If a game stays silent, disable Steam Input for it and restart the game.

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
dotnet publish DS5BtHaptics -c Release -o ds5-bt-haptics
```

## Credits and license

- [DS5Dongle](https://github.com/awalol/DS5Dongle) by awalol: Bluetooth haptics protocol, USB
  descriptors and report translation were ported from its firmware. Thank you!
- [usbip-win2](https://github.com/vadimgrn/usbip-win2), [HidHide](https://github.com/nefarius/HidHide),
  [NAudio](https://github.com/naudio/NAudio)

This project is MIT licensed, see [LICENSE](LICENSE). `DS5BtHaptics/Protocol.cs` is derived from
DS5Dongle's MPL-2.0 file `src/audio.cpp` and stays under MPL-2.0. Details in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Deutsche Kurzanleitung: [README.de.md](README.de.md)
