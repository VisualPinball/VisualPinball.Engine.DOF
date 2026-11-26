# Visual Pinball Engine - DOF Integration

This repository brings the [DirectOutput Framework](https://github.com/DirectOutput/DirectOutput) (DOF) to the [Visual Pinball Engine](https://github.com/freezy/VisualPinball.Engine) (VPE): feedback devices like solenoids, contactors, shakers, flashers and addressable LED strips, driven by the same configuration files DOF uses on Windows. It uses [libdof](https://github.com/vpinball/libdof), the C++ port of DOF that Visual Pinball Standalone uses, so it runs on Windows, Linux and macOS without installing anything else.

## Structure

- `third-party/` holds libdof and the hardware libraries it uses (hidapi, libusb, libserialport, libftdi) as submodules, at the revisions libdof pins.
- `native/` is the CMake project that builds `VpeDof`, one shared library per platform with libdof, the hardware libraries (linked statically) and a small C ABI (`native/include/vpe_dof.h`). The patches in `native/patches` are applied to a copy of libdof in the build folder, so the submodule stays as upstream has it.
- `VisualPinball.Engine.DOF` is the .NET library (netstandard2.1, no Unity) with the bindings to `VpeDof`.
- `VisualPinball.Engine.DOF.Test` runs the bindings against the real native library, with an Art-Net controller on localhost standing in for hardware.
- `VisualPinball.Engine.DOF.Unity` is the Unity package. The builds copy their binaries into its `Plugins` folder.
- `Tools~/parity` compares libdof with C# DOF (see its [README](Tools~/parity/README.md)).

## Building

Get the submodules first:

```bash
git submodule update --init --recursive
```

On Windows, with Visual Studio 2022 or newer and CMake 3.24 or newer (Visual Studio 2026 needs CMake 4.2 or newer), build the native library from a command prompt or PowerShell. It uses the newest Visual Studio installed. This also runs the native smoke test and copies `VpeDof.dll` to `VisualPinball.Engine.DOF.Unity/Plugins/win-x64`:

```bash
native\build-windows.cmd
```

On Linux (CMake 3.24 or newer, a C++20 compiler, git, pkg-config and `libudev-dev`) and macOS (CMake 3.24 or newer and the Xcode command-line tools), the scripts do the same and copy the library to `Plugins/linux-x64` or `Plugins/osx`. The macOS library contains arm64 and x86_64 and runs on macOS 12.0 and newer. `BUILD_DIR` overrides the build folder.

The native libraries are not in git, and neither are their Unity import settings: Unity deletes a `.meta` in a package whose library is missing. The builds copy them from `native/unity`, where they are kept with fixed GUIDs, together with the library.

```bash
bash native/build-linux.sh
bash native/build-macos.sh
```

Then build the managed library, which copies itself to `Plugins/Managed`, and run the tests:

```bash
dotnet build VisualPinball.Engine.DOF -c Release
dotnet test VisualPinball.Engine.DOF.Test -c Release
```

The tests listen on UDP port 6454 on 127.0.0.1 and never open real devices.

`VpeDof` is loaded once and stays loaded until the process exits, as Unity does with every native plugin: it is not made to be unloaded. The GCC runtime it links statically on Linux keeps an emergency exception pool, and the file confinement keeps a thread-local root on every thread that read a file; `dlclose` frees neither, so loading and unloading it repeatedly leaks. A host that wants a fresh libdof calls `vpe_dof_destroy` and creates a new instance instead.

## Releases

The [build workflow](.github/workflows/build.yml) builds and tests every platform: Windows x64 with MSVC and the static CRT, Linux x64 in an Ubuntu 22.04 container (glibc 2.35, libstdc++ linked statically) and macOS 12.0 for arm64 and x86_64. Each job uploads its `Plugins/<rid>` and `Plugins/Managed`. A pushed `v*` tag drafts a release with those, the licence texts and the source archive. The checks it runs can be run locally:

- `native/release-check.py <library>` checks that `VpeDof` exports exactly the functions `native/include/vpe_dof.h` declares and imports only system libraries, and with `--unity-meta` that its import settings are next to it (on Linux also the newest glibc it needs, on macOS its architectures and minimum version).
- `native/tests/load-with-dmd.py <library> <DMD package>/Plugins/<rid>` loads every native library in the DMD package's folder (libusb, libserialport, ZeDMD, Serum, sockpp and the others it ships) and `VpeDof` into one process, in both orders, and calls into each. All of them must load.
- `native/source-archive.py` writes the complete corresponding source of a commit: the repository with the submodules at their pinned revisions, the patches and the build scripts. Licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Using DOF in VPE

DOF runs in the VPE player as a plugin. The [VPE documentation](https://docs.visualpinball.org) (Plugins, DOF) explains how to import a configuration from the [DOF Config Tool](https://configtool.vpuniverse.com) or an existing DOF installation, the device permissions on Linux and macOS, and the settings. In short:

- Download your configuration zip from the Config Tool with your browser and import it on the DOF settings page. The player never contacts the Config Tool itself. An existing configuration folder (DOF on Windows, or VPX's `directoutputconfig`) can be imported as well.
- On Linux, the user running the player needs access to the controllers' `hidraw` and USB devices (a udev rule) and to the serial ports (the `dialout` group). On macOS, only elevated processes can open Pinscape Pico's feedback interface, and HID boards are opened by one program at a time. On Windows, FT245R boards need the WinUSB driver.
- Close other DOF hosts (DOFLinx, a frontend with DOF enabled, VPX) before starting a game: a controller can only be driven by one program at a time.

Table authors add a `DofTriggerComponent` (*DOF Triggers*) to send DOF's numbered (`E`) and named (`$name`) events from the table's switches, coils and lamps, as VPX tables do from their scripts, and set the DOF game name of a table without a ROM, so the Config Tool's rows for it apply. The player tries the ROM first and the declared name after it.

## What the patches change

libdof is used as upstream has it, except for what `native/patches/0001-vpe-libdof.patch` changes:

- **Output gate.** A process-wide gate that turns all outputs off while the game is paused or stopping, whatever effects are running. For each change it reports which controllers have sent it, which failed, and whether a later change replaced it before every controller had it. A stop or a fault latches it closed from any thread, without allocating: every controller's updater sends the closing without DOF's main thread (an idle updater checks the gate every 50 ms, its polling interval, which bounds how long it takes to notice; a PacLed64 or PacUIO drops the rest of an opening's update), and an opening still queued for that thread, or any later one, applies as a closing.
- **File confinement.** libdof reads only files inside the configuration folder it was given, and never writes or deletes files. Its log goes to the host instead of a file.
- **Sessions.** `Setup` fails instead of falling back to defaults when the global config cannot be loaded, and instead of auto-configuring when the configured cabinet config is missing or invalid (without a configured cabinet, auto-configuration works as before). It clears what the previous session left in libdof's singletons, and the Art-Net socket reopens for the next session. `Finish` undoes only what `Init` started, applies changes still queued, finishes every controller even when one fails, and fails when a controller could not turn its outputs off.
- **Controllers.** A controller reports a change as sent only after a complete transfer; failed transfers are reported and retried. PinOne owns its serial port inside the process instead of sharing it through a named pipe or socket. Auto-configuration never opens a serial port: the host leases only the ports the configuration names, so a serial PinOne is used where the cabinet configuration names its port, and the auto-configurator neither probes the legacy PinOne port nor scans the others (on Linux it does not even list them, since listing opens some ports). HID PinOne boards are still found. A few shutdown, locking and buffer-size bugs are fixed.
- **Parity with C# DOF.** Fixes for differences the parity harness found. They are listed in the parity [README](Tools~/parity/README.md#differences-found).

## License

GPLv3 or later, like libdof. See [LICENSE](LICENSE), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the libraries `VpeDof` contains.
