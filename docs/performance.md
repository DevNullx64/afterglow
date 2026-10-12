# Performance: what Afterglow costs to keep running, and how it was measured

Afterglow is a resident app: the logon task starts it hidden in the tray, and it
polls the GPU once a second for days. What matters most is therefore not how
fast it starts but what it costs while nobody is looking at it. This page records
the measurements behind the October 2026 optimization pass, the method, and the limits of
what the numbers mean.

## Method

All numbers come from `tools/measure-perf.ps1` and `tools/measure-startup.ps1` on
the development machine (RTX 5090, driver 617.42, Windows 11 26200, AMD AM5, 4K
display). Every run:

- uses a fresh `--data-dir`, so the installed copy's settings, profiles and flight
  recordings are never read or rotated, and every build starts from the same
  empty state;
- runs unelevated (`--no-elevate`): monitoring only, no GPU writes;
- measures the **published** single-file build, the way users run it, not the
  framework-dependent `bin\` output.

Steady-state metrics wait **90 s** before a **120 s** measurement window. A short
warm-up measures the JIT instead of the app: methods called once per telemetry
tick are recompiled at tier 1 only after ~30–60 calls, and with tiered PGO some go
through an extra instrumented tier, so the first minute is dominated by
compilation bursts (seen directly in the `dotnet.jit.compiled_methods` counter).
Even after 90 s some tier-up remains in the window, so the steady-state CPU
figures below are slightly pessimistic — equally so for every build compared.

| Metric | Meaning |
|---|---|
| CPU % of one core | process `TotalProcessorTime` delta ÷ wall time over the window |
| Working set / private | `WorkingSet64` / `PrivateMemorySize64` at the end of the window |
| Alloc KB/s | managed allocation rate (`dotnet.gc.heap.total_allocated`, dotnet-counters) |
| Startup CPU | CPU consumed during the 90 s warm-up (startup + JIT) |
| Time to window | launch → main window handle exists (window created and shown) |

Scenarios:

- **tray** — started with `--minimized`, the way the logon task runs it;
- **minimized** — shown on the dashboard, then minimized after 5 s;
- **visible** — the dashboard on screen, every graph redrawing each second.

Each figure is the median of the runs listed. CPU readings on Windows are
quantized to the 15.6 ms scheduler tick, so differences of a few hundredths of a
percent are noise; memory and allocation figures are stable to within a few
percent run to run.

## Results

**Baseline** is 1.2.0-beta.4 plus only the `--data-dir` option (so it could be measured in
isolation), published exactly as 1.2.0-beta.4 shipped: two compressed single-file
executables. **After** is this branch, published by `tools/publish.ps1`. The three-scenario
runs used the build at commit `cd8ef23`; the startup and long tray runs used `57bf44f`
(Mica off by default). Commits between the two touched nothing these runs exercise.

### Resident cost (median of 3; 90 s warm-up, 120 s window)

| Scenario | Metric | Before | After | Change |
|---|---|---:|---:|---:|
| **Tray** (logon default) | CPU, % of one core | 0.651 | 0.456 | −30 % |
| | Working set, MiB | 286 | 137 | −52 % |
| | Private, MiB | 186 | 121 | −35 % |
| | Managed allocation, KiB/s | 49.1 | 26.8 | −45 % |
| | Threads | 18 | 13 | −5 |
| | CPU during the 90 s warm-up, ms | 1,438 | 984 | −32 % |
| **Minimized** | CPU, % of one core | 0.833 | 0.469 | −44 % |
| | Working set, MiB | 351 | 160 | −55 % |
| | Private, MiB | 232 | 136 | −42 % |
| | Managed allocation, KiB/s | 351.5 | 26.7 | −92 % |
| **Visible** (dashboard on screen) | CPU, % of one core | 1.393 | 1.354 | −3 % (noise) |
| | Working set, MiB | 353 | 207 | −41 % |
| | Private, MiB | 233 | 154 | −34 % |
| | Managed allocation, KiB/s | 342 | 341 | none (see below) |

With the opt-in Mica backdrop (`--mica`) the visible scenario measured 1.536 % CPU and
164 MiB private (runs 1.536 / 1.484 / 1.706 % against 1.328 / 1.354 / 1.354 % without):
WPF renders a translucent window with per-pixel alpha, and behind Afterglow's near-opaque
palette the effect was barely visible — which is why it is off by default.

### Startup (median of 10 warm launches after one discarded first launch)

| | Before | After | Change |
|---|---:|---:|---:|
| GUI: launch → main window shown | 805 ms | 548 ms | −32 % |
| CLI: `afterglow-cli get --json` launch → exit (includes NVML init) | 171 ms | 124 ms | −27 % |

### Download and disk

| | Before | After |
|---|---:|---:|
| Portable zip | 103,659,846 B | 61,978,094 B (−40 %) |
| Installed files | 7 files, 110.2 MiB | 261 files, 136.5 MiB |

### Where the gains came from

Packaging and code were measured separately (tray scenario, two runs each, same code
except the first row):

| Build | CPU % | Working set MiB | Private MiB | Alloc KiB/s | Warm-up CPU ms |
|---|---:|---:|---:|---:|---:|
| Baseline (old code, compressed single-file) | 0.651 | 286 | 186 | 49.1 | 1,438 |
| New code, same compressed single-file | 0.625 | 233 | 178 | 26.9 | 1,102 |
| New code, uncompressed single-file | 0.469 | 136 | 119 | 26.9 | 1,063 |
| … + ReadyToRun | 0.378 | 140 | 121 | 26.8 | 891 |
| … + English-only resources | 0.476 | 142 | 122 | 26.8 | 969 |
| Shipped: shared-runtime folder, ReadyToRun, English | 0.521 | 137 | 119 | 26.8 | 906 / 2,125 * |

\* the second run overlapped a UI walkthrough and builds on the same machine; the first is
representative. CPU differences between the last four rows are within run-to-run noise.

So the code changes alone cut allocation by 45 % and working set by ~50 MiB (WinForms no
longer loaded, less retained state); dropping single-file **compression** is the largest
single memory win (~97 MiB working set): a compressed bundle decompresses every assembly
into private memory at each launch, while uncompressed files are memory-mapped and shared.
ReadyToRun mainly trims startup JIT.

### Interop hot path

A micro-benchmark of `SensorPoller.Poll()` on the RTX 5090 (200 polls after 30 warm-up,
`GC.GetAllocatedBytesForCurrentThread`, four interleaved runs per build): **1,864 → 464 bytes
per poll**, the remainder being the result objects themselves (snapshot 312 B, NVAPI
enrichment 72 B, two 3-element arrays). Time per poll was unchanged at ~1.6 ms — it is
driver time.

### What these numbers do not show

- **The capped dashboard copy.** The visible-scenario allocation is unchanged because a
  3.5-minute run never fills the history: the dashboard used to copy the whole hour on every
  tick and now copies the 600 samples its longest graph draws. Once an hour of history
  exists the old path copies 3,600 × (8-byte reference + 9 series × 8 bytes + 16-byte
  timestamp) = 345,600 bytes per tick (~338 KiB/s at 1 Hz); the new one 600 × 96 =
  57,600 bytes (~56 KiB/s). *Computed from the array sizes, not measured.*
- **The flight-log startup read.** Each run starts from an empty data folder, so the old
  `ReadAllLines` of the previous session's log (26 MB on the development machine after a
  multi-day session) is not in these numbers. The new code reads its last 4 KB.
- **One machine.** Everything above is one RTX 5090 desktop; absolute figures will differ
  elsewhere, the direction should not.
- **CPU resolution.** Windows charges CPU time in 15.6 ms ticks; at ~0.5 % of a core over
  120 s that is ±0.013 percentage points per reading, plus run-to-run variance of about
  ±0.1 pp seen above.

### True steady state (one run each; 5 min warm-up, 5 min window, no counters attached)

The 90 s / 120 s runs above still include the tail of tiered JIT compilation and the
dotnet-counters session's own overhead. Waiting five minutes and measuring five without
counters gives the figure that matters for an app that stays resident for days:

| Tray | Before | After |
|---|---:|---:|
| CPU, % of one core | 0.339 | 0.318 |
| Working set, MiB | 295 | 131 |
| Private, MiB | 199 | 116 |
| Threads | 17 | 11 |
| CPU in the first 5 minutes, ms | 2,031 | 1,531 |

**Steady-state CPU did not meaningfully change** — it is dominated by the driver calls of
the one-per-second sensor poll (~1.6 ms of driver time per poll on this card), which this
work deliberately left alone: every sensor feeds something that runs while hidden (fan
curve, flight recorder, automation, alerts, the history behind the graphs). The 0.65 →
0.46 % drop in the shorter runs is mostly less work during the JIT tail. What did change
for good: memory (−56 % working set, −42 % private), threads, and startup/warm-up CPU.

### After merging the Intel Arc line (A/B, same session)

1.5 also carries the Intel Arc support from the 1.3 betas, which probes for Intel's
IGCL and Level Zero libraries at startup. To check that cost, the last pre-merge build
(57bf44f) and the merged release build (3792cd4) were measured back to back on the RTX 5090
machine, which has no Intel GPU: two alternating tray runs each (90 s warm-up, 120 s
window), then 11 launches each for startup.

| | Pre-merge | Merged |
|---|---:|---:|
| Tray working set, MiB (two runs) | 136.1 · 134.1 | 134.2 · 136.6 |
| Tray private, MiB | 119.5 · 117.2 | 117.3 · 120.4 |
| Tray allocation, KiB/s | 26.8 · 26.8 | 26.8 · 26.8 |
| Tray CPU, % of one core | 0.677 · 0.326 | 0.625 · 0.352 |
| GUI launch → window shown, median ms | 540 | 545 |
| CLI `get --json` launch → exit, median ms | 114 | 122 |

Memory and allocation are unchanged. CPU moved by a factor of two between runs of the
*same* build (other activity on the machine), so the merge's effect on it is below what
these runs can resolve. Startup costs a few milliseconds more, within the spread of the
individual launches; the published 548 ms and 124 ms still describe the release.
