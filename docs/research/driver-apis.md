# Driver API research — NVML + NVAPI

This document records the driver interfaces Afterglow uses, where each fact came
from, and what was verified on real hardware (RTX 5090, driver 610.88, NVML 13;
the per-point V/F table on driver 616.56 — see the Blackwell section, which also
keeps the record of a claim this document used to make and that was wrong).
Nothing in the interop layer is based on guesswork: every struct layout and
interface ID below was read from an authoritative source (the `nvml.h` header
shipped in CUDA 13.3, NVIDIA's own `nvidia-ml-py` bindings, or open-source
projects that ship these calls in production), and the self-test verifies each
capability live before the UI exposes it.

## NVML (documented, ships as `System32\nvml.dll`)

Primary sources: `nvml.h` from CUDA 13.3.29 (matches driver 610.88 / NVML 13.3);
NVML API Reference vR610 (docs.nvidia.com/deploy/nvml-api); `nvidia-ml-py`
13.610.43. The PE export table of the target machine's nvml.dll was parsed to
confirm every function below is exported.

Key facts:

- Struct version macro: `version = sizeof(struct) | (ver << 24)`.
  `nvmlClockOffset_v1 = 0x01000018`, `nvmlFanSpeedInfo_v1 = 0x0100000C`,
  `nvmlMemory_v2 = 0x02000028`, `nvmlFieldValue_t` is 40 bytes.
- **Clock offsets** (the modern OC API, R555+): `nvmlDeviceGet/SetClockOffsets`
  with `nvmlClockOffset_v1_t { version; clockType; pstate; clockOffsetMHz;
  minClockOffsetMHz; maxClockOffsetMHz }`. Use `NVML_CLOCK_GRAPHICS (0)` /
  `NVML_CLOCK_MEM (2)` at `NVML_PSTATE_0`. Offsets are effectively global
  (the pstate field is ignored by current drivers). Driver reports the legal
  min/max — Afterglow clamps to it.
- **Locked clocks**: `nvmlDeviceSetGpuLockedClocks(min,max)` /
  `ResetGpuLockedClocks` (Volta+, admin) — the `nvidia-smi -lgc` mechanism, and
  the documented undervolt path when combined with a positive core offset.
  **Do not use `SetMemoryLockedClocks` on Blackwell**: on RTX 5090 it acts as a
  global performance-limit constraint and costs large amounts of performance
  (LACT issue #1128). Memory tuning is offset-only.
- **Power limit**: `nvmlDeviceSetPowerManagementLimit` (mW, admin), constraints
  from `GetPowerManagementLimitConstraints`. `GetPowerUsage` is a ~1 s average
  on Ampere+; field ID `NVML_FI_DEV_POWER_INSTANT (186)` gives instantaneous
  power for graphs.
- **Fans**: `GetNumFans`, `GetFanSpeed_v2` (%), `GetFanSpeedRPM` (R565+,
  `nvmlFanSpeedInfo_v1`), `GetMinMaxFanSpeed` (typ. 30–100 %),
  `Get/SetFanControlPolicy` (0 = auto, 1 = manual), `SetFanSpeed_v2`,
  `SetDefaultFanSpeed_v2`. NVML cannot command 0 % (zero-RPM) — that path uses
  NVAPI cooler control. Windows-GeForce behavior of the setters is probed live
  at startup (documented but rarely field-reported; NVAPI is the fallback).
- **Temperatures**: `GetTemperature(GPU)` only — NVML has **no hot-spot or
  memory-junction sensor** (full-header grep). Ada+ thresholds moved to
  T.Limit-relative field IDs 193–196; `GetMarginTemperature` (R570+) reports
  headroom-to-throttle directly. The acoustic threshold (temp-limit slider) is
  not exposed on Blackwell/Windows (`nvidia-smi` shows Target Temperature N/A) —
  the temp limit uses NVAPI thermal policies instead.
- **Throttling**: `GetCurrentClocksEventReasons` (R535+) with the documented
  bitmask (SwPowerCap 0x4, HwSlowdown 0x8, SwThermalSlowdown 0x20 …); legacy
  export kept as fallback.
- **Per-process VRAM**: NVML always reports "not available" under WDDM (header
  states Windows KMD owns memory) — per-process VRAM comes from Windows GPU
  performance counters instead.
- Thread-safe per the header; `GetPcieThroughput` blocks ~20 ms by design
  (sampled sparsely, off the UI thread; field IDs 197/198 are the counter
  alternative).
- Errors: `NotSupported=3`, `NoPermission=4` (all setters need admin),
  `ArgumentVersionMismatch=25` (bad struct version).

## NVAPI (`nvapi64.dll`, via the exported `nvapi_QueryInterface(uint id)`)

The public NVAPI SDK documents only part of the surface; tuning tools use
additional interfaces whose IDs and layouts are published by multiple
open-source projects. Afterglow only ships constants read from these
production sources (cross-checked where they overlap, and probed read-only at
runtime before use):

- LibreHardwareMonitor `LibreHardwareMonitorLib/Interop/NvApi.cs` +
  `Hardware/Gpu/NvidiaGpu.cs` (MPL-2.0) — monitoring set, exact struct
  layouts (Pack=8) and struct versions, RTX 40/50 thermal channel mapping.
- NvAPIWrapper `NvAPIWrapper/Native/Helpers/FunctionId.cs` + structure files
  (MIT) — overclocking set (pstates20, power/thermal policies, boost table).
- Demion/nvapioc — the clock-boost mask / VFP-curve / clock-boost-table
  layouts and the call sequence the per-point V/F editor ships (mask → curve →
  table, 255 uniform slots). The delta encoding was then calibrated on our own
  hardware rather than taken from it (see the Blackwell section).

Interface IDs (all verified in the sources above; version = `sizeof | ver<<16`):

| Function | ID | Struct / notes |
|---|---|---|
| NvAPI_Initialize | 0x0150E828 | — |
| NvAPI_EnumPhysicalGPUs | 0xE5AC921F | handles[64] |
| NvAPI_GPU_GetFullName | 0xCEEE8E9F | char[64] |
| NvAPI_GPU_GetBusId | 0x1BE0B8E5 | — |
| NvAPI_GPU_GetTachReading | 0x5F608315 | primary fan RPM |
| NvAPI_GPU_GetThermalSettings | 0xE3640A56 | v2, 3 sensors {controller, defMin, defMax, current, target} |
| NvAPI_GPU_GetThermalSensors (private) | 0x65FE3AAD | v2 {version, mask, int[8], int[32] temps ×256}. Mask probed bit-by-bit. Channel map (LHM): RTX 50 → [1]=GPU, [2]=memory junction, hot spot unavailable; RTX 40 → [1]=hot spot, [7]=memory junction; older → [1]=hot spot, [9]=memory junction |
| NvAPI_GPU_GetDynamicPstatesInfoEx | 0x60DED2ED | v1, 8 domains (GPU/FB/VID/BUS utilization) |
| NvAPI_GPU_GetAllClockFrequencies | 0xDCB616C3 | v2/v3 probed; domains: Graphics=0, Memory=4, Video=8; kHz |
| NvAPI_GPU_ClientFanCoolersGetStatus | 0x35AED5E8 | v1, 32 × {coolerId, rpm, minLevel, maxLevel, level} |
| NvAPI_GPU_ClientFanCoolersGetControl | 0x814B209F | v1, 32 × {coolerId, level, mode(0 auto/1 manual)} |
| NvAPI_GPU_ClientFanCoolersSetControl | 0xA58971A5 | same struct — per-fan manual duty incl. 0 % |
| NvAPI_GPU_RestoreCoolerSettings | 0x8F6ED0FB | restore firmware fan control |
| NvAPI_GPU_GetCoolerSettings / SetCoolerLevels (legacy) | 0xDA141340 / 0x891FA0AE | v2 / v1, pre-FanCoolers fallback |
| NvAPI_GPU_ClientVoltRailsGetStatus | 0x465F9BCF | v1, size 0x4C, core µV at offset 0x28 |
| NvAPI_GPU_ClientPowerPoliciesGetInfo | 0x34206D86 | v1: {ver, valid u8, count u8, 4×{stateId, 2×unk, min, 2×unk, def, 2×unk, max, unk}} in per-cent-mille (100000 = 100 %) |
| NvAPI_GPU_ClientPowerPoliciesGetStatus | 0x70916171 | v1: {ver, count, 4×{stateId, unk, targetPCM, unk}} |
| NvAPI_GPU_ClientPowerPoliciesSetStatus | 0xAD95F5ED | same struct |
| NvAPI_GPU_ClientThermalPoliciesGetInfo | 0x0D258BB5 | v2: {ver, count u8, unk u8, 4×{controller, unk, min, def, max, unk}} temps ×256 |
| NvAPI_GPU_ClientThermalPoliciesGetStatus | 0xE9C425A1 | v2: {ver, count, 4×{controller, target ×256, stateId}} |
| NvAPI_GPU_ClientThermalPoliciesSetStatus | 0x34C0B13D | same struct — the temp-limit slider |
| NvAPI_GPU_ClientPowerTopologyGetStatus | 0xEDCF624E | v1, 4 × {domain (0 GPU/1 board), rsvd, power PCM, rsvd} |
| NvAPI_GPU_GetPstates20 / SetPstates20 | 0x6FF81213 / 0x0F4DAE6B | v2: 16 pstates × (8 clock entries {domain, type, flags, deltaKHz{val,min,max}, union single/range} + 4 base voltages {domain, flags, µV, delta}) + overvolt tail |
| NvAPI_GPU_GetCoreVoltageBoostPercent / Set | 0x9DF23CA1 / 0xB9306D9B | v1 {ver, percent, u32[8]} |
| NvAPI_GPU_GetClockBoostMask | 0x507B4B59 | v1, 6188 B: {ver, mask[32], unk[32], 255 × {clockType, enabled u8, unk[19]}} — which slots are live (nvapioc) |
| NvAPI_GPU_GetVFPCurve | 0x21537AD4 | v1, 7208 B: {ver, mask[32], unk[32], 255 × {clockType (0 core / 1 memory), freq kHz, µV, unk[16]}} — each slot's stored point (nvapioc). *Retracted row: this table used to say "masks[4], u32[12], 80 GPU + 23 memory entries" — that split read the same 7208-byte wire format with a misinterpreted tail and never worked; see below.* |
| NvAPI_GPU_GetClockBoostTable / SetClockBoostTable | 0x23F1B133 / 0x0733E009 | v1, 9248 B: {ver, mask[32], unk[32], 255 × {clockType, unk[16], frequency delta, unk[12]}} — per-point V/F offsets; read **and write** verified on RTX 5090 / 616.56 (nvapioc layout; delta calibrated as plain kHz, see below). Formerly tagged "(pre-Blackwell)" — retracted |
| NvAPI_GPU_GetClockBoostRanges / Lock / SetLock | 0x64B43A6A / 0xE440B867 / 0x39442CFB | rest of the curve-editing suite — recorded only; Afterglow does not call these. Formerly tagged "(pre-Blackwell)" — retracted, untested either way |
| NvAPI_GPU_GetPerfDecreaseInfo | 0x7F7F4600 | throttle flags (NVAPI view) |

NvStatus: OK=0, NotSupported=-104, IncompatibleStructVersion=-9,
FunctionNotFound=-136. `NvAPI_ShortString` = char[64].

### Blackwell (RTX 50) reality, reflected in Afterglow's capability gating

- **Hot spot**: removed from every public API by NVIDIA (Jan 2025). Tools that
  show it on RTX 50 (HWiNFO 8.52+, LHM) use low-level register access via their
  own kernel components. Afterglow deliberately ships **no kernel driver**
  (that's a trust feature — Afterburner's RTCore64.sys, GPU-Z's driver, and
  Gigabyte's tooling all have CVE history), so on RTX 50 hot spot is shown as
  "blocked by NVIDIA for this generation" rather than a fake value.
  Memory junction *is* readable (thermal sensors channel 2) and drives the
  fan-curve "memory" temp source.
- **Per-point V/F table** (`Get/SetClockBoostTable` with the mask and VFP
  curve above): **read and write work on Blackwell.** Measured on RTX 5090 /
  driver 616.56 (2026-08-30) with the nvapioc layouts: 127 live core points;
  per-point offsets written and read back; and the shared-table behaviour the
  editor is built around —
  1. NVML's global core offset lives in the same table: with a global +100 MHz
     applied and no table writes, every live slot read +100. (Re-observed
     read-only on driver 617.42, 2026-10-12: `get` reports a +260 MHz core
     offset and `vfpoints` lists all 127 points at +260.)
  2. A core-offset write rewrites every live slot uniformly, so it erases
     per-point shape by itself (slot 68 set to −50 became +100 the moment
     `set --core-offset 100` was applied).
  3. Clearing the point offsets left the global core offset in place: the
     table still read +100 on every point afterwards.
  4. The delta is plain kHz (`TableDeltaPerMHz = 1000`), calibrated against
     that known global offset; nvapioc encodes kHz × 2 for the generations it
     was written against, so 20/30/40-series reports of offsets reading at
     half or double the applied global offset would pin a generation split.

  Points 2–4 are single-driver observations: the apply path does not depend on
  them (it writes the core offset again after any clear it performs, and only
  removes a curve for a profile that recorded a table reading), and every
  write and clear is read back and reported against what was asked. Afterglow
  ships the editor (V/F Curve page, `afterglow-cli vfpoints`) gated on a live
  probe — the table must read back with points — never on GPU generation.
  Lock-and-offset (documented NVML APIs) remains the second undervolt path.
- **Retracted — kept as the record of what this document used to say.** Until
  1.2.0-beta.3 this section stated: *"V/F curve writes (`SetClockBoostTable` /
  boost locks): not accepted on Blackwell (driver rejects; also reported by
  press covering NV-UV)"*, tagged the boost-table and curve-editing interfaces
  "(pre-Blackwell)", tabled `GetVFPCurve` as "80 GPU + 23 memory entries", and
  called the per-point editor "roadmap, not shipped … pre-Blackwell-only".
  All of it traced to our own struct: the 80 + 23 split misread the 7208-byte
  wire format, so the calls failed on our side. Found and fixed in
  1.2.0-beta.3 (CHANGELOG); with the corrected layout read and write work on
  616.56. To be exact about the limits of that: the original failure was seen
  on 610.88 and that driver was not re-run with the fixed struct — the broken
  layout is a sufficient cause, and nothing points at the driver. The press
  article that was cited for the "restriction" is no longer cited for anything
  here: the restriction it was used to support did not exist on the driver we
  tested.
- **Voltage**: reference Blackwell exposes only NVIDIA's small core-voltage
  boost (~20 mV class); vendor-specific unlocks (MSI) go through PWM ICs that
  NVIDIA has been blacklisting in drivers. Afterglow exposes the boost-percent
  control only when the driver accepts it, and reads actual core µV via
  VoltRails for display.

## Sources

- NVML API reference: https://docs.nvidia.com/deploy/nvml-api/
- nvml.h, CUDA 13.3.29 redistributable (cuda_nvml_dev-windows-x86_64-13.3.29)
- nvidia-ml-py 13.610.43: https://pypi.org/project/nvidia-ml-py/
- LibreHardwareMonitor (MPL-2.0): https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- NvAPIWrapper (MIT): https://github.com/falahati/NvAPIWrapper
- LACT #1128 (Blackwell memory-lock regression): https://github.com/ilya-zlobintsev/LACT/issues/1128
- NVML clock-offset pstate behavior: https://forums.developer.nvidia.com/t/318332
- Hot-spot removal on RTX 50: https://videocardz.com/pixel/nvidia-has-removed-hot-spot-sensor-data-from-geforce-rtx-50-gpus,
  https://www.techpowerup.com/350705/
- Clock-boost mask / VFP curve / clock-boost table layouts and call sequence:
  Demion/nvapioc, https://github.com/Demion/nvapioc
- ~~Blackwell V/F write restriction: videocardz, NV-UV article~~ — retracted;
  see "Retracted" in the Blackwell section. The link
  (https://videocardz.com/newz/nv-uv-brings-one-click-undervolting-to-geforce-rtx-50-gpus)
  is kept only so the old citation can be traced.
- Voltage-controller blacklisting: https://www.techspot.com/news/109434-msi-afterburner-update-unlock-additional-voltage-controls-but.html
