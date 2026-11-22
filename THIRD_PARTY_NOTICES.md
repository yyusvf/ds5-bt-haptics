# Third-party notices

## DS5Dongle

https://github.com/awalol/DS5Dongle

Parts of this program are ports of the DS5Dongle firmware:

| This project | DS5Dongle source | License |
|---|---|---|
| `DS5BtHaptics/Protocol.cs` (BT haptics report 0x32, audio reports 0x36/0x39, audio control block) | `src/audio.cpp` | MPL-2.0 |
| `DS5BtHaptics/Speaker.cs` (speaker path: resampling, Opus settings) | `src/audio.cpp` | MPL-2.0 |
| `DS5BtHaptics/Protocol.cs` (CRC32 seeds, output/feature checksums) | `src/utils.h` | MIT |
| `DS5BtHaptics/Descriptors.cs` (USB configuration and HID report descriptors) | `src/usb_descriptors.cpp` | MIT (incl. TinyUSB example by HiFiPhile) |
| `DS5BtHaptics/Bridge.cs`, `VirtualDualSense.cs` (report translation, UAC1 values) | `src/main.cpp`, `src/usb.cpp`, `src/bt.cpp` | MIT |

`DS5BtHaptics/Protocol.cs` and `DS5BtHaptics/Speaker.cs` are covered by the Mozilla Public License 2.0:
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

## Concentus

https://github.com/lostromb/concentus — pure C# port of the Opus codec, BSD 3-Clause License.
Copyright (c) by various holding parties, including Skype Limited, Xiph.Org Foundation, CSIRO,
Microsoft Corporation, Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg.

```
Copyright (c) by various holding parties, including (but not limited to): 
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.
```

## NAudio

https://github.com/naudio/NAudio — MIT License, Copyright (c) Mark Heath & contributors.
Used as a NuGet package for WASAPI capture/playback and audio file decoding.
