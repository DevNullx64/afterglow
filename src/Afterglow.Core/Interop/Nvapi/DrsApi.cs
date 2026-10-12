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

    // Setting IDs (NvApiDriverSettings.h).
    public const uint FrameRateLimiterId = 0x10835002;   // FRL_FPS_ID: fps, 0 = off
    public const uint VsyncModeId = 0x00A879CF;          // VSYNCMODE_ID
    public const uint PreRenderedFramesId = 0x007BA09E;  // PRERENDERLIMIT_ID

    public const uint VsyncApplicationControlled = 0x60925292;
    public const uint VsyncForceOff = 0x08416747;
    public const uint VsyncForceOn = 0x47814940;

    private const string ProfilePrefix = "Afterglow - ";

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

        bool ok = actual.FrameCapFps == (settings.FrameCapFps > 0 ? settings.FrameCapFps : 0) &&
                  actual.Vsync == (settings.Vsync is "on" or "off" ? settings.Vsync : "default") &&
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
    private NvapiStatus FindApplication(nint session, string exeName, out nint profile)
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
        return rc;
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
        string profileName = ProfilePrefix + exeName.ToLowerInvariant();
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
