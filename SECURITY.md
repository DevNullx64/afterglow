# Security policy

## Design stance

Afterglow's attack surface is deliberately small:

- **No kernel driver.** All hardware access is through NVIDIA's userland driver libraries
  (`nvml.dll`, `nvapi64.dll`) that ship with every GeForce driver. Afterglow cannot be used
  as a bring-your-own-vulnerable-driver (BYOVD) primitive because it doesn't bring one.
- **No network access by default.** Afterglow has no telemetry and no accounts, and makes
  no network request on its own. The one exception is an opt-in, off-by-default update
  check: when you enable "Check for updates at startup" (or press "Check now" in
  Settings → About), Afterglow issues a single anonymous HTTPS GET to the GitHub Releases
  API (`api.github.com/repos/minewefu/afterglow/releases/latest`) to compare the latest
  release tag with the running version. Nothing about the machine is uploaded beyond the
  request itself (the User-Agent carries only the app version). The only bundled external
  binary is Intel's MIT-licensed PresentMon console app, pinned to a SHA-256 that CI
  verifies (`THIRD_PARTY.md`).
- **Elevation is scoped.** Admin rights are required only because the driver requires them
  for writes (clocks/fans/power) and ETW tracing; monitoring runs unelevated.
- Settings/profiles are plain JSON under `%ProgramData%\Afterglow`. Afterglow writes no
  registry values of its own. The installer's only registry action is to *delete* a
  pre-1.0.2 `HKLM\...\Run\Afterglow` value left by the old autostart; current autostart is
  a Task Scheduler entry (the "Afterglow" logon task), created by the optional installer
  checkbox and removed at uninstall.

## Known trust boundary

`%ProgramData%` inherits a DACL that lets standard users create files, and the elevated
Afterglow process reads profiles/settings from there. This is defended in depth:

- **Clamping (always).** Every value loaded from disk is schema-validated and then clamped
  to the driver-reported legal range before any write reaches the GPU, so a tampered
  profile cannot push the hardware beyond what the driver itself allows any tool to set.
- **Data-root lock-down (when elevated).** The first time Afterglow runs elevated against
  the default `%ProgramData%\Afterglow` root, it resets that directory's permissions to
  SYSTEM + Administrators full control with the owner set to Administrators, leaving
  standard users read-only. Unelevated monitoring still reads everything; standard users
  can no longer plant or alter files there.
- **Owner check at load (when elevated).** As a second layer — for anything planted before
  the lock-down, e.g. before first run — the elevated app refuses to load a settings or
  profile file whose owner is neither an administrator/SYSTEM nor the current user, falling
  back to safe defaults and logging the refusal.

Residual risk: this closes the cross-user case (a *standard* user influencing the elevated
app). It does **not** defend against the same administrator account tampering with its own
machine — that is the UAC boundary, which Microsoft does not service as a security
boundary. The lock-down is applied only to the default root; a `--data-dir` test or
side-by-side root is deliberately left untouched, and an unelevated instance never changes
a permission or refuses a read.

## Reporting a vulnerability

Please open a GitHub security advisory (Security → Report a vulnerability) or a private
report to the maintainer rather than a public issue. Include reproduction steps and impact.
You should get a response within a week; fixes for confirmed issues in the driver-write or
elevation paths are prioritized above everything else.

## Supported versions

The latest release is supported. Older releases receive fixes only for vulnerabilities that
allow privilege escalation or arbitrary code execution.
