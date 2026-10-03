# Third-party notices

## OpenFlydigi

The Flydigi identity request/reply layout, Apex 5 DeviceType allowlist, and
related protocol behavior, and the DualSense adaptive-trigger translation in
`src/dualsense/AdaptiveTriggerTranslation.*`, were ported from and
validated against OpenFlydigi:

- Project: https://github.com/mkaliaha/openflydigi
- Reference commit: `8477300f1bd0cdd0e4a277a544aa9b151c623e62`
- Reference files: `flydigi/device.py`, `flydigi/identity.py`,
  `flydigi/motion.py`, `flydigi/effects.py`, `flydigi/ds5.py`,
  `flydigi/relay.py`, and `PROTOCOL.md`
- Copyright: 2026 Mikalai Kaliaha
- License for the referenced implementation: MIT

MIT License

Copyright (c) 2026 Mikalai Kaliaha

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
## SteamlessController DualSense fork

The VIIPER request framing, patched-sidecar compatibility checks, compact
DualSense feedback framing, and Windows sidecar lifecycle in
`src/dualsense/*` and `src/platform/windows/WindowsVirtualDualSense.cpp` were
adapted from and validated against the SteamlessController DualSense fork:

- Project: https://github.com/david419kr/steamless-controller-XB-PS-NS
- Reference commit: `e19e874e00938eff2ee28d1a928dc2ab58bf9b75`
- Reference files: `src/app/ViiperClient.*`, `src/app/VirtualControllerTypes.*`,
  `src/steam/SteamController.cpp`, and
  `third_party/viiper-patches/viiper-v0.6.1-dualsense.patch` and
  `third_party/viiper-patches/viiper-v0.7.0-asb.patch`
- Copyright: 2026 Dylan Deverill; 2026 david419kr
- License for the referenced main application implementation: MIT

MIT License

Copyright (c) 2026 Dylan Deverill
Copyright (c) 2026 david419kr

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

## VIIPER and usbip-win2 runtime components

The offline installer redistributes the pinned patched VIIPER integrated
library and sidecar fallback (GPL-3.0), plus the OSSign usbip-win2 0.9.8.0 x64
release installer built from upstream commit
`83bd1f781d57ed6efdf15530c55710cf5d4482bc` and signed through Microsoft
attestation (BSD-2-Clause). The corresponding VIIPER source reference/patch and
complete license texts are installed under `Licenses`.

> **Note (Ds in Apex):** the paragraph above is upstream text describing
> upstream's Inno setup. In this derivative work the engine files — including
> the `Licenses` folder referenced here — live under `engine/`, and the two
> bundled driver installers ship in `Prerequisites/` (see the additions
> section at the end of this file).

## HidHide

The mandatory DualSense-mode physical-controller isolation uses the public HidHide IOCTL
contract and configuration semantics. The implementation was validated against:

- Project: https://github.com/nefarius/HidHide
- Reference commit: `2b950fd9393e1644b4199f6eb4999e1720f0c6e9`
- Reference files: `Shared/HidHideIoctlContract.h`,
  `HidHideCLI/src/FilterDriverProxy.cpp`, and `DEVELOPER.md`
- Copyright: 2020 Eric Korff de Gidts; 2021-2024 Benjamin Höglinger-Stelzer
- License: MIT

The offline installer redistributes the official signed HidHide 1.5.230 x64
installer and its MIT license. It leaves a pre-existing installation owned by
the user unless complete dependency removal is explicitly requested.

## Playnite SDK

The Playnite integration plugin (`playnite/ApexSenseBridge`) references and compiles against the Playnite SDK:

- Project: https://github.com/JosefNemec/Playnite
- Reference: Playnite SDK 6.16 (Playnite 10 / 11)
- Copyright: 2017-2023 Josef Nemec
- License: MIT

MIT License

Copyright (c) 2017-2023 Josef Nemec

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

## Playnite NX Audio Switcher

The Windows `IPolicyConfig` declaration and three-role default-endpoint update
used by `WindowsAudioEndpointProtection.cpp` were adapted from Playnite NX
Audio Switcher's `AudioDeviceManager.cs`:

- Project: https://github.com/Naerian/playnite-nx-audio-switcher
- Reference file: `AudioDeviceManager.cs`
- License: MIT

MIT License

Copyright (c) Naerian

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

## Chromium Core Audio

The device-topology route used to resolve an audio endpoint to its controller
`PKEY_Device_InstanceId` was adapted from Chromium's Core Audio utility:

- Project: https://chromium.googlesource.com/chromium/src
- Reference commit: `0760405a9f0330d4a79a6d4beb48f290afc96a28`
- Reference file: `media/audio/win/core_audio_util_win.cc`
- Copyright: 2012 The Chromium Authors
- License: BSD-style (3-clause)

Copyright (c) 2012 The Chromium Authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of Google Inc. nor the names of its contributors may be
   used to endorse or promote products derived from this software without
   specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

---

## Ds in Apex additions (dependencies introduced by this derivative work)

The upstream project shipped a WPF / .NET Framework tray application. Ds in
Apex is a WinUI 3 / .NET 10 rewrite, so the NuGet packages below are new here
and are **not** part of the upstream notice list. None of their source code is
modified; each package ships with its own license text.

| Component | Version | License | Purpose |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.5.1 | Microsoft Software License Terms | WinUI 3 runtime and MSBuild wiring |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Software License Terms | WinRT projections and XAML compiler toolchain |
| H.NotifyIcon.WinUI | 2.4.1 | MIT | System tray icon (WinUI 3 has no built-in tray API) |
| WinUIEx | 2.9.3 | MIT | Window features missing from WinUI 3 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM infrastructure |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | MIT | Dependency injection |
| System.Management | 10.0.0 | MIT | WMI process start/stop event monitoring |

### Bundled driver installers (not NuGet, not built from source here)

`Prerequisites/` ships two third-party installers verbatim from their upstream
projects. They run only after an explicit user action plus a SHA-256
verification; Ds in Apex never modifies them.

- **USBip 0.9.8.0** (`USBip-0.9.8.0-x64.exe`) — USB/IP for Windows
  (https://github.com/vadimgrn/usbip-win2), **BSD-2-Clause**.
  The license text shipped inside the installer is reproduced verbatim at
  `Prerequisites/USBIP-WIN2-LICENSE.txt`; the kernel-mode driver components the
  installer deploys carry the terms stated inside the installer itself.
- **HidHide 1.5.230** (`HidHide_1.5.230_x64.exe`) — Nefarius HidHide
  (https://github.com/nefarius/HidHide), **MIT**.
  License text reproduced at `Prerequisites/HIDHIDE-LICENSE.txt`.

### Ds in Apex licensing paperwork

| File | Contents |
|---|---|
| `LICENSE` | Full, verbatim GNU GPL v3.0 text |
| `NOTICE.md` | Derivative-work notice and modification record (GPL-3.0 §5a) |
| `SOURCE_OFFER.md` | Where to obtain the corresponding source |
| this file | Third-party components and their licenses |

The full text of the GNU GPL v3.0 that covers this work is available at
https://www.gnu.org/licenses/gpl-3.0.txt (see also `LICENSE` and `SOURCE_OFFER.md`).
