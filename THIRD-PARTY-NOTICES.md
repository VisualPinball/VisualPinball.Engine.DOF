# Third-party notices

`VpeDof` (`VpeDof.dll`, `libVpeDof.so`, `libVpeDof.dylib`) contains the software below, compiled from the sources in `third-party/` at the revisions the repository pins and linked statically into the one library. The whole of `VpeDof`, and the DOF package with it, is distributed under the GNU General Public License version 3 or later (see [LICENSE](LICENSE)).

The complete corresponding source of every release is attached to the release as `VisualPinball.Engine.DOF-<version>-source.tar.gz`: this repository with the submodules at their pinned revisions, the patches VPE applies to libdof, the CMake project and the build scripts. With it, `VpeDof` can be rebuilt, including with modified versions of the LGPL libraries below (see "Building" in the [README](README.md)). `native/source-archive.py` writes the same archive for any commit. The VPE player carries the archive of the commit its `VpeDof` was built from, with this file, the licence texts and the commits (`SOURCE.txt`), in its `DOF` folder (`<player>_Data/DOF`, or `Contents/Resources/DOF` in the macOS app).

| Component | Version | Licence | Licence text |
| --- | --- | --- | --- |
| [libdof](https://github.com/vpinball/libdof), C++ port of the [DirectOutput Framework](https://github.com/DirectOutput/DirectOutput), with VPE's patches (`native/patches`) | 0.5.4 (d92f8fc) | GPL-3.0-or-later | `third-party/libdof/LICENSE` |
| [hidapi](https://github.com/libusb/hidapi) | 0.15.0 (d6b2a97) | BSD-3-Clause (one of its three licence options, chosen here) | `third-party/hidapi/LICENSE-bsd.txt`, below |
| [libusb](https://github.com/libusb/libusb) | 1.0.29 (15a7ebb) | LGPL-2.1-or-later | `third-party/libusb/COPYING` |
| [libserialport](https://github.com/sigrokproject/libserialport) | 21b3dfe | LGPL-3.0-or-later | `third-party/libserialport/COPYING` |
| [libftdi1](https://github.com/jsm174/libftdi) (jsm174's fork, as libdof uses) | 1.5 (8b13836) | LGPL-2.1-only | `third-party/libftdi/COPYING.LIB`, `third-party/libftdi/LICENSE` |
| [tinyxml2](https://github.com/leethomason/tinyxml2), bundled with libdof | 11.0.0 | Zlib | below |
| [exprtk](https://github.com/ArashPartow/exprtk), bundled with libdof | as in libdof | MIT | below |
| [stb_image](https://github.com/nothings/stb), bundled with libdof | 2.30 | MIT or public domain (Unlicense) | below |

Copyright holders, as the sources name them:

- libdof: Copyright (C) 2000-2024 Visual Pinball development team and contributors.
- hidapi: Copyright (c) 2010, Alan Ott, Signal 11 Software, and the hidapi contributors (`third-party/hidapi/AUTHORS.txt`).
- libusb: Copyright (C) 2001 Johannes Erdfelt, 2007-2008 Daniel Drake, 2012-2023 Nathan Hjelm, and the libusb contributors (`third-party/libusb/AUTHORS`).
- libserialport: Copyright (C) 2010-2012 Bert Vermeulen, 2010-2015 Uwe Hermann, 2013-2015 Martin Ling, and the libserialport contributors (`third-party/libserialport/AUTHORS`).
- libftdi1: Copyright (C) 2003-2020 Intra2net AG and the libftdi developers (`third-party/libftdi/AUTHORS`).
- tinyxml2: Lee Thomason.
- exprtk: Arash Partow.
- stb_image: Copyright (c) 2017 Sean Barrett.

On Linux, `libVpeDof.so` also contains parts of GCC's C++ runtime (libstdc++ and libgcc), linked statically under the GCC Runtime Library Exception. On Windows it contains parts of Microsoft's C runtime (static CRT). Neither changes the licence of `VpeDof`.

Not bundled and not needed: FTDI's D2XX driver, Ultimarc's PacDrive DLL, BASS. libudev (Linux) and the operating system's HID, USB and serial frameworks are used from the system.

## hidapi (BSD-3-Clause)

```
Copyright (c) 2010, Alan Ott, Signal 11 Software
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright
      notice, this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.
    * Neither the name of Signal 11 Software nor the names of its
      contributors may be used to endorse or promote products derived from
      this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

## tinyxml2 (Zlib)

```
Original code by Lee Thomason (www.grinninglizard.com)

This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any
damages arising from the use of this software.

Permission is granted to anyone to use this software for any
purpose, including commercial applications, and to alter it and
redistribute it freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must
not claim that you wrote the original software. If you use this
software in a product, an acknowledgment in the product documentation
would be appreciated but is not required.

2. Altered source versions must be plainly marked as such, and
must not be misrepresented as being the original software.

3. This notice may not be removed or altered from any source
distribution.
```

## exprtk (MIT)

```
C++ Mathematical Expression Toolkit Library
Author: Arash Partow (1999-2024)
URL: https://www.partow.net/programming/exprtk/index.html

Free use of the C++ Mathematical Expression Toolkit Library is
permitted under the guidelines and in accordance with the most
current version of the MIT License.
https://www.opensource.org/licenses/MIT
SPDX-License-Identifier: MIT
```

The MIT License:

```
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

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

## stb_image (MIT, alternative A of its two licences)

```
Copyright (c) 2017 Sean Barrett

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

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

The full texts of the GNU licences are in the files named in the table; the release zips carry them in `licenses/`.
