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

On Windows, with Visual Studio 2022 and CMake 3.24 or newer, build the native library from a command prompt or PowerShell. This also runs the native smoke test and copies `VpeDof.dll` to `VisualPinball.Engine.DOF.Unity/Plugins/win-x64`:

```bash
native\build-windows.cmd
```

On Linux and macOS, configure and build with CMake directly:

```bash
cmake -S native -B build/<rid> -DCMAKE_BUILD_TYPE=Release -DVPE_DOF_OUTPUT_DIR=$PWD/VisualPinball.Engine.DOF.Unity/Plugins/<rid>
cmake --build build/<rid> --parallel
```

Then build the managed library, which copies itself to `Plugins/Managed`, and run the tests:

```bash
dotnet build VisualPinball.Engine.DOF -c Release
dotnet test VisualPinball.Engine.DOF.Test -c Release
```

The tests listen on UDP port 6454 on 127.0.0.1 and never open real devices.

## What the patches change

libdof is used as upstream has it, except for what `native/patches/0001-vpe-libdof.patch` changes:

- **Output gate.** A process-wide gate that turns all outputs off while the game is paused or stopping, whatever effects are running. For each change it reports which controllers have sent it, which failed, and whether a later change replaced it before every controller had it.
- **File confinement.** libdof reads only files inside the configuration folder it was given, and never writes or deletes files. Its log goes to the host instead of a file.
- **Sessions.** `Setup` fails instead of falling back to defaults when the global config cannot be loaded, and instead of auto-configuring when the configured cabinet config is missing or invalid (without a configured cabinet, auto-configuration works as before). It clears what the previous session left in libdof's singletons, and the Art-Net socket reopens for the next session. `Finish` undoes only what `Init` started, applies changes still queued, finishes every controller even when one fails, and fails when a controller could not turn its outputs off.
- **Controllers.** A controller reports a change as sent only after a complete transfer; failed transfers are reported and retried. PinOne owns its serial port inside the process instead of sharing it through a named pipe or socket. A few shutdown, locking and buffer-size bugs are fixed.
- **Parity with C# DOF.** Fixes for differences the parity harness found. They are listed in the parity [README](Tools~/parity/README.md#differences-found).

## License

GPLv3 or later, like libdof. See [LICENSE](LICENSE).
