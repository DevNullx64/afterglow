# Hardware verification — RTX 5090, driver 610.88

Every claim below was executed on real hardware (NVIDIA GeForce RTX 5090, GeForce driver
610.88 / NVML 13.610.88, Windows 11 Pro, 2026-08-25) during development. Transcripts are
reproduced verbatim from the test logs; anyone with the hardware can reproduce them with
the listed commands. Section 7 (the per-point V/F table) was measured later, on driver
616.56, and corrects a claim sections 5 and 6 originally made — the original wording is
kept, struck through and marked retracted, rather than deleted.

## 1. Read path — `afterglow-cli selftest` (unelevated)

Abridged to the capability-relevant lines; every listed value returned `OK` live:

```text
NVML initialized. Driver 610.88, NVML 13.610.88
--- GPU 0: NVIDIA GeForce RTX 5090 ---
  Architecture               OK    10            (Blackwell)
  Temp GPU                   OK    50 C
  TempThreshold Shutdown     OK    96 C   | Slowdown 93 C | GpuMax 90 C
  TempThreshold Acoustic*    [NotSupported]      (expected on Blackwell — see notes)
  Clock Graphics/Mem/Video   OK    (idle: 900 / 7001 / 1252 MHz; max 3090 / 14001)
  Utilization / Encoder / Decoder / Memory / BAR1        OK
  Power usage 50.8 W | limit 575 W | constraints 400..575 W | default 575 W
  Perf state P3 | Throttle reasons: None
  Fans: 3 | per-fan speed + policy OK | min/max 30..100%
  PCIe Gen5 x16 | TX/RX throughput OK | busId 00000000:01:00.0
  Core offset                OK    0 MHz (range -1000..1000)
  Mem offset                 OK    0 MHz (range -2000..6000)
  Throttle margin            OK    38 C headroom
  Fan[0..2] RPM              OK    (0 RPM in zero-RPM mode) | Fan target 30%
  Field values               OK    power instant 77632 mW; TLimit shutdown/slowdown = -5/-2
=== NVAPI (read-only probe) ===
  Hot spot                   [unavailable]       (NVIDIA blocks it on RTX 50 — shown honestly)
  Memory junction            OK    62.0 C        (thermal-sensors channel 2)
  Core voltage               OK    960.0 mV
  Voltage boost              OK    0%
  Util domains               OK    gpu/fb/vid/bus
  Fan coolers                OK    3 fans, level range 30..100%
```

## 2. Write path — elevated apply/revert round-trip

Sequence executed elevated via `afterglow-cli set/reset`; every knob returned `ok` and the
system was left at driver defaults:

```text
[1] no-op offsets (0/0) + power limit 575 W          → ok power limit / ok memory offset / ok core offset
[2] voltage boost 0%                                  → ok
[3] fans fixed 40% for 6 s                            → ok; live readback: fans 41%/42%/42% (spun up from 0%)
[4] fans back to auto                                 → ok (returned to zero-RPM)
[5] clock lock 210..2500 MHz, then unlock             → ok / ok
[6] core offset +15 MHz                               → ok; `get` shows Core offset 15 MHz; reverted to 0 → ok
[7] reset to defaults (all knobs)                     → ok core offset / ok memory offset / ok clock lock
                                                        / ok power limit / ok voltage boost / ok fans
```

Notes: the offset readback in [6] is the driver reporting the applied value back, not an
echo of the request. The apply engine performs this verification on every offset write.

## 3. FPS capture — elevated ETW round-trip, including a real game

`afterglow-cli fps --seconds 10` while an actual game session (League of Legends) was
running alongside desktop apps and the `--present-storm` test window:

```text
Capture state: Running, stdout lines: 4649, parse errors: 0, header parsed: True
Presenting apps seen: 4
  League of Legends.exe     168.3 fps  P1  68.4  1%low  65.4  ft 5.94 ms  [Hardware: Independent Flip]
  Afterglow.exe             239.3 fps  P1 201.2  1%low 145.2  ft 4.18 ms  [Composed: Copy with GPU GDI]
  (two desktop apps elided) ~240 fps at the display refresh rate
Auto-selected target: League of Legends.exe
cli exit code: 0; leftover processes: none
```

Highlights: a real game measured in Independent Flip with plausible in-game statistics;
automatic target selection picked the game over three desktop apps; capture start, parse,
statistics, and child-process teardown all verified clean. The 2.5.1 console app emits the
`TimeInMs` schema; Afterglow's header-driven parser accepts both `TimeInMs` and the
documented `TimeInSeconds`, which these runs exercised.

## 4. Stress engine — live burns on the RTX 5090

The burn workload was developed against measured board power. The original ALU-only
kernel drew ~390 W (68% of the 575 W limit, memory controller idle). The shipping
workload adds a 512 MiB cache-defeating streamed working set, four independent FMA
chains, and a concurrent integer chain on the INT pipe:

```text
afterglow-cli stress --seconds 28 --intensity 8192, sampled live:
  core 3082 MHz | mem ctrl 56% | 99% GPU load | 548.8 W / 575 W (95% of TGP) | P0
Result: Stopped after 00:00:28, 3941 dispatches, 0 errors.
```

Verified live: D3D11 device + compute pipeline creation, sustained near-TGP load with the
power limiter engaging (core easing off max boost — the correct burn-test behavior),
periodic bit-exact readback comparisons (all matched), and clean stop/teardown. Pure
compute cannot exercise the raster/ROP power domains, so a few percent below TGP is the
honest ceiling for an anticheat-safe, non-graphical burn.

## 5. V/F curve probe — measured on the RTX 5090

> **Retracted (1.2.0-beta.3).** This section originally opened: ~~"NVIDIA removed the curve
> query interface and rejects per-point curve writes on RTX 50 (both verified: the private
> VFP-curve read returns unavailable on driver 610.88)."~~ The "unavailable" result came from
> Afterglow's own struct layout (an 80 + 23 entry split that misread the 7208-byte wire
> format), not from the driver. With the corrected layout the curve read and the per-point
> table read **and write** work on RTX 5090 / driver 616.56 — see section 7. 610.88 itself
> was not re-run with the fixed struct.

Independently of the stored table, Afterglow maps the curve the GPU actually runs by
measurement: `afterglow-cli vfcurve --probe` locks the core clock at each step under a
compute load and records the voltage the driver selects, then restores the previous state.
Full sweep, ~80 s, run elevated:

```text
step  1/18: lock   600 MHz ->   592 MHz @  800.0 mV     (voltage floor)
step  8/18: lock  1650 MHz ->  1642 MHz @  865.0 mV
step 12/18: lock  2250 MHz ->  2242 MHz @  910.0 mV
step 15/18: lock  2700 MHz ->  2692 MHz @  940.0 mV
step 16/18: lock  2850 MHz ->  2842 MHz @  985.0 mV     (the knee begins)
step 17/18: lock  3000 MHz ->  2992 MHz @ 1040.0 mV
step 18/18: lock  3090 MHz ->  3080 MHz @ 1090.0 mV
17 voltage points recorded; post-probe state: clock lock restored to none.
```

A classic Blackwell curve: ~10–15 mV per 150 MHz until ~2700 MHz, then ~50 mV per
150 MHz — the knee that makes undervolting worthwhile. The V/F Curve page renders this
measured map, and picking a point computes the lock+offset pair that holds that clock at
that voltage.

## 6. Known Blackwell gaps (verified, by design)

- Hot spot: blocked by NVIDIA in all public APIs on RTX 50 — Afterglow reports
  "unavailable" instead of a fake value. (Kernel-register workarounds exist in other tools;
  Afterglow ships no kernel driver as a matter of policy.)
- NVML acoustic temperature thresholds: `NotSupported` on this driver — the temp-limit
  slider is therefore gated off with an in-app explanation, and the NVAPI thermal-policy
  probe returns no entries on 610.88 (tracked for a future driver re-probe).
- `nvmlDeviceSetMemoryLockedClocks` is intentionally never used: on Blackwell it acts as a
  global performance cap (documented upstream in LACT issue #1128). Memory tuning is
  offset-only.
- ~~Per-point V/F curve writes (`SetClockBoostTable`) are rejected by the driver on RTX 50;
  undervolting uses the supported clock-lock + offset method.~~ **Retracted** — not a
  Blackwell gap: a struct-layout bug on Afterglow's side. Per-point writes work on driver
  616.56 (section 7); clock-lock + offset remains available as the second undervolt path.

## 7. Per-point V/F table — RTX 5090, driver 616.56

Measured 2026-08-30 with the corrected (nvapioc) layouts. The sequence, elevated: read the
table → `set --core-offset 60` → read → `vfpoints --set "68=-50"` → read →
`set --core-offset 100` → read → `vfpoints --clear` → read. The transcript was not kept
verbatim; these are the recorded results:

- The table exposes **127 live core points**; reading and writing both work.
- **The global core offset shares the table.** With a global offset applied and no table
  writes, every live slot reads that offset (+100 MHz read as raw 100000, i.e. plain kHz —
  the delta scale Afterglow uses, `TableDeltaPerMHz = 1000`).
- **A per-point write lands:** slot 68 was set to −50 —
- **— and a core-offset write rewrites every live slot uniformly:** slot 68 became +100 the
  moment `set --core-offset 100` was applied, erasing the per-point edit.
- **Clearing the point offsets left the global offset in place:** after `vfpoints --clear`
  the table still read +100 on every point. (An earlier README/in-app note said a clear also
  returns the core offset to 0; that was inferred, never measured, and is wrong here.)

Read-only re-check, driver 617.42, 2026-10-12 (`--data-dir` to a scratch folder; reproduce
with `afterglow-cli get` and `afterglow-cli vfpoints`): `get` reports a +260 MHz core
offset, and `vfpoints` lists 127 points (450 mV → 435 MHz … 1240 mV → 3412 MHz stored),
every one at +260 MHz — the shared-table behaviour again, on a newer driver.

These are single-card, single-driver observations. Afterglow's apply path does not rely on
the clear-and-core-offset behaviour (it writes the core offset again after any clear it
performs), and every per-point write and every clear is now read back and reported against
what was asked rather than assumed (fixes for review findings F-06/F-10/F-57/F-58).
