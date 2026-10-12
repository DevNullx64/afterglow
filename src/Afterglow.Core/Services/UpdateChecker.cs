using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Afterglow.Core.Services;

public sealed record UpdateCheckResult(string LatestTag, bool UpdateAvailable);

/// <summary>
/// A SemVer 2.0 version — the three numeric core fields plus an optional
/// prerelease tail — with precedence per semver.org §11. Build metadata
/// (the <c>+sha</c> tail) is parsed off and ignored in comparisons, exactly as
/// the spec requires. Afterglow's releases are tagged <c>vMAJOR.MINOR.PATCH</c>
/// with an optional <c>-beta.N</c> suffix, and the running build stamps the same
/// string into <see cref="AssemblyInformationalVersionAttribute"/>; the numeric
/// <c>AssemblyVersion</c> cannot carry the suffix, which is why the update check
/// used to treat <c>1.2.0-beta.3</c> as equal to <c>1.2.0</c> (F-42/F-65).
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemVer>
{
    /// <summary>
    /// Parses <c>[v]MAJOR[.MINOR[.PATCH]][-prerelease][+build]</c>. One or two
    /// core numbers are accepted (missing fields are zero) so historic 2-part
    /// tags like <c>v2.0</c> still parse; build metadata is discarded. Returns
    /// null for anything without at least one numeric core field.
    /// </summary>
    public static SemVer? TryParse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string s = tag.Trim().TrimStart('v', 'V');

        // Build metadata is ignored for precedence (§10) — drop it before anything else.
        int plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string prerelease = string.Empty;
        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            // A trailing '-' with nothing after it is not a valid prerelease.
            if (prerelease.Length == 0 || prerelease.Split('.').Any(id => id.Length == 0))
            {
                return null;
            }
        }

        string[] core = s.Split('.');
        if (core.Length is < 1 or > 3)
        {
            return null;
        }

        int[] parts = [0, 0, 0];
        for (int i = 0; i < core.Length; i++)
        {
            if (!int.TryParse(core[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                return null;
            }

            parts[i] = value;
        }

        return new SemVer(parts[0], parts[1], parts[2], prerelease);
    }

    /// <summary>A plain numeric <see cref="Version"/> as a release (no prerelease).</summary>
    public static SemVer FromVersion(Version version) =>
        new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0), string.Empty);

    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;

    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;

    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

    public int CompareTo(SemVer other)
    {
        int core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        // §11: a version with a prerelease has LOWER precedence than the
        // associated normal version (1.0.0-beta < 1.0.0).
        if (!IsPrerelease && !other.IsPrerelease)
        {
            return 0;
        }

        if (!IsPrerelease)
        {
            return 1;
        }

        if (!other.IsPrerelease)
        {
            return -1;
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Dot-separated identifier precedence, semver.org §11.</summary>
    private static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        int count = Math.Min(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            bool aNum = int.TryParse(a[i], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int an);
            bool bNum = int.TryParse(b[i], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int bn);

            int cmp;
            if (aNum && bNum)
            {
                cmp = an.CompareTo(bn);
            }
            else if (aNum)
            {
                // Numeric identifiers always have lower precedence than alphanumeric.
                cmp = -1;
            }
            else if (bNum)
            {
                cmp = 1;
            }
            else
            {
                cmp = string.CompareOrdinal(a[i], b[i]);
            }

            if (cmp != 0)
            {
                return Math.Sign(cmp);
            }
        }

        // All shared identifiers equal: the larger set has higher precedence.
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() =>
        IsPrerelease
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

/// <summary>
/// Opt-in update check: one anonymous GET against the GitHub Releases API,
/// comparing the latest tag to the running version. Off by default; nothing
/// about the machine is sent (GitHub sees the same request a browser would).
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/minewefu/afterglow/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/minewefu/afterglow/releases/latest";

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Afterglow-update-check", DisplayVersion));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    /// <summary>Numeric assembly version (no prerelease suffix — kept for callers that display three parts).</summary>
    public static Version? CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version;

    /// <summary>
    /// The real running version, prerelease and all. Read from
    /// <see cref="AssemblyInformationalVersionAttribute"/> (the one place the
    /// <c>-beta.N</c> suffix survives the build), with build metadata stripped;
    /// falls back to the numeric assembly version when the attribute is absent.
    /// This — not <see cref="CurrentVersion"/> — is what the comparison uses, so
    /// a beta build is correctly told when its final release ships (F-65).
    /// </summary>
    public static SemVer? RunningVersion
    {
        get
        {
            string? informational = typeof(UpdateChecker).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return SemVer.TryParse(informational)
                ?? (CurrentVersion is { } v ? SemVer.FromVersion(v) : null);
        }
    }

    /// <summary>Human-readable running version for display and the request User-Agent.</summary>
    public static string DisplayVersion =>
        RunningVersion?.ToString() ?? CurrentVersion?.ToString(3) ?? "dev";

    /// <summary>SemVer-2.0 precedence: is <paramref name="latestTag"/> strictly newer than <paramref name="current"/>?</summary>
    public static bool IsNewer(string latestTag, SemVer current) =>
        SemVer.TryParse(latestTag) is { } latest && latest.CompareTo(current) > 0;

    /// <summary>
    /// Back-compatible overload for a numeric <see cref="Version"/> (treated as a
    /// release with no prerelease). The prerelease-aware overload above is what
    /// the fetch path uses.
    /// </summary>
    public static bool IsNewer(string latestTag, Version current) =>
        IsNewer(latestTag, SemVer.FromVersion(current));

    /// <summary>Null means the check could not run (offline, rate-limited, bad JSON).</summary>
    public static async Task<UpdateCheckResult?> CheckAsync(
        Version current, CancellationToken cancellationToken = default)
    {
        // The passed numeric version cannot carry a prerelease suffix, so the
        // decision is made against the real running SemVer (F-42/F-65). The
        // argument is retained for API compatibility and as a fallback.
        SemVer running = RunningVersion ?? SemVer.FromVersion(current);
        try
        {
            string json = await Client.Value.GetStringAsync(ReleasesApiUrl, cancellationToken)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.GetString() is not { Length: > 0 } tag)
            {
                return null;
            }

            // A tag that isn't a version we can order (four parts, a stray prefix)
            // is "couldn't tell", not "up to date".
            if (SemVer.TryParse(tag) is null)
            {
                Diagnostics.Log.Info($"Update check: the latest release tag '{tag}' is not a version Afterglow can compare.");
                return null;
            }

            return new UpdateCheckResult(tag, IsNewer(tag, running));
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
