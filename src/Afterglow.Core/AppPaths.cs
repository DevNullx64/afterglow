using System.Security.Cryptography;
using System.Text;

namespace Afterglow.Core;

/// <summary>
/// Canonical file-system locations. Everything lives under ProgramData so the
/// elevated app, the CLI, and a login task all see the same state.
/// </summary>
public static class AppPaths
{
    /// <summary>Test seam: redirects every path off the real ProgramData tree. Never set in production.</summary>
    internal static string? OverrideRoot { get; set; }

    public static string Root => OverrideRoot ?? DefaultRoot;

    private static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Afterglow");

    /// <summary>True when <c>--data-dir</c> moved this process's state off the shared ProgramData tree.</summary>
    public static bool IsRedirected => OverrideRoot is not null;

    /// <summary>
    /// Moves every path (settings, profiles, logs, flight recordings, curves,
    /// applied state) under <paramref name="path"/> for the rest of this process —
    /// the <c>--data-dir</c> option, so a test run or a side-by-side build never
    /// rotates or rewrites the installed copy's state. Must run before anything
    /// reads a path. Deliberately a command-line option only: an environment
    /// variable would let any unelevated process point the elevated logon task
    /// at a folder it controls (and at a startup profile it wrote).
    /// </summary>
    public static void UseDataRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        OverrideRoot = Path.GetFullPath(path);
    }

    /// <summary>
    /// Suffix that keeps named kernel objects (the single-instance mutex) apart
    /// per data root: empty for the shared root, a short stable hash otherwise,
    /// so a redirected test instance can run beside the resident one.
    /// </summary>
    public static string InstanceSuffix => OverrideRoot is null
        ? string.Empty
        : "-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(OverrideRoot.ToUpperInvariant())), 0, 4);

    public static string ProfilesDir => Path.Combine(Root, "profiles");

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Written while tuning values are applied; consulted on startup for crash recovery.</summary>
    public static string AppliedStateFile => Path.Combine(Root, "applied-state.json");

    /// <summary>Flight-recorder telemetry ring and crash-forensics reports.</summary>
    public static string FlightDir => Path.Combine(Root, "flight");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(LogsDir);
    }
}
