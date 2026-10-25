# Third-party notices

## DS5Dongle

https://github.com/awalol/DS5Dongle

Parts of this program are ports of the DS5Dongle firmware:

| This project | DS5Dongle source | License |
|---|---|---|
| `DS5BtHaptics/Protocol.cs` (BT haptics report 0x32, audio control block) | `src/audio.cpp` | MPL-2.0 |
| `DS5BtHaptics/Protocol.cs` (CRC32 seeds, output/feature checksums) | `src/utils.h` | MIT |
| `DS5BtHaptics/Descriptors.cs` (USB configuration and HID report descriptors) | `src/usb_descriptors.cpp` | MIT (incl. TinyUSB example by HiFiPhile) |
| `DS5BtHaptics/Bridge.cs`, `VirtualDualSense.cs` (report translation, UAC1 values) | `src/main.cpp`, `src/usb.cpp`, `src/bt.cpp` | MIT |

`DS5BtHaptics/Protocol.cs` is covered by the Mozilla Public License 2.0:
https://mozilla.org/MPL/2.0/ . Its source is available in this repository.

DS5Dongle repository license:

```
MIT License

Copyright (c) 2026 awalol

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

TinyUSB descriptor example (in `src/usb_descriptors.cpp`): MIT License, Copyright (c) 2023 HiFiPhile.

## NAudio

https://github.com/naudio/NAudio — MIT License, Copyright (c) Mark Heath & contributors.
Used as a NuGet package for WASAPI capture/playback and audio file decoding.
