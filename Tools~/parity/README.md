# DOF parity harness

libdof is a port of the C# DirectOutput Framework, and its README says the port is not fully accurate yet. This harness checks it: it drives C# DOF and libdof with the same configuration and the same script of table element changes, has both send their outputs to an Art-Net controller on localhost, and compares the frames.

## Setup

C# DOF runs on .NET Framework, in its own process (`CSharpRunner`). Build it from the [official repository](https://github.com/DirectOutput/DirectOutput) first (Release, x64):

```bash
git clone https://github.com/DirectOutput/DirectOutput
msbuild DirectOutput/DirectOutput/DirectOutput.csproj -t:restore -p:RestorePackagesConfig=true -p:SolutionDir=$PWD/DirectOutput/
msbuild DirectOutput/DirectOutput/DirectOutput.csproj -p:Configuration=Release -p:Platform=x64
```

Then build the runner against its output folder, and the orchestrator, which runs libdof in-process through the bindings (build `VpeDof` first, see the main README):

```bash
dotnet build CSharpRunner -c Release -p:DirectOutputDir=<DirectOutput>/bin/x64/Release
dotnet build DofParity -c Release
```

Rebuild `DofParity` after rebuilding `VpeDof`, since the build copies the native library next to it.

## Running

```bash
dotnet DofParity/bin/Release/net10.0/DofParity.dll --csharp CSharpRunner/bin/Release/net48/CSharpRunner.exe --report parity.md
```

This runs every scenario in `scenarios/`. `--scenarios <folder>` runs another folder.

To compare a real configuration, pass a `directoutputconfig` folder (for example from a Config Tool zip) and a ROM. The harness generates a random script over the table elements that ROM's rows use:

```bash
dotnet DofParity/bin/Release/net10.0/DofParity.dll --csharp ... --config <folder> --rom <rom> --seed 1 --tolerance 45
```

For each run the harness writes a temporary configuration: a global config, the ini files, and a cabinet that maps each ini device to an Art-Net controller on its own universe, so no real device is touched. The exit code is 1 when a sample differs.

## Scenarios

A scenario is a text file (`.dofscript`):

```text
name: inverted solenoid
# The ini columns after the ROM name.
row: S3 Invert,S3
# The largest difference per port still counted as equal.
tolerance: 40
# Type, number, value.
S 3 1
# A named element.
$flipper 1
# Sleep, then take a sample.
wait 150
```

Only a line that starts with `#` is a comment; a `#` elsewhere belongs to the line, as in `I#80` or `#FF8000`. The harness defines the colours Red, Green, Blue, White, Orange, Yellow, Purple and Cyan.

Both engines get 300 ms to settle after `Init`, then a sample is taken after each `wait`. A sample compares 64 ports per device, from the last Art-Net frame each controller sent; a device without a frame counts as a difference. The C# runner waits for the orchestrator to acknowledge each sample before its script goes on.

## Timing

The engines run in different processes with their own timers, so a sample in the middle of a fade can be one fade step apart (255 / (duration / 30 ms)), and a timed effect that ends within a few milliseconds of a sample can be on in one engine and off in the other. Scenarios with fades set a tolerance of one step, and scenarios with blinking take their samples at least 50 ms from an edge. Random runs over real configurations should use the step of the fastest fade they contain (45 for 200 ms fades) and be repeated; a difference that does not come back on a second run is timing. A blink with fades (`Blink fu500 fd550`) restarts its fade at every blink edge, so the two engines' 30 ms fade steps run at different phases: its samples scatter by up to about three steps without drifting (a plain blink on the same element matches every sample), and a given script reproduces the same scatter, so a single difference on such a port at around twice the tolerance is consistent with timing; a dedicated scenario of that effect on its own tells it apart from a real difference.

## Differences found

Against libdof d92f8fc (0.5.4). Each one was fixed in the patch unless noted.

| Difference | Effect | Scenario |
| --- | --- | --- |
| Chained effects shared the table element data instead of copying it. | An inverted effect with a full-range or fade effect in front saw the value the effect after it had written, and stayed on. | 02-invert |
| Conditions (`(S1 and S2)`) were not built. | libdof assigned the effect to a table element named after the column instead of the condition's elements, so conditional columns never fired. | 08-conditions |
| Interval alarms were advanced by position. | A handler that re-registered itself while running was advanced twice, so fades ran at half speed. | 06-fade |
| The Art-Net socket stayed closed after the first session. | A second game sent nothing. | (bindings test) |
| Single-output effects got no layer. | All settings of a column shared layer 0, so turning one setting off turned the port off while another was still on. | 09-multi |
| A new analog layer was opaque (alpha 255) instead of transparent. | Settings that had not fired yet covered the ones below them. | 09-multi |
| Named elements were stored without their name, and looked up with the case they were sent in although names are stored upper case. | `$name` columns and conditions never fired. | 10-named |
| The alpha table was computed in `float`. | 170 came out as 169, 85 as 84. | (Config Tool configuration) |
| `RGBAToy` blended its layers with its own formula and did not clear them on reset. | Colours were off by one, and a reset toy kept its old layers. | (Config Tool configuration) |
| `BL<n>` (blink low value as a number) is ignored by C# DOF: its parser takes the substring after the first character, `L<n>`, which fails. libdof applies it. | Not changed: libdof does what the format describes. | - |

With these fixes all scenarios match, and random runs over a full Config Tool configuration match within one fade step, except for occasional samples on ports with a blink with fades, which scatter by up to about three steps around the same curve (see above); such a difference is consistent with timing scatter, and is checked with a dedicated scenario before it is put down to it.
