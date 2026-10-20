using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.Nvapi;

/// <summary>Per-game driver settings Afterglow manages through DRS.</summary>
public sealed record GameDriverSettings
{
    /// <summary>Driver frame-rate limiter in FPS; 0 = off.</summary>
    public int FrameCapFps { get; init; }

    /// <summary>"default" (application-controlled), "on", or "off".</summary>
    public string Vsync { get; init; } = "default";

    /// <summary>Caps maximum pre-rendered frames at 1 (the classic latency reduction).</summary>
    public bool LowLatency { get; init; }

    public bool AnythingSet => FrameCapFps > 0 || Vsync is "on" or "off" || LowLatency;
}

/// <summary>
/// A read-only <see cref="DrsApi.ScanProfiles"/> pass: how many profiles were
/// enumerated, how many of those GetProfileInfo could not read (so were not
/// examined), and full snapshots of the ones whose name passed the filter.
/// </summary>
public sealed record DrsScanResult(int Scanned, int Unreadable, IReadOnlyList<DrsProfileSnapshot> Profiles);

/// <summary>
/// What <see cref="DrsApi.DeleteProfiles"/> did per requested name: deleted and
/// saved, found but refused by the confirmation (left untouched), or absent.
/// </summary>
public sealed record DrsDeleteResult(
    IReadOnlyList<string> Deleted, IReadOnlyList<string> Refused, IReadOnlyList<string> NotFound);

/// <summary>
/// NVIDIA DRS (driver settings store) — the same per-application settings the
/// NVIDIA Control Panel edits: frame-rate limiter, vsync, pre-rendered frames.
/// Settings persist in the driver, so they apply whenever the game runs, with
/// no injection and nothing resident. Interface IDs and setting IDs come from
/// the public NVAPI SDK (NvApiDriverSettings.h) and published open-source
/// implementations; every write is verified by reading the store back.
/// </summary>
public sealed unsafe class DrsApi
{
    // Function interface IDs (public NVAPI SDK).
    private const uint IdCreateSession = 0x0694D52E;
    private const uint IdDestroySession = 0xDAD9CFF8;
    private const uint IdLoadSettings = 0x375DBD6B;
    private const uint IdSaveSettings = 0xFCBC7E14;
    private const uint IdFindProfileByName = 0x7E4A9A0B;
    private const uint IdCreateProfile = 0xCC176068;
    private const uint IdDeleteProfile = 0x17093206;
    private const uint IdCreateApplication = 0x4347A9DE;
    private const uint IdFindApplicationByName = 0xEEE566B2;
    private const uint IdSetSetting = 0x577DD202;
    private const uint IdGetSetting = 0x73BF8338;
    private const uint IdDeleteProfileSetting = 0xE4A26362;
    private const uint IdGetProfileInfo = 0x61CD6FD6;
    private const uint IdEnumProfiles = 0xBC371EE0;

    // Profile inspection (leftover cleanup, self-test). Taken from NVIDIA's own
    // interface table, nvapi_interface.h in github.com/NVIDIA/nvapi (main):
    // NvAPI_DRS_EnumApplications 0x7fa2173a, NvAPI_DRS_EnumSettings 0xae3039da
    // — the same table every ID above matches. Signatures per nvapi.h:
    // (hSession, hProfile, NvU32 startIndex, NvU32* count [in: array length,
    // out: entries returned], NVDRS_APPLICATION* / NVDRS_SETTING* array);
    // NVAPI_END_ENUMERATION once startIndex passes the last entry.
    private const uint IdEnumApplications = 0x7FA2173A;
    private const uint IdEnumSettings = 0xAE3039DA;

    // Setting IDs (NvApiDriverSettings.h).
    public const uint FrameRateLimiterId = 0x10835002;   // FRL_FPS_ID: fps, 0 = off
    public const uint VsyncModeId = 0x00A879CF;          // VSYNCMODE_ID
    public const uint PreRenderedFramesId = 0x007BA09E;  // PRERENDERLIMIT_ID

    public const uint VsyncApplicationControlled = 0x60925292;
    public const uint VsyncForceOff = 0x08416747;
    public const uint VsyncForceOn = 0x47814940;

    private const string ProfilePrefix = "Afterglow - ";

    /// <summary>Upper bound on any enumeration loop (the store holds ~8,000 profiles).</summary>
    private const uint MaxEnumeration = 100_000;

    // Wire structs. NVAPI strings are NvAPI_UnicodeString (NvU16[2048], UTF-16),
    // and `fixed char` is exactly that because these structs are only ever
    // passed by pointer — nothing marshals them (the assembly also disables
    // runtime marshalling). Under the classic marshaller the compiler-generated
    // fixed-buffer type is CharSet.Ansi, which copied only the FIRST character,
    // narrowed to one ANSI byte: every profile name read back as a single
    // letter, and names written to the store were truncated the same way.

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NvdrsProfile
    {
        public uint Version;
        public fixed char ProfileName[2048];
        public uint GpuSupport;
        public uint IsPredefined;
        public uint NumOfApps;
        public uint NumOfSettings;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NvdrsApplication   // NVDRS_APPLICATION_V4 — what current drivers expect for find/create
    {
        public uint Version;
        public uint IsPredefined;
        public fixed char AppName[2048];
        public fixed char UserFriendlyName[2048];
        public fixed char Launcher[2048];
        public fixed char FileInFolder[2048];
        public uint Flags;                 // bit 0 isMetro, bit 1 isCommandLine
        public fixed char CommandLine[2048];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NvdrsSetting
    {
        public uint Version;
        public fixed char SettingName[2048];
        public uint SettingId;
        public uint SettingType;             // 0 = DWORD
        public uint SettingLocation;
        public uint IsCurrentPredefined;
        public uint IsPredefinedValid;
        public uint PredefinedValue;
        public fixed byte PredefinedPad[4096];
        public uint CurrentValue;
        public fixed byte CurrentPad[4096];
    }

    /// <summary>
    /// The DRS interface table. Interface addresses are process-wide, so it is
    /// resolved exactly once per process, on first use — which is always after
    /// NvAPI_Initialize succeeded, because only <see cref="TryCreate"/> touches
    /// it and only past that point.
    /// </summary>
    private sealed class Functions
    {
        internal readonly delegate* unmanaged[Cdecl]<nint*, NvapiStatus> CreateSession =
            (delegate* unmanaged[Cdecl]<nint*, NvapiStatus>)NvapiNative.Resolve(IdCreateSession);

        internal readonly delegate* unmanaged[Cdecl]<nint, NvapiStatus> DestroySession =
            (delegate* unmanaged[Cdecl]<nint, NvapiStatus>)NvapiNative.Resolve(IdDestroySession);

        internal readonly delegate* unmanaged[Cdecl]<nint, NvapiStatus> LoadSettings =
            (delegate* unmanaged[Cdecl]<nint, NvapiStatus>)NvapiNative.Resolve(IdLoadSettings);

        internal readonly delegate* unmanaged[Cdecl]<nint, NvapiStatus> SaveSettings =
            (delegate* unmanaged[Cdecl]<nint, NvapiStatus>)NvapiNative.Resolve(IdSaveSettings);

        internal readonly delegate* unmanaged[Cdecl]<nint, char*, nint*, NvapiStatus> FindProfileByName =
            (delegate* unmanaged[Cdecl]<nint, char*, nint*, NvapiStatus>)NvapiNative.Resolve(IdFindProfileByName);

        internal readonly delegate* unmanaged[Cdecl]<nint, NvdrsProfile*, nint*, NvapiStatus> CreateProfile =
            (delegate* unmanaged[Cdecl]<nint, NvdrsProfile*, nint*, NvapiStatus>)NvapiNative.Resolve(IdCreateProfile);

        internal readonly delegate* unmanaged[Cdecl]<nint, nint, NvapiStatus> DeleteProfile =
            (delegate* unmanaged[Cdecl]<nint, nint, NvapiStatus>)NvapiNative.Resolve(IdDeleteProfile);

        internal readonly delegate* unmanaged[Cdecl]<nint, nint, NvdrsApplication*, NvapiStatus> CreateApplication =
            (delegate* unmanaged[Cdecl]<nint, nint, NvdrsApplication*, NvapiStatus>)NvapiNative.Resolve(IdCreateApplication);

        internal readonly delegate* unmanaged[Cdecl]<nint, char*, nint*, NvdrsApplication*, NvapiStatus> FindApplicationByName =
            (delegate* unmanaged[Cdecl]<nint, char*, nint*, NvdrsApplication*, NvapiStatus>)NvapiNative.Resolve(IdFindApplicationByName);

        internal readonly delegate* unmanaged[Cdecl]<nint, nint, NvdrsSetting*, NvapiStatus> SetSetting =
            (delegate* unmanaged[Cdecl]<nint, nint, NvdrsSetting*, NvapiStatus>)NvapiNative.Resolve(IdSetSetting);

        internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, NvdrsSetting*, NvapiStatus> GetSetting =
            (delegate* unmanaged[Cdecl]<nint, nint, uint, NvdrsSetting*, NvapiStatus>)NvapiNative.Resolve(IdGetSetting);

        internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, NvapiStatus> DeleteProfileSetting =
            (delegate* unmanaged[Cdecl]<nint, nint, uint, NvapiStatus>)NvapiNative.Resolve(IdDeleteProfileSetting);

        internal readonly delegate* unmanaged[Cdecl]<nint, nint, NvdrsProfile*, NvapiStatus> GetProfileInfo =
            (delegate* unmanaged[Cdecl]<nint, nint, NvdrsProfile*, NvapiStatus>)NvapiNative.Resolve(IdGetProfileInfo);

        /// <summary>Diagnostic only (<see cref="ProbeListProfiles"/>); not required by <see cref="TryCreate"/>.</summary>
        internal readonly delegate* unmanaged[Cdecl]<nint, uint, nint*, NvapiStatus> EnumProfiles =
            (delegate* unmanaged[Cdecl]<nint, uint, nint*, NvapiStatus>)NvapiNative.Resolve(IdEnumProfiles);

        /// <summary>Profile inspection only; not required by <see cref="TryCreate"/>.</summary>
        internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint*, NvdrsApplication*, NvapiStatus> EnumApplications =
            (delegate* unmanaged[Cdecl]<nint, nint, uint, uint*, NvdrsApplication*, NvapiStatus>)NvapiNative.Resolve(IdEnumApplications);

        /// <summary>Profile inspection only; not required by <see cref="TryCreate"/>.</summary>
        internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint*, NvdrsSetting*, NvapiStatus> EnumSettings =
            (delegate* unmanaged[Cdecl]<nint, nint, uint, uint*, NvdrsSetting*, NvapiStatus>)NvapiNative.Resolve(IdEnumSettings);

        /// <summary>Everything <see cref="ReadProfileSnapshot"/> and <see cref="ScanProfiles"/> call.</summary>
        internal bool CanInspect => EnumProfiles != null && EnumApplications != null && EnumSettings != null;

        /// <summary>Every interface the settings workflow needs is present.</summary>
        internal bool HasRequired =>
            CreateSession != null && DestroySession != null && LoadSettings != null && SaveSettings != null &&
            FindProfileByName != null && CreateProfile != null && DeleteProfile != null &&
            CreateApplication != null && FindApplicationByName != null && SetSetting != null &&
            GetSetting != null && DeleteProfileSetting != null && GetProfileInfo != null;
    }

    private static readonly Lazy<Functions> ResolvedFunctions = new(() => new Functions());

    private readonly Functions _fn;

    private DrsApi(Functions functions)
    {
        _fn = functions;
    }

    public static DrsApi? TryCreate(out NvapiStatus status)
    {
        // Layout self-checks: these are the exact wire sizes the driver
        // validates through the version word; a layout regression must fail
        // loudly here, never as silent driver-store corruption.
        if (sizeof(NvdrsProfile) != 4116 || sizeof(NvdrsApplication) != 20492 || sizeof(NvdrsSetting) != 12320)
        {
            status = NvapiStatus.IncompatibleStructVersion;
            return null;
        }

        status = NvapiNative.EnsureInitialized();
        if (status != NvapiStatus.Ok)
        {
            return null;
        }

        var functions = ResolvedFunctions.Value;
        if (!functions.HasRequired)
        {
            status = NvapiStatus.FunctionNotFound;
            return null;
        }

        return new DrsApi(functions);
    }

    /// <summary>
    /// Writes the given settings onto the DRS application profile for
    /// <paramref name="exeName"/> (creating an "Afterglow - exe" profile when
    /// the driver has none), then re-reads every value from a fresh session to
    /// verify the store took it.
    /// </summary>
    public NvapiStatus ApplySettings(string exeName, GameDriverSettings settings, out string note)
    {
        note = string.Empty;

        // A value outside the three legal ones used to mean "not enabled", which
        // DELETES the stored setting — and the verification below re-derived its
        // expected value from the same test, so it compared against "default",
        // exactly what the delete produced, and could never fail. `--vsync yes`
        // therefore removed the user's vsync override and reported "(verified)".
        if (settings.Vsync is not ("default" or "on" or "off"))
        {
            note = $"'{settings.Vsync}' is not a vsync mode (expected default, on or off) — nothing was written";
            return NvapiStatus.InvalidArgument;
        }
        var status = WithSession((session) =>
        {
            var rc = FindOrCreateProfile(session, exeName, out nint profile, out bool created);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            rc = ApplyOne(session, profile, FrameRateLimiterId, settings.FrameCapFps > 0,
                (uint)Math.Clamp(settings.FrameCapFps, 0, 1000));
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            uint vsync = settings.Vsync switch
            {
                "on" => VsyncForceOn,
                "off" => VsyncForceOff,
                _ => VsyncApplicationControlled,
            };
            rc = ApplyOne(session, profile, VsyncModeId, settings.Vsync is "on" or "off", vsync);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            rc = ApplyOne(session, profile, PreRenderedFramesId, settings.LowLatency, 1);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            return _fn.SaveSettings(session);
        });

        if (status != NvapiStatus.Ok)
        {
            return status;
        }

        // Verification round-trip from a fresh session.
        var readBack = ReadSettings(exeName, out var actual);
        if (readBack != NvapiStatus.Ok)
        {
            note = "write succeeded but readback failed";
            return readBack;
        }

        // Compare against the REQUESTED value. Re-deriving the expected value
        // from the same predicate that decided whether to write made the check
        // self-confirming for every input the predicate rejected.
        bool ok = actual.FrameCapFps == (settings.FrameCapFps > 0 ? settings.FrameCapFps : 0) &&
                  actual.Vsync == settings.Vsync &&
                  actual.LowLatency == settings.LowLatency;
        note = ok ? "verified" : $"readback mismatch (got cap={actual.FrameCapFps}, vsync={actual.Vsync}, lowlat={actual.LowLatency})";
        return ok ? NvapiStatus.Ok : NvapiStatus.Error;
    }

    /// <summary>Reads the three Afterglow-managed settings for an executable.</summary>
    public NvapiStatus ReadSettings(string exeName, out GameDriverSettings settings)
    {
        int cap = 0;
        string vsync = "default";
        bool lowLatency = false;

        var status = WithSession((session) =>
        {
            var rc = FindApplication(session, exeName, out nint profile);
            if (rc != NvapiStatus.Ok)
            {
                return rc;   // ExecutableNotFound and friends -> no profile
            }

            if (TryGetDword(session, profile, FrameRateLimiterId, out uint frl))
            {
                cap = (int)frl;
            }

            if (TryGetDword(session, profile, VsyncModeId, out uint v))
            {
                vsync = v switch
                {
                    VsyncForceOn => "on",
                    VsyncForceOff => "off",
                    _ => "default",
                };
            }

            if (TryGetDword(session, profile, PreRenderedFramesId, out uint prerender))
            {
                lowLatency = prerender == 1;
            }

            return NvapiStatus.Ok;
        });

        settings = new GameDriverSettings { FrameCapFps = cap, Vsync = vsync, LowLatency = lowLatency };
        return status;
    }

    /// <summary>
    /// Removes Afterglow's settings for an executable: a profile Afterglow
    /// created is deleted outright; on a pre-existing (driver-predefined or
    /// user) profile only the three managed settings are removed.
    /// </summary>
    public NvapiStatus ClearSettings(string exeName)
    {
        return WithSession((session) =>
        {
            if (FindApplication(session, exeName, out nint profile) != NvapiStatus.Ok)
            {
                return NvapiStatus.Ok;   // nothing to clear
            }

            string profileName = GetProfileNameFromApp(session, profile);
            if (profileName.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            {
                _ = _fn.DeleteProfile(session, profile);
            }
            else
            {
                _ = _fn.DeleteProfileSetting(session, profile, FrameRateLimiterId);
                _ = _fn.DeleteProfileSetting(session, profile, VsyncModeId);
                _ = _fn.DeleteProfileSetting(session, profile, PreRenderedFramesId);
            }

            return _fn.SaveSettings(session);
        });
    }

    /// <summary>Diagnostic: every profile name in the store that contains the filter.</summary>
    public IReadOnlyList<string> ProbeListProfiles(string contains)
    {
        var names = new List<string>();
        if (_fn.EnumProfiles == null)
        {
            return names;
        }

        int total = 0;
        int infoOk = 0;
        var lastInfoStatus = NvapiStatus.Ok;
        _ = WithSession((session) =>
        {
            for (uint i = 0; i < 20000; i++)
            {
                nint profile = 0;
                if (_fn.EnumProfiles(session, i, &profile) != NvapiStatus.Ok)
                {
                    break;
                }

                total++;
                var info = default(NvdrsProfile);
                info.Version = MakeVersion(sizeof(NvdrsProfile), 1);
                lastInfoStatus = _fn.GetProfileInfo(session, profile, &info);
                if (lastInfoStatus != NvapiStatus.Ok)
                {
                    continue;
                }

                infoOk++;
                string name = ReadFixedString(info.ProfileName, 2048);
                if (contains.Length == 0 || name.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return NvapiStatus.Ok;
        });

        names.Insert(0, $"[probe: {total} profiles enumerated, {infoOk} GetProfileInfo ok, last status {lastInfoStatus}]");
        return names;
    }

    /// <summary>Diagnostic: create a bare profile, save, re-find from a fresh session, delete.</summary>
    public string ProbeCreate(string profileName)
    {
        var createRc = NvapiStatus.Ok;
        var saveRc = NvapiStatus.Ok;
        string writtenBack = string.Empty;
        _ = WithSession((session) =>
        {
            var newProfile = default(NvdrsProfile);
            newProfile.Version = MakeVersion(sizeof(NvdrsProfile), 1);
            newProfile.GpuSupport = 0x1;   // Geforce
            WriteFixedString(newProfile.ProfileName, profileName);
            writtenBack = ReadFixedString(newProfile.ProfileName, 2048);
            nint created = 0;
            createRc = _fn.CreateProfile(session, &newProfile, &created);
            saveRc = createRc == NvapiStatus.Ok ? _fn.SaveSettings(session) : NvapiStatus.Ok;
            return NvapiStatus.Ok;
        });

        var findRc = ProbeFindProfile(profileName);

        var deleteRc = WithSession((session) =>
        {
            nint handle = 0;
            fixed (char* name = profileName)
            {
                if (_fn.FindProfileByName(session, name, &handle) != NvapiStatus.Ok)
                {
                    return NvapiStatus.ProfileNotFound;
                }
            }

            var rc = _fn.DeleteProfile(session, handle);
            return rc != NvapiStatus.Ok ? rc : _fn.SaveSettings(session);
        });

        return $"nameInStruct='{writtenBack}' create={createRc} save={saveRc} refind={findRc} delete+save={deleteRc}";
    }

    /// <summary>Diagnostic: raw FindProfileByName status for an exact profile name.</summary>
    public NvapiStatus ProbeFindProfile(string profileName)
    {
        return WithSession((session) =>
        {
            nint handle = 0;
            fixed (char* name = profileName)
            {
                return _fn.FindProfileByName(session, name, &handle);
            }
        });
    }

    /// <summary>The profile name Afterglow creates for an executable no driver profile knows.</summary>
    public static string ProfileNameFor(string exeName) => ProfilePrefix + exeName.ToLowerInvariant();

    /// <summary>
    /// Read-only scan of the whole store: a full snapshot (applications and
    /// settings) of every profile whose name passes <paramref name="nameFilter"/>.
    /// It calls enumeration and GetProfileInfo only — never Set, Delete or Save —
    /// and its session is destroyed unsaved, so it cannot change the store.
    /// Anything but a clean END_ENUMERATION is returned as a failure: a scan that
    /// stopped early must not read as "nothing found".
    /// </summary>
    public NvapiStatus ScanProfiles(Func<string, bool> nameFilter, out DrsScanResult result)
    {
        ArgumentNullException.ThrowIfNull(nameFilter);
        result = new DrsScanResult(0, 0, []);
        if (!_fn.CanInspect)
        {
            return NvapiStatus.FunctionNotFound;
        }

        int scanned = 0;
        int unreadable = 0;
        var profiles = new List<DrsProfileSnapshot>();
        var status = WithSession((session) =>
        {
            for (uint i = 0; i < MaxEnumeration; i++)
            {
                nint profile = 0;
                var rc = _fn.EnumProfiles(session, i, &profile);
                if (rc == NvapiStatus.EndEnumeration)
                {
                    return NvapiStatus.Ok;
                }

                if (rc != NvapiStatus.Ok)
                {
                    return rc;
                }

                scanned++;
                var info = default(NvdrsProfile);
                info.Version = MakeVersion(sizeof(NvdrsProfile), 1);
                if (_fn.GetProfileInfo(session, profile, &info) != NvapiStatus.Ok)
                {
                    unreadable++;   // reported, never silently skipped
                    continue;
                }

                if (nameFilter(ReadFixedString(info.ProfileName, 2048)))
                {
                    profiles.Add(Snapshot(session, profile, &info));
                }
            }

            return NvapiStatus.Error;   // more profiles than any real store holds: not a complete scan
        });

        result = new DrsScanResult(scanned, unreadable, profiles);
        return status;
    }

    /// <summary>Read-only: a snapshot of the profile with exactly this name (ProfileNotFound when absent).</summary>
    public NvapiStatus ReadProfile(string profileName, out DrsProfileSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        DrsProfileSnapshot? found = null;
        var status = _fn.CanInspect
            ? WithSession((session) => ReadProfileSnapshot(session, profileName, out _, out found))
            : NvapiStatus.FunctionNotFound;
        snapshot = found;
        return status;
    }

    /// <summary>
    /// Read-only: the profile FindApplicationByName resolves <paramref name="exeName"/>
    /// to, and the application name exactly as the store holds it.
    /// </summary>
    public NvapiStatus ReadApplicationProfile(string exeName, out DrsProfileSnapshot? snapshot, out string storedAppName)
    {
        ArgumentNullException.ThrowIfNull(exeName);
        DrsProfileSnapshot? found = null;
        string appName = string.Empty;
        var status = !_fn.CanInspect ? NvapiStatus.FunctionNotFound : WithSession((session) =>
        {
            var rc = FindApplication(session, exeName, out nint profile, out appName);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            var info = default(NvdrsProfile);
            info.Version = MakeVersion(sizeof(NvdrsProfile), 1);
            rc = _fn.GetProfileInfo(session, profile, &info);
            if (rc != NvapiStatus.Ok)
            {
                return rc;
            }

            found = Snapshot(session, profile, &info);
            return NvapiStatus.Ok;
        });

        snapshot = found;
        storedAppName = appName;
        return status;
    }

    /// <summary>
    /// Deletes each named profile that — re-read inside this deleting session, so
    /// nothing can change between the check and the delete — still satisfies
    /// <paramref name="confirm"/>, then saves once. Predefined profiles are refused
    /// whatever <paramref name="confirm"/> says. If any delete fails, nothing is
    /// saved (DRS edits live in the session until SaveSettings; the session is
    /// destroyed unsaved). On any failure — including a refused save —
    /// <see cref="DrsDeleteResult.Deleted"/> is empty; callers re-read the store
    /// to report what it actually holds.
    /// </summary>
    public NvapiStatus DeleteProfiles(
        IEnumerable<string> profileNames, Func<DrsProfileSnapshot, bool> confirm, out DrsDeleteResult result)
    {
        ArgumentNullException.ThrowIfNull(profileNames);
        ArgumentNullException.ThrowIfNull(confirm);
        var deleted = new List<string>();
        var refused = new List<string>();
        var notFound = new List<string>();
        var status = !_fn.CanInspect ? NvapiStatus.FunctionNotFound : WithSession((session) =>
        {
            foreach (string name in profileNames.Distinct(StringComparer.Ordinal))
            {
                var rc = ReadProfileSnapshot(session, name, out nint handle, out var snapshot);
                if (rc == NvapiStatus.ProfileNotFound)
                {
                    notFound.Add(name);
                    continue;
                }

                if (rc != NvapiStatus.Ok)
                {
                    return rc;
                }

                if (snapshot!.IsPredefined || !confirm(snapshot))
                {
                    refused.Add(name);
                    continue;
                }

                rc = _fn.DeleteProfile(session, handle);
                if (rc != NvapiStatus.Ok)
                {
                    return rc;
                }

                deleted.Add(snapshot.Name);
            }

            return deleted.Count > 0 ? _fn.SaveSettings(session) : NvapiStatus.Ok;
        });

        result = new DrsDeleteResult(status == NvapiStatus.Ok ? deleted : [], refused, notFound);
        return status;
    }

    private NvapiStatus WithSession(Func<nint, NvapiStatus> body)
    {
        nint session = 0;
        var rc = _fn.CreateSession(&session);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        try
        {
            rc = _fn.LoadSettings(session);
            return rc != NvapiStatus.Ok ? rc : body(session);
        }
        finally
        {
            _ = _fn.DestroySession(session);
        }
    }

    /// <summary>FindApplicationByName for the lower-cased exe (V4 application struct, contents discarded).</summary>
    private NvapiStatus FindApplication(nint session, string exeName, out nint profile) =>
        FindApplication(session, exeName, out profile, out _);

    /// <summary>FindApplicationByName for the lower-cased exe, plus the app name as stored.</summary>
    private NvapiStatus FindApplication(nint session, string exeName, out nint profile, out string storedAppName)
    {
        var app = default(NvdrsApplication);
        app.Version = MakeVersion(sizeof(NvdrsApplication), 4);
        nint found = 0;
        NvapiStatus rc;
        fixed (char* name = exeName.ToLowerInvariant())
        {
            rc = _fn.FindApplicationByName(session, name, &found, &app);
        }

        profile = found;
        storedAppName = rc == NvapiStatus.Ok ? ReadFixedString(app.AppName, 2048) : string.Empty;
        return rc;
    }

    /// <summary>FindProfileByName for an exact name, then a full snapshot of what it found.</summary>
    private NvapiStatus ReadProfileSnapshot(nint session, string profileName, out nint handle, out DrsProfileSnapshot? snapshot)
    {
        snapshot = null;
        nint found = 0;
        NvapiStatus rc;
        fixed (char* name = profileName)
        {
            rc = _fn.FindProfileByName(session, name, &found);
        }

        handle = found;
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        var info = default(NvdrsProfile);
        info.Version = MakeVersion(sizeof(NvdrsProfile), 1);
        rc = _fn.GetProfileInfo(session, found, &info);
        if (rc != NvapiStatus.Ok)
        {
            return rc;
        }

        snapshot = Snapshot(session, found, &info);
        return NvapiStatus.Ok;
    }

    /// <summary>
    /// A profile's applications and settings, read one entry per call. It asks
    /// for one entry past the declared count, so a driver returning more than
    /// numOfApps / numOfSettings shows as a count mismatch; any failure other
    /// than END_ENUMERATION leaves a short list, which the signature checks refuse
    /// to judge. Read-only.
    /// </summary>
    private DrsProfileSnapshot Snapshot(nint session, nint profile, NvdrsProfile* info)
    {
        var apps = new List<DrsAppSnapshot>();
        for (uint i = 0; i <= info->NumOfApps && i < MaxEnumeration; i++)
        {
            var app = default(NvdrsApplication);
            app.Version = MakeVersion(sizeof(NvdrsApplication), 4);
            uint count = 1;
            if (_fn.EnumApplications(session, profile, i, &count, &app) != NvapiStatus.Ok || count == 0)
            {
                break;
            }

            apps.Add(new DrsAppSnapshot(ReadFixedString(app.AppName, 2048), app.IsPredefined != 0));
        }

        var settings = new List<DrsSettingSnapshot>();
        for (uint i = 0; i <= info->NumOfSettings && i < MaxEnumeration; i++)
        {
            var setting = default(NvdrsSetting);
            setting.Version = MakeVersion(sizeof(NvdrsSetting), 1);
            uint count = 1;
            if (_fn.EnumSettings(session, profile, i, &count, &setting) != NvapiStatus.Ok || count == 0)
            {
                break;
            }

            settings.Add(new DrsSettingSnapshot(
                setting.SettingId, setting.SettingType, setting.CurrentValue, setting.IsCurrentPredefined != 0));
        }

        return new DrsProfileSnapshot(
            ReadFixedString(info->ProfileName, 2048), info->IsPredefined != 0, apps, settings,
            (int)Math.Min(info->NumOfApps, int.MaxValue), (int)Math.Min(info->NumOfSettings, int.MaxValue));
    }

    private NvapiStatus FindOrCreateProfile(nint session, string exeName, out nint profile, out bool created)
    {
        created = false;
        var rc = FindApplication(session, exeName, out profile);
        if (rc == NvapiStatus.Ok)
        {
            return NvapiStatus.Ok;
        }

        // No driver profile knows this exe: create our own and attach the app.
        string profileName = ProfileNameFor(exeName);
        var newProfile = default(NvdrsProfile);
        newProfile.Version = MakeVersion(sizeof(NvdrsProfile), 1);
        WriteFixedString(newProfile.ProfileName, profileName);
        nint handle = 0;
        rc = _fn.CreateProfile(session, &newProfile, &handle);
        if (rc != NvapiStatus.Ok)
        {
            // Name collision from a previous run: reuse it.
            fixed (char* name = profileName)
            {
                if (_fn.FindProfileByName(session, name, &handle) != NvapiStatus.Ok)
                {
                    profile = handle;
                    return rc;
                }
            }
        }

        profile = handle;
        var newApp = default(NvdrsApplication);
        newApp.Version = MakeVersion(sizeof(NvdrsApplication), 4);
        WriteFixedString(newApp.AppName, exeName.ToLowerInvariant());
        rc = _fn.CreateApplication(session, profile, &newApp);
        created = true;
        return rc;
    }

    private NvapiStatus ApplyOne(nint session, nint profile, uint settingId, bool enabled, uint value)
    {
        if (!enabled)
        {
            // Not-found is fine — the knob simply wasn't set before.
            _ = _fn.DeleteProfileSetting(session, profile, settingId);
            return NvapiStatus.Ok;
        }

        var setting = default(NvdrsSetting);
        setting.Version = MakeVersion(sizeof(NvdrsSetting), 1);
        setting.SettingId = settingId;
        setting.SettingType = 0;   // DWORD
        setting.CurrentValue = value;
        return _fn.SetSetting(session, profile, &setting);
    }

    private bool TryGetDword(nint session, nint profile, uint settingId, out uint value)
    {
        var setting = default(NvdrsSetting);
        setting.Version = MakeVersion(sizeof(NvdrsSetting), 1);
        if (_fn.GetSetting(session, profile, settingId, &setting) == NvapiStatus.Ok)
        {
            value = setting.CurrentValue;
            return true;
        }

        value = 0;
        return false;
    }

    private string GetProfileNameFromApp(nint session, nint profile)
    {
        var info = default(NvdrsProfile);
        info.Version = MakeVersion(sizeof(NvdrsProfile), 1);
        if (_fn.GetProfileInfo(session, profile, &info) != NvapiStatus.Ok)
        {
            return string.Empty;
        }

        return ReadFixedString(info.ProfileName, 2048);
    }

    private static string ReadFixedString(char* source, int capacity)
    {
        int length = 0;
        while (length < capacity && source[length] != '\0')
        {
            length++;
        }

        return new string(source, 0, length);
    }

    private static uint MakeVersion(int size, int version) => (uint)size | ((uint)version << 16);

    private static void WriteFixedString(char* destination, string value)
    {
        int count = Math.Min(value.Length, 2047);
        for (int i = 0; i < count; i++)
        {
            destination[i] = value[i];
        }

        destination[count] = '\0';
    }
}
