# Third-party components

## Intel PresentMon (console application)

- File: `ThirdParty/PresentMon/PresentMon-2.5.1-x64.exe`
- Version: 2.5.1 (pinned)
- Source: https://github.com/GameTechDev/PresentMon/releases/tag/v2.5.1
- SHA-256: `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`
- License: MIT (Intel Corporation) — see `ThirdParty/PresentMon/LICENSE.txt`
- Used for: FPS/frametime capture via Windows ETW present tracing (spawned as a
  child process with `--output_stdout`; no code is linked).

CI verifies the hash above before packaging a release.

## NuGet packages (shipped in release builds)

- CommunityToolkit.Mvvm (MIT) — MVVM source generators for the WPF app.
- Vortice.Direct3D11, Vortice.D3DCompiler, SharpGen.Runtime (MIT, Amer Koleci &
  contributors) — Direct3D 11 bindings used by the stress test's compute workload.
- System.Diagnostics.EventLog (MIT, .NET Foundation) — Windows event-log watcher
  for TDR detection.

Test-only: xunit, Microsoft.NET.Test.Sdk, coverlet.

## Knowledge provenance (no code copied)

The NVAPI interface IDs and the general-purpose structure layouts used by
`Afterglow.Core` are constants published by open-source projects
(LibreHardwareMonitor, MPL-2.0; NvAPIWrapper, MIT) and were re-declared in this
codebase from those references — see `docs/research/driver-apis.md` for the
provenance table.

The **per-point V/F clock-boost** layouts specifically — the `NvClockMasks`,
`NvVfpCurve` and `NvClockTable` structures and the Get/Set call sequence behind
the per-point curve editor — were derived from the field-proven layouts in
**nvapioc by Demion** (<https://github.com/Demion/nvapioc>), as the source
comment at `src/Afterglow.Core/Interop/Nvapi/NvapiNative.cs` records; the delta
scale was then calibrated live against a known global offset on RTX 5090. No
code is copied from nvapioc — only the wire layouts and call order, which are
functional interface facts — and Afterglow re-declares them independently. Its
license could not be confirmed from the information available offline in this
repository; it is credited here as the reference these layouts were derived
from, and its license is **unconfirmed**. (The earlier, broken 80+23 layout
Afterglow once used — the one behind the retracted "RTX 50 blocks the curve
interfaces" claim — was the NvAPIWrapper/LHM-style layout, not nvapioc's.)

The IGCL and Level Zero Sysman structure layouts in
`Afterglow.Core/Interop/Igcl` and `Afterglow.Core/Interop/LevelZero` were
transcribed field-for-field from Intel's own published headers — `igcl_api.h`
(Intel Graphics Control Library, MIT) and `zes_api.h` (oneAPI Level Zero, MIT) —
and are pinned by unit tests against compiler record-layout dumps of those
headers. No Intel code is copied or linked; both runtime libraries
(`ControlLib.dll`, `ze_loader.dll`) ship with the Intel graphics driver and are
loaded from System32 at run time. See `docs/research/intel-driver-apis.md` for
the per-interface provenance table and the dump procedure.
