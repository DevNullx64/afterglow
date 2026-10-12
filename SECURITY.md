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

- **Validation and clamping (always).** Every value loaded from disk is schema-validated and
  clamped to the driver-reported legal range before any write reaches the GPU. Fan curves
  additionally have safety floors — the hottest point must ask for at least 30 % and the
  zero-RPM threshold can't exceed 60 °C — because 0 % at any temperature is "legal" to the
  driver.
- **Data-folder lock-down (each elevated start).** Against the default
  `%ProgramData%\Afterglow` root only, the elevated app (GUI or CLI):
  - refuses a root that is itself a link (it removes the link — never its target — and
    recreates a real folder), then sets SYSTEM + Administrators full control, owner
    Administrators, standard users read-only;
  - walks the tree without following links: removes any link inside it (a junction there
    would redirect the elevated app's log and flight-recorder writes), re-owns every folder
    and trusted file to Administrators with inherit-only permissions (a creator otherwise
    keeps an implicit right to reopen its folder or file, and explicit grants survive), and
    moves files owned by another standard user aside as `*.untrusted` — kept for
    inspection, never loaded, never re-owned.
- **Owner check at load (when elevated).** A settings or profile file is read through one
  handle that also supplies its owner and refuses writers while open; if the owner is
  neither an administrator/SYSTEM nor the current user, it is refused (defaults are used and
  the refusal is logged). Saves go through a temporary file and a rename, so the result
  carries the folder's permissions, and a refused file is moved aside rather than replaced.

What this does not cover: code already running as the same administrator user, unelevated —
that is the UAC boundary, which Microsoft does not service as a security boundary. A
`--data-dir` root is deliberately left untouched, and an unelevated instance never changes a
permission or refuses a read (writes it can no longer make degrade with a message). Before
the first elevated start — or if something outside Afterglow loosens the folder's
permissions again — only the owner check stands, until the next elevated start re-applies
the lock-down.

## Reporting a vulnerability

Please open a GitHub security advisory (Security → Report a vulnerability) or a private
report to the maintainer rather than a public issue. Include reproduction steps and impact.
You should get a response within a week; fixes for confirmed issues in the driver-write or
elevation paths are prioritized above everything else.

## Supported versions

The latest release is supported. Older releases receive fixes only for vulnerabilities that
allow privilege escalation or arbitrary code execution.
