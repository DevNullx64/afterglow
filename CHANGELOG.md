# Changelog

## Unreleased

### Performance — measured, not estimated ([docs/performance.md](docs/performance.md))

- Resident in the tray (the logon default), against 1.2.0-beta.4 on the RTX 5090
  test machine: working set 295 → 131 MiB and private memory 199 → 116 MiB at
  steady state, managed allocation −45 %, 11 threads instead of 17; minimized:
  allocation −92 %, working set −55 %; window shown 805 → 548 ms after launch;
  CLI 171 → 124 ms; portable download 104 → 62 MB. Steady-state CPU is ~0.3 % of
  one core before and after — it is the driver's time for the per-second
  sensor poll, which every hidden feature (fan curve, flight recorder, alerts)
  depends on
- Pages only do display work while you can see them: hidden in the tray or
  minimized, no page formats readouts, copies graph history or redraws, and
  opening a page catches up at once. The dashboard copies the last 600 samples
  its longest graph shows, not the whole hour
- One shared, ReadyToRun-compiled runtime for the app and the CLI instead of two
  compressed single-file executables, which decompressed every assembly into
  private memory at each launch. The portable download shrinks from ~104 MB to
  ~62 MB; the install grows from 2 files to ~260 (and ~+40 MB on disk)
- WinForms is no longer loaded at all: the tray icon talks to the shell
  directly (and finally shows the Afterglow icon — the bundled one was never
  copied to the output, so the tray showed Windows' generic icon)
- Native interop rewritten on source-generated P/Invokes, NVAPI function
  pointers resolved once per process and blittable driver structs: a sensor
  poll allocates 464 bytes instead of 1,864; every struct's wire layout is
  pinned by tests
- The black-box flight recorder writes from a background thread (a stalled
  disk can no longer delay the fan-curve tick), rotates by rename, caps each
  session file at 4 MB, and recognises a clean previous session from its last
  4 KB instead of parsing the whole file at every logon (26 MB on the test PC)
- Fewer idle wake-ups: FPS-page timers run only while capturing, the game
  watcher only while game rules exist, the second-launch activation wait is a
  kernel wait instead of a thread polling every second, and the CSV-only
  sensors (incl. a ~40 ms blocking PCIe call) are read only while CSV logging
- Startup: the logon-task query and install-location check moved off the UI
  thread; each launch logs where its startup time went

### Fixed — per-game driver settings never reached the right game

- Every profile and application name was sent to the driver truncated to its
  first character (classic marshalling of the structs' fixed `char` buffers).
  A rule for an exe the driver didn't know created a profile literally named
  "A" holding an application named after the exe's first letter, and the
  readback "verification" was truncated the same way. Every later create then
  collided with "A", which is what was misdiagnosed in 1.1.0 as "current
  drivers reject creating profiles via NVAPI" — that claim is withdrawn. Names
  now arrive intact (verified read-only against the driver store: 7,984 full
  profile names, where the old code read 7,984 single letters). A profile named
  "A" left by an earlier version is not removed automatically; delete it with
  NVIDIA Profile Inspector if present

### Fixed

- Shutdown could hand the fans back to firmware and then re-pin them: telemetry
  now stops before fan control is released, and a released curve can't re-arm
- Quitting while the V/F probe runs could leave its exact clock pin applied and
  still record a clean exit; exit now cancels the probe, waits for its restore,
  and keeps the crash banner armed if the restore didn't land (as already done
  for the stepper)
- Temperature alerts fire once per crossing (re-armed 3 °C below) instead of
  every minute, name the card on multi-GPU systems, and are logged; the tray's
  rate limit is per kind of alert — a card parked above its alert line no
  longer silences automation and fan-failure notices
- A second launch (or the installer's startup-task hook) no longer marks the
  running instance's crash-recovery records clean on its way out
- Only one Afterglow with administrator rights can run at a time — a
  `--data-dir` copy started elevated beside the resident one now explains and
  exits instead of fighting it over fans, clock locks and TDR resets
- The tray icon comes back after an Explorer restart even though the logon
  task runs elevated (UIPI let the shell's broadcast through), and a failed add
  at logon is retried
- A corrupt or planted flight log with an out-of-range timestamp no longer
  blocks every future launch (F-34)
- The rest of the independent 1.2.0-beta.3 review — every remaining medium and
  low finding, each with a regression test that fails on the old code:
  - V/F curve: profile capture keeps the zero offsets a flatten wrote (F-02/07);
    out-of-range point offsets are refused, not clamped and then reported as
    verified (F-10/58); clearing the curve reads it back and only touches core
    slots (F-03/06/23/43/57); a flatten no longer leaves lower points above the
    target clock (F-11); the V/F and Tuning pages refresh after each other's
    writes (F-08/75)
  - Multi-GPU: curve files are keyed by GPU identity (F-15/55) — a V/F curve
    recorded by an earlier version carries no identity and is not loaded; it
    rebuilds from telemetry or a one-minute probe; game-exit restore puts back
    the temperature limit and V/F curve on the card the game used, with that
    card's own fans (F-16/21); stress/VRAM refuse a lone adapter on the wrong
    bus (F-17/62); FPS session recording without an NVIDIA GPU (F-18); an
    unconfirmed restore at exit mid-stepper keeps the crash banner (F-27); an
    unstamped legacy applied-state record is no longer adopted by two cards
    (F-59)
  - Hardware safety: a failed min-fan-speed query keeps a 30% spin floor
    instead of 0% (F-22); reset refuses a driver default outside the driver's
    own range (F-24)
  - CLI: a malformed `--gpu` or a valueless `vfpoints --set/--flatten` exits 2
    instead of silently using GPU 0 or listing (F-09/12)
  - Update check understands prerelease versions, so a beta is offered the
    release (F-42/65/46); the MCP server survives a read-only profile (F-36);
    the overlay applies settings while open (F-73) and releases its handlers
    (F-28); telemetry can't poll concurrently from the V/F probe (F-29) or
    dispose its stop event under a live thread (F-30); the startup-profile
    picker refreshes (F-74); V/F hit-density bars have the right width (F-76)
  - Installer: a failed "Start with Windows" registration is reported (F-67);
    uninstall removes the stale crash-recovery records and asks (default keep)
    before removing settings, profiles, logs and recordings (F-66); the
    publish script's PresentMon repair works (F-69)
  - Docs: the retracted "RTX 50 blocks per-point curve writes" claim is
    corrected everywhere it survived, nvapioc is credited, SECURITY.md
    describes the opt-in update check (F-01/04/05/35/38/39/40/41/44/45/47/48/
    49/50/51/52)

### Security

- When elevated, Afterglow locks `%ProgramData%\Afterglow` to Administrators
  and SYSTEM (other users keep read access, so unelevated monitoring still
  works) and refuses settings or profiles owned by another standard user —
  ProgramData lets any user create files there, which the elevated logon task
  would otherwise trust. A `--data-dir` folder is never touched
- The data-folder lock-down also walks the tree on each elevated start:
  links inside it are removed (never their targets), folders and trusted files
  are re-owned with inherit-only permissions, and files another user planted
  are moved aside as `*.untrusted`. Settings and profiles are read through one
  handle that supplies the owner check, and saved by rename
- Fan curves have safety floors: the hottest point must ask for at least 30 %
  and the zero-RPM threshold can't exceed 60 °C (0 % at any temperature is
  driver-legal, so a curve read from disk could otherwise keep fans stopped)
- The logon task's definition is no longer staged in user-writable %TEMP%
  (F-33); schtasks/explorer are launched by full path from the elevated app
  (F-32); the "non-admin location" warning judges the exe the task actually
  launches

### Changed

- Windows 11 window chrome: Fluent caption buttons with the red close hover,
  Snap Layouts on the maximize button, a dark rounded frame with a palette
  border, and no more clipped edges when maximized. A Mica backdrop is
  available with `--mica` but off by default: measured, it cost ~0.18 points
  of a core and ~10 MB while the window is visible, for an effect barely
  visible behind the dark palette
- Keyboard: visible focus rings on every control, and the tuning sliders and
  V/F point picker are reachable by keyboard again (F-77); sliders have
  screen-reader names; the tray menu matches the dark theme
- `--data-dir PATH` (app and CLI) keeps all state under PATH instead of
  ProgramData — for test runs and side-by-side builds — and the elevated
  relaunch now quotes arguments that contain spaces

### Internal

- C# 14 partial properties for all 166 view-model properties
  (CommunityToolkit.Mvvm 8.4), `System.Threading.Lock` for every private lock,
  central package management, xUnit v3 on Microsoft.Testing.Platform, 369 tests
  (from 140)
- `tools/measure-perf.ps1` and `tools/measure-startup.ps1` reproduce the numbers
  in docs/performance.md

## 1.2.0-beta.4 — 2026-08-30

### Fixed — independent review of 1.2.0-beta.3 (all 8 high-severity findings)

- Automation rules: the "apply a profile" action now lands on the card that
  actually breached, not on whichever card the profile was stamped for or the
  title bar happened to show. A profile saved for a different card is refused
  outright — before any clock or fan moves — and the log line and tray balloon
  now report what really happened, including a refusal or a partial apply,
  instead of always claiming success. The action also no longer hands the
  breaching card's fans back to firmware
- Applied state: a legacy single-GPU record stamped for another card is no
  longer handed to a second GPU, which could copy that card's tracked clock
  lock into the second card's file under the second card's identity — a silent
  ~half-boost cap on a knob NVML has no getter for. Unstamped records from a
  genuine single-GPU upgrade still migrate
- Fans: "Firmware (auto)" now always issues the driver release, so it can
  recover fans left in manual mode by the per-fan buttons, `afterglow-cli set
  --fan`, the MCP fan tool, or an unclean exit — previously it silently did
  nothing and still reported "restored". A refused release now says so instead
- Profile apply: a saved profile that recorded no per-point V/F offsets now
  removes point offsets still in force (reported as its own knob, like the clock
  lock), so certification can no longer stamp a profile stable against a curve it
  never tested. Only profiles that actually read the table when they were saved
  can remove a curve — a partial `set`, an MCP tuning call, a stepper step or the
  post-game restore says nothing about the curve and leaves it alone
- V/F probe: a probe is bound to the card it started on — switching the
  title-bar GPU mid-probe no longer redirects sampling to the other card while
  the probe keeps locking clocks on, and saving results into, the first. Each
  card's curve recorder now refuses samples from any other card
- Stability stepper: Stop now takes effect within half a second instead of
  after the rest of the burn step (up to 5 minutes of burning at an offset the
  user just abandoned), quitting mid-run waits for the starting offset to be
  restored (and says so in the log if it could not), and a stepper instance that
  is still unwinding now refuses to start a second run on the same GPU
- Start with Windows: the elevated, no-UAC logon task is refused unless the
  executable sits where only administrators can change it. Registering it from
  a portable copy in a user-writable folder handed anything able to rewrite that
  exe administrator rights at every logon. The default install location passes;
  an install redirected to a user-writable folder does not, and the refusal
  explains what to do. A task registered before this check is not removed — the
  Settings toggle now warns when the running exe's location would be refused
- The multi-GPU selector in the title bar is clickable again — it sat inside the
  window-chrome caption band, so every click dragged the window instead of
  opening the list

### Changed

- The per-point curve documentation now matches measurement rather than
  inference: a core-offset write lands on every point of the shared clock-boost
  table at once (so applying one erases per-point edits), and clearing the point
  offsets left the global core offset intact on RTX 5090 / driver 616.56 — but
  rather than depend on that, any clear Afterglow performs during an apply is
  followed by writing the core offset again

## 1.2.0-beta.3 — 2026-08-30

- **Per-point V/F curve editor** (#1) — the Afterburner-style mechanism, on the
  V/F Curve page and as `afterglow-cli vfpoints`: the driver's stored curve
  table drawn over the measured curve (gold dashed), per-point offsets, a
  one-click flatten undervolt (raise the point at the target voltage, cap
  every higher point), clear-all, and profile capture/apply of point offsets —
  every write verified by reading the table back. Interop rebuilt on the
  field-proven nvapioc layouts after discovering the original struct never
  worked anywhere: **the long-standing "RTX 50 blocks the curve interfaces"
  claim was our own broken layout, not the driver** — read and write are now
  verified live on RTX 5090 (127 points, delta scale calibrated against a
  known global offset), and RTX 20/30/40 are expected to work via the same
  interfaces (awaiting field confirmation). The capability is probed live,
  never assumed by generation.

## 1.2.0-beta.2 — 2026-08-29

- Multi-GPU support, phase 2: fan configuration is saved per GPU (the Fans
  page edits and restores the selected card's own mode/curve at startup;
  pre-multi-GPU settings migrate to the primary card), automation rules
  watch every GPU independently (breach time and cooldown per card, the fan
  action pins the breaching card's fans, alerts name the card), and the
  flight recorder keeps one black box per GPU (primary keeps the original
  flight directory; secondaries get flight\gpuN — crash forensics scans all
  of them). Honest limit: dual-GPU behavior is still not verified on real
  dual-NVIDIA hardware; that verification is what the 1.2.0 betas are for.

## 1.2.0-beta.1 — 2026-08-29

- Multi-GPU support, phase 1 (#1): a title-bar GPU selector (visible when
  more than one NVIDIA card is present) points every page — dashboard,
  tuning, fans, V/F curve, stability, profiles, FPS session recording, the
  overlay, and the tray tooltip — at one card. Stress, VRAM, certification,
  the stepper, and the V/F probe bind their D3D adapter to the tuned card's
  PCI bus (resolved from the adapter LUID via D3DKMT — never by enumeration
  order; on a requested bus with several NVIDIA adapters and no match, the
  test refuses instead of guessing). Profiles are stamped with the GPU they
  were saved on and refuse to apply to a different card; game rules and
  startup apply target the stamped card. Applied-state/crash-recovery records
  are per-GPU files (two cards can never overwrite each other; the old single
  file stays readable until superseded), and each GPU records its own V/F
  curve (previously all cards fed one curve). CLI: `--gpu N` on
  stress/vram/certify/vfcurve and `mcp --gpu N`; new `stress --probe-adapter`
  diagnostic prints each GPU's NVML-bus → D3D-adapter mapping.

## 1.1.0 — 2026-08-28

- Independent defect review (docs/REVIEW-2026-08-28.md): all 22 findings and
  4 README-claim gaps addressed. Highlights: offset apply no longer claims
  "(verified)" unless the readback actually happened and matched; unknown
  driver throttle bits are surfaced raw instead of hidden (616.xx reports
  0x400); burn/VRAM tests bind explicitly to the NVIDIA adapter instead of
  trusting DXGI adapter order; burn verification now rotates across the
  entire output instead of a fixed slice; the V/F undervolt planner refuses
  unplannable targets, validates persisted curve data, and requires
  well-populated bins before offering a hardware write; the V/F probe
  restores range locks as range locks; the stepper ships as the
  `find_stable_offset` MCP tool and MCP `isError` now agrees with the result
  body; applied-state is stamped with the GPU UUID; plus tray-alert thread
  marshaling, fan-command failure surfacing, and interop cleanup

- Stress patterns: alongside the sustained burn, **Transition cycling** forces
  P-state and memory-clock switches with bit-exact VRAM retention checks across
  every transition, and **Boost excursions** rides the boost overshoot through
  the top clock bins — the two regimes where daily overclocks fail after
  passing every conventional stress test (`--pattern` in the CLI, `pattern`
  on the MCP `run_stress` tool)
- Crash forensics: an always-on flight recorder keeps recent telemetry and
  applied-offset markers on disk; after a hard crash, the next launch
  correlates the final minutes with the Windows event log (Kernel-Power 41,
  unexpected shutdown, WHEA, TDR, nvlddmkm) and explains the failure in plain
  language — banner on launch, full report on the Stability page
- Full-VRAM test: fills the card's memory up to the DXGI budget with
  deterministic patterns (alternate rounds bit-inverted) and verifies every
  element on the GPU — Stability page, `afterglow-cli vram`, and the MCP
  `run_vram_test` tool
- Profile certification: applies a saved profile and runs all four stability
  modes against it in sequence, stamping each pass into the profile pinned to
  the tested offsets; all four passes mark it stable, a failure resets the
  GPU to driver defaults — Profiles page (with per-mode badges and a
  CERTIFIED chip) and `afterglow-cli certify`
- Per-game NVIDIA driver settings: game rules can now set the driver's own
  frame-rate limiter, vsync, and low-latency (pre-rendered frames = 1) for
  the exe — written to the same DRS store the NVIDIA Control Panel edits,
  verified by readback, persistent with no injection and nothing resident;
  also `afterglow-cli drs`. Current drivers reject creating brand-new app
  profiles via NVAPI, so unknown exes need one-time registration in NVCP
  (games the driver already knows — effectively all of them — just work)
- Automation rules: "if GPU temp / memory junction / board power stays at or
  above X for Y seconds → apply a profile, pin the fans, or reset to
  defaults", with a 5-minute re-arm cooldown, tray notifications, and
  flight-recorder markers (Settings page)
- Session history: every FPS capture of 30 s or more is recorded with the
  offsets that were applied — before/after tuning comparisons per game on the
  FPS page, exportable as a Markdown table
- The flight recorder no longer blocks a second Afterglow instance from
  starting (screenshot/demo runs skip it; failures degrade to
  monitoring-without-black-box instead of an error dialog)
- Certifications are now pinned to the NVIDIA driver version they were earned
  on as well as the offsets — a driver update marks them "⚠ driver changed"
  (re-certify to confirm the tune still holds on the new driver's clock
  management). Certifications from older Afterglow builds carry no driver
  stamp and stay valid.
- Session comparison: Ctrl+click two recorded sessions on the FPS page for an
  A/B delta — avg FPS, 1% lows, board power, temperatures, and FPS-per-watt,
  each as newer-minus-older with percentages — copyable as a Markdown table.
  Comparing different applications is flagged as not comparable instead of
  silently diffed.
- Opt-in update check (off by default): one anonymous request to the GitHub
  Releases API at startup, a tray note if a newer version exists, and a
  "Check now" button in Settings → About. Nothing is downloaded or uploaded;
  failures are silent.
- Graph inspection: every large dashboard graph shows a crosshair readout on
  hover — the exact value under the mouse and how long ago it was sampled
  (real snapshot timestamps, not an assumed polling rate); the temperatures
  panel is one dual-series graph so a single hover reads GPU and memory
  junction together; the V/F curve reads out the nearest measured bin (clock,
  voltage, samples) on hover
- Click any dashboard hero tile to expand it into a full-width hoverable
  10-minute graph — VRAM, fans, and core voltage gain large views for the
  first time
- The automation-rules row in Settings no longer overflows: it wraps, the
  numeric fields fit their values, and the fan-% / profile inputs appear only
  for the action that uses them
- Review round 2 (docs/REVIEW-2026-08-28.md): fan commands are serialized and
  generation-checked so a mode change can never be undone by a slower
  in-flight command from the previous mode; the MCP server survives tools
  returning non-object JSON; `find_stable_offset` takes a `max_minutes`
  budget and restores the starting offset on timeout; the VRAM bandwidth
  claim re-measured honestly (87 GiB/s over 2 minutes on driver 616.56, not
  the 100+ recorded on 610.88)

## 1.0.2 — 2026-08-26

- Start with Windows now runs through a Task Scheduler entry that launches
  Afterglow elevated with no UAC prompt at logon: new Settings toggle, the
  installer checkbox uses the same mechanism, uninstall removes the task, and
  upgrades clean up the old Run-key autostart (which prompted every boot)

## 1.0.1 — 2026-08-26

- V/F chart: axis unit no longer collides with the last tick label; the
  live "now" marker and target label render on backing pills with
  edge-aware placement
- Screenshot mode accepts `--screenshot-delay N` for longer telemetry
  accumulation
- README screenshots replaced with real RTX 5090 captures

## 1.0.0 — 2026-08-26

Initial release.

- V/F Curve page: the GPU's real voltage/frequency map, measured — a ~1-minute
  active probe (lock each clock step under load, record the selected voltage)
  plus continuous passive recording; pick a point to compute the exact
  offset + clock-lock undervolt. Works on RTX 50, where NVIDIA blocks the
  curve interfaces.
  > **Correction (1.2.0):** the "NVIDIA blocks the curve interfaces on RTX 50"
  > claim above was wrong — it was Afterglow's own broken struct layout, not the
  > driver. The curve interfaces work on RTX 50; 1.2.0 ships direct per-point
  > curve editing, verified live on an RTX 5090. The original line is left as
  > written to keep the historical record intact.
- Agent-native: `afterglow-cli mcp` (Model Context Protocol server) and
  `--json` CLI output for autonomous tuning loops.
- Burn test loads FP32 pipes, INT pipe, and the memory controller
  (~95% of TGP measured on RTX 5090), with live sensors on the page.

> Note for anyone who ran pre-release development builds: profiles saved by those
> builds stored a placeholder fan-curve sentinel; re-save your profiles once (they
> now capture your real fan configuration).

- Tuning: core/memory offsets (driver-validated), power limit, voltage boost,
  clock-lock undervolting with wizard presets, knob-by-knob apply results
- Fans: interactive curve editor, dual-direction hysteresis, zero-RPM window,
  ramp limiting, temp-source selection (core / hot spot / memory junction),
  per-fan RPM, true 0% duty
- Monitoring: full sensor suite incl. memory junction on RTX 50, instantaneous
  board power, plain-language throttle analysis, throttle headroom, graphs,
  CSV logging, tray tooltip, temperature alerts
- FPS: ETW present tracing (bundled Intel PresentMon), avg/P1/P0.1 and 1%/0.1%
  lows with labeled methods, frametime graph, click-through overlay
- Stability: compute burn test with bit-exact error detection and TDR capture,
  guided stability stepper
- Automation: per-game auto profiles with revert, global hotkeys incl. panic
  reset, profiles as JSON, scriptable CLI, TDR watchdog, crash recovery,
  tray/startup modes
- Verified end-to-end on RTX 5090 (driver 610.88)
