namespace Afterglow.Core.Interop.Nvapi;

/// <summary>An application entry on a DRS profile, as the store reports it.</summary>
public sealed record DrsAppSnapshot(string Name, bool IsPredefined);

/// <summary>
/// A setting stored on a DRS profile. <see cref="Value"/> is the DWORD value and
/// is meaningful only when <see cref="Type"/> is 0 (NVDRS_DWORD_TYPE).
/// </summary>
public sealed record DrsSettingSnapshot(uint Id, uint Type, uint Value, bool IsPredefinedValue);

/// <summary>
/// One DRS profile as read from the store: everything the signature checks below
/// look at, constructible without a GPU. <see cref="DeclaredApps"/> and
/// <see cref="DeclaredSettings"/> are the profile's own numOfApps / numOfSettings
/// (NvAPI_DRS_GetProfileInfo); the lists are what enumeration actually returned,
/// so a short or over-long read is visible and never judged.
/// </summary>
public sealed record DrsProfileSnapshot(
    string Name,
    bool IsPredefined,
    IReadOnlyList<DrsAppSnapshot> Apps,
    IReadOnlyList<DrsSettingSnapshot> Settings,
    int DeclaredApps,
    int DeclaredSettings);

/// <summary>Whether a profile matched a signature, and why — either way.</summary>
public readonly record struct DrsSignatureVerdict(bool Matches, string Reason);

/// <summary>
/// Pure decisions about which DRS profiles Afterglow may delete. Nothing here
/// touches the driver; <see cref="DrsApi"/> reads the snapshots and re-checks
/// them inside the session that deletes.
/// </summary>
public static class DrsProfileSignatures
{
    /// <summary>
    /// The only name the truncation bug ever produced. Up to 1.2.0-beta.4 every
    /// profile name reached the driver as its first character, and every name
    /// Afterglow created began with "A" ("Afterglow - game.exe", and the hidden
    /// --probe-create's "AfterglowProbe…" / "Afterglow - afterglow-drstest.exe").
    /// </summary>
    public const string TruncatedProfileName = "A";

    /// <summary>One of the three settings Afterglow writes (frame-rate limiter, vsync mode, pre-rendered frames).</summary>
    public static bool IsAfterglowSetting(uint settingId) =>
        settingId is DrsApi.FrameRateLimiterId or DrsApi.VsyncModeId or DrsApi.PreRenderedFramesId;

    /// <summary>Profiles the cleanup examines and reports on: one-character names.</summary>
    public static bool IsCleanupCandidate(string profileName) => profileName.Length == 1;

    /// <summary>
    /// The 1.2.0-beta.4-and-earlier leftover: a user-created profile named "A"
    /// whose applications all have one-character names (the exe's truncated first
    /// letter) and whose settings are all ones Afterglow writes. Every condition
    /// must hold; the first one that fails is the reason it is left alone.
    /// </summary>
    public static DrsSignatureVerdict EvaluateTruncationLeftover(DrsProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Name.Length != 1)
        {
            return No($"the name is {profile.Name.Length} characters, not the bug's single letter");
        }

        if (profile.IsPredefined)
        {
            return No("predefined (shipped with the driver) — predefined profiles are never deleted");
        }

        if (!string.Equals(profile.Name, TruncatedProfileName, StringComparison.Ordinal))
        {
            return No($"every profile the truncation bug created was named \"{TruncatedProfileName}\", not \"{profile.Name}\"");
        }

        if (profile.Apps.Count != profile.DeclaredApps || profile.Settings.Count != profile.DeclaredSettings)
        {
            return No($"enumeration returned {profile.Apps.Count} of {profile.DeclaredApps} declared applications and " +
                      $"{profile.Settings.Count} of {profile.DeclaredSettings} declared settings — " +
                      "not judged on a partial or inconsistent read");
        }

        if (profile.Apps.Count == 0)
        {
            return No("it has no applications — the leftover always carries the truncated exe name");
        }

        foreach (var app in profile.Apps)
        {
            if (app.IsPredefined)
            {
                return No($"application \"{app.Name}\" is predefined by the driver");
            }

            if (app.Name.Length != 1)
            {
                return No($"application \"{app.Name}\" is a full name, not a one-letter truncation");
            }
        }

        foreach (var setting in profile.Settings)
        {
            if (!IsAfterglowSetting(setting.Id))
            {
                return No($"it carries setting 0x{setting.Id:X8}, which Afterglow never writes");
            }
        }

        return new DrsSignatureVerdict(true,
            "one-letter profile and application names holding only Afterglow's settings — " +
            "the leftover of the name truncation in Afterglow 1.2.0-beta.4 and earlier");
    }

    /// <summary>
    /// The profile `drs --selftest` created for <paramref name="exeName"/>: exactly
    /// <see cref="DrsApi.ProfileNameFor"/>(exe), user-created, and holding no
    /// application other than that exe (none at all if attaching it failed). The
    /// exe name is random per run and was checked unknown before the write, so
    /// the exact name proves the profile is the run's own.
    /// </summary>
    public static DrsSignatureVerdict EvaluateSelfTestProfile(DrsProfileSnapshot profile, string exeName)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(exeName);

        string expected = DrsApi.ProfileNameFor(exeName);
        if (!string.Equals(profile.Name, expected, StringComparison.Ordinal))
        {
            return No($"named \"{profile.Name}\", not \"{expected}\"");
        }

        if (profile.IsPredefined)
        {
            return No("predefined (shipped with the driver) — predefined profiles are never deleted");
        }

        if (profile.Apps.Count != profile.DeclaredApps)
        {
            return No($"enumeration returned {profile.Apps.Count} of {profile.DeclaredApps} declared applications");
        }

        foreach (var app in profile.Apps)
        {
            if (app.IsPredefined || !string.Equals(app.Name, exeName, StringComparison.OrdinalIgnoreCase))
            {
                return No($"it also holds application \"{app.Name}\", which the self-test did not add");
            }
        }

        return new DrsSignatureVerdict(true, "the self-test's own profile");
    }

    private static DrsSignatureVerdict No(string reason) => new(false, reason);
}
