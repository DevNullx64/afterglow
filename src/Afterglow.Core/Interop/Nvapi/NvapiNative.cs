using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.Nvapi;

/// <summary>NvAPI_Status (subset; full negative-value space is driver-defined).</summary>
public enum NvapiStatus
{
    Ok = 0,
    Error = -1,
    LibraryNotFound = -2,
    NoImplementation = -3,
    ApiNotInitialized = -4,
    InvalidArgument = -5,
    NvidiaDeviceNotFound = -6,
    EndEnumeration = -7,
    InvalidHandle = -8,
    IncompatibleStructVersion = -9,
    HandleInvalidated = -10,
    NotSupported = -104,
    DataNotFound = -121,
    FunctionNotFound = -136,
    SettingNotFound = -160,
    ProfileNotFound = -163,
    ProfileNameInUse = -164,
    ExecutableNotFound = -166,
}

/// <summary>
/// Interface IDs used by Afterglow. Every value is verified against published
/// open-source implementations (LibreHardwareMonitor, NvAPIWrapper) — see
/// docs/research/driver-apis.md for the provenance table.
/// </summary>
internal static class NvapiIds
{
    public const uint Initialize = 0x0150E828;
    public const uint EnumPhysicalGpus = 0xE5AC921F;
    public const uint GpuGetFullName = 0xCEEE8E9F;
    public const uint GpuGetBusId = 0x1BE0B8E5;
    public const uint GpuGetTachReading = 0x5F608315;
    public const uint GpuGetThermalSensors = 0x65FE3AAD;
    public const uint GpuGetDynamicPstatesInfoEx = 0x60DED2ED;
    public const uint GpuClientFanCoolersGetStatus = 0x35AED5E8;
    public const uint GpuClientFanCoolersGetControl = 0x814B209F;
    public const uint GpuClientFanCoolersSetControl = 0xA58971A5;
    public const uint GpuRestoreCoolerSettings = 0x8F6ED0FB;
    public const uint GpuClientVoltRailsGetStatus = 0x465F9BCF;
    public const uint GpuClientThermalPoliciesGetInfo = 0x0D258BB5;
    public const uint GpuClientThermalPoliciesGetStatus = 0xE9C425A1;
    public const uint GpuClientThermalPoliciesSetStatus = 0x34C0B13D;
    public const uint GpuGetCoreVoltageBoostPercent = 0x9DF23CA1;
    public const uint GpuSetCoreVoltageBoostPercent = 0xB9306D9B;
    public const uint GpuGetVfpCurve = 0x21537AD4;
    public const uint GpuGetClockBoostMask = 0x507B4B59;
    public const uint GpuGetClockBoostTable = 0x23F1B133;
    public const uint GpuSetClockBoostTable = 0x0733E009;
}

// Every struct below is blittable (unmanaged, sequential/explicit layout, arrays
// as fixed buffers or [InlineArray] storage), so it crosses the boundary as the
// raw bytes of the managed struct — no per-call marshalling copies and no array
// allocations. InteropLayoutTests pins each size and field offset.

#pragma warning disable CS0649 // fields assigned by native code

/// <summary>Private thermal sensor block: temperatures are fixed-point °C × 256.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvThermalSensors
{
    public const int Channels = 32;

    public uint Version;
    public uint Mask;
    public fixed int Reserved[8];
    public fixed int Temperatures[Channels];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvFanCoolersStatusItem
{
    public uint CoolerId;
    public uint CurrentRpm;
    public uint CurrentMinLevel;
    public uint CurrentMaxLevel;
    public uint CurrentLevel;
    public fixed uint Reserved[8];
}

[InlineArray(NvFanCoolersStatus.MaxCoolers)]
internal struct NvFanCoolersStatusItemArray
{
    private NvFanCoolersStatusItem _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvFanCoolersStatus
{
    public const int MaxCoolers = 32;

    public uint Version;
    public uint Count;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
    public ulong Reserved4;
    public NvFanCoolersStatusItemArray Items;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvFanCoolerControlItem
{
    public uint CoolerId;
    public uint Level;

    /// <summary>0 = auto (firmware), 1 = manual.</summary>
    public uint ControlMode;

    public fixed uint Reserved[8];
}

[InlineArray(NvFanCoolerControl.MaxCoolers)]
internal struct NvFanCoolerControlItemArray
{
    private NvFanCoolerControlItem _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvFanCoolerControl
{
    public const int MaxCoolers = 32;

    public uint Version;
    public uint Reserved;
    public uint Count;
    public fixed uint Reserved2[8];
    public NvFanCoolerControlItemArray Items;
}

/// <summary>Core voltage rail status; explicit layout, size 0x4C, core µV at 0x28.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x4C)]
internal struct NvVoltRailsStatus
{
    [FieldOffset(0x00)]
    public uint Version;

    [FieldOffset(0x28)]
    public uint CoreMicrovolts;

    /// <summary>
    /// Reserved in published layouts; kept only to document the byte at 0x2C.
    /// Nothing reads it — do not use without independent verification.
    /// </summary>
    [FieldOffset(0x2C)]
    public uint ReservedAfterCoreMicrovolts;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvDynamicPstate
{
    public uint IsPresent;
    public int Percentage;
}

[InlineArray(NvDynamicPstatesInfo.Domains)]
internal struct NvDynamicPstateArray
{
    private NvDynamicPstate _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvDynamicPstatesInfo
{
    public const int Domains = 8;

    public uint Version;
    public uint Flags;
    public NvDynamicPstateArray Utilizations;
}

/// <summary>Thermal policy limits; temperatures fixed-point °C × 256.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesInfoEntry
{
    public int Controller;
    public uint Unknown1;
    public int MinimumTemp;
    public int DefaultTemp;
    public int MaximumTemp;
    public uint Unknown2;
}

[InlineArray(NvThermalPoliciesInfo.MaxEntries)]
internal struct NvThermalPoliciesInfoEntryArray
{
    private NvThermalPoliciesInfoEntry _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesInfo
{
    public const int MaxEntries = 4;

    public uint Version;
    public byte Count;
    public byte Unknown;
    public NvThermalPoliciesInfoEntryArray Entries;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesStatusEntry
{
    public int Controller;

    /// <summary>Target temperature, °C × 256.</summary>
    public int TargetTemp;

    public uint PstateId;
}

[InlineArray(NvThermalPoliciesStatus.MaxEntries)]
internal struct NvThermalPoliciesStatusEntryArray
{
    private NvThermalPoliciesStatusEntry _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NvThermalPoliciesStatus
{
    public const int MaxEntries = 4;

    public uint Version;
    public uint Count;
    public NvThermalPoliciesStatusEntryArray Entries;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvVoltageBoostPercent
{
    public uint Version;
    public uint Percent;
    public fixed uint Reserved[8];
}

// --- Per-point V/F curve (private clock-boost interfaces) --------------------
// Layouts and call sequence follow Demion/nvapioc: GetClockBoostMask marks
// live table slots, GetVFPCurve carries each slot's frequency/voltage
// (clockType 0 = core, 1 = memory), and Get/SetClockBoostTable carry the
// per-point frequency deltas (plain kHz — calibrated live against a known
// global offset). All 255 slots are uniform (the previous 80+23 split read
// the same 7208-byte wire format with an interpreted tail — and never
// worked; the "RTX 50 blocks the curve APIs" finding from early development
// was that broken layout, not the driver). Read AND write verified live on
// RTX 5090 / driver 616.56.

/// <summary>Shape shared by the three clock-boost tables.</summary>
internal static class NvClockBoost
{
    /// <summary>Table slots in every clock-boost structure.</summary>
    public const int Slots = 255;

    /// <summary>Bytes in the live-slot bitmask (one bit per slot, rounded up).</summary>
    public const int MaskBytes = 32;
}

/// <summary>One slot of the clock-boost mask: whether the table index is a live point.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvClockMaskEntry
{
    public uint ClockType;
    public byte Enabled;
    public fixed byte Unknown[19];
}

[InlineArray(NvClockBoost.Slots)]
internal struct NvClockMaskEntryArray
{
    private NvClockMaskEntry _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvClockMasks
{
    public uint Version;
    public fixed byte Mask[NvClockBoost.MaskBytes];
    public fixed byte Unknown1[32];
    public NvClockMaskEntryArray Clocks;
}

/// <summary>One stored V/F point: the driver's frequency/voltage for a table slot.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvVfpCurveEntry
{
    public uint ClockType;
    public uint FrequencyKHz;
    public uint VoltageMicroV;
    public fixed byte Unknown[16];
}

[InlineArray(NvClockBoost.Slots)]
internal struct NvVfpCurveEntryArray
{
    private NvVfpCurveEntry _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvVfpCurve
{
    public uint Version;
    public fixed byte Mask[NvClockBoost.MaskBytes];
    public fixed byte Unknown1[32];
    public NvVfpCurveEntryArray Clocks;
}

/// <summary>
/// One clock-boost-table slot: the per-point frequency delta, plain kHz on the
/// hardware it was calibrated on (see NvapiGpu.TableDeltaPerMHz).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvClockTableEntry
{
    public uint ClockType;
    public fixed byte Unknown1[16];
    public int FrequencyDeltaKHz;
    public fixed byte Unknown2[12];
}

[InlineArray(NvClockBoost.Slots)]
internal struct NvClockTableEntryArray
{
    private NvClockTableEntry _element;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvClockTable
{
    public uint Version;
    public fixed byte Mask[NvClockBoost.MaskBytes];
    public fixed byte Unknown1[32];
    public NvClockTableEntryArray Clocks;
}

#pragma warning restore CS0649

/// <summary>
/// nvapi64.dll plumbing: interfaces are resolved through the exported
/// nvapi_QueryInterface and called through unmanaged function pointers. A null
/// pointer means the driver does not provide that interface.
/// </summary>
internal static unsafe partial class NvapiNative
{
    private static int _initialized;

    [LibraryImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint QueryInterface(uint interfaceId);

    /// <summary>Address of one NVAPI interface, or 0 when nvapi64.dll or the interface is missing.</summary>
    internal static nint Resolve(uint id)
    {
        try
        {
            return QueryInterface(id);
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// NvAPI_Initialize, run until it first succeeds and then never again in
    /// this process: nothing calls NvAPI_Unload, so a successful initialization
    /// stays valid for the process lifetime and repeating it only bumps the
    /// driver's reference count. A failure is not cached — the next caller
    /// retries, exactly as before. <see cref="NvapiStatus.LibraryNotFound"/>
    /// when nvapi64.dll (or its initialize interface) is absent.
    /// </summary>
    internal static NvapiStatus EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 0)
        {
            return NvapiStatus.Ok;
        }

        var initialize = (delegate* unmanaged[Cdecl]<NvapiStatus>)Resolve(NvapiIds.Initialize);
        if (initialize == null)
        {
            return NvapiStatus.LibraryNotFound;
        }

        var status = initialize();
        if (status == NvapiStatus.Ok)
        {
            Volatile.Write(ref _initialized, 1);
        }

        return status;
    }

    /// <summary>NVAPI struct version word: sizeof(struct) | (version &lt;&lt; 16).</summary>
    internal static uint MakeVersion<T>(int version) where T : unmanaged =>
        (uint)(sizeof(T) | (version << 16));
}

/// <summary>
/// The NVAPI GPU interface table. Interface addresses are process-wide (the
/// same for every GPU), so the table is resolved exactly once per process —
/// on first use, which is always after NvAPI_Initialize succeeded, because
/// only <see cref="NvapiApi"/> and the <see cref="NvapiGpu"/> instances it
/// hands out touch it.
/// </summary>
internal sealed unsafe class NvapiGpuFunctions
{
    private static readonly Lazy<NvapiGpuFunctions> Instance = new(() => new NvapiGpuFunctions());

    internal readonly delegate* unmanaged[Cdecl]<nint*, int*, NvapiStatus> EnumPhysicalGpus =
        (delegate* unmanaged[Cdecl]<nint*, int*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.EnumPhysicalGpus);

    internal readonly delegate* unmanaged[Cdecl]<nint, byte*, NvapiStatus> GetFullName =
        (delegate* unmanaged[Cdecl]<nint, byte*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetFullName);

    internal readonly delegate* unmanaged[Cdecl]<nint, uint*, NvapiStatus> GetBusId =
        (delegate* unmanaged[Cdecl]<nint, uint*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetBusId);

    internal readonly delegate* unmanaged[Cdecl]<nint, int*, NvapiStatus> GetTachReading =
        (delegate* unmanaged[Cdecl]<nint, int*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetTachReading);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvThermalSensors*, NvapiStatus> GetThermalSensors =
        (delegate* unmanaged[Cdecl]<nint, NvThermalSensors*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetThermalSensors);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvDynamicPstatesInfo*, NvapiStatus> GetDynamicPstates =
        (delegate* unmanaged[Cdecl]<nint, NvDynamicPstatesInfo*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetDynamicPstatesInfoEx);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvFanCoolersStatus*, NvapiStatus> FanCoolersGetStatus =
        (delegate* unmanaged[Cdecl]<nint, NvFanCoolersStatus*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuClientFanCoolersGetStatus);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvFanCoolerControl*, NvapiStatus> FanCoolersGetControl =
        (delegate* unmanaged[Cdecl]<nint, NvFanCoolerControl*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuClientFanCoolersGetControl);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvFanCoolerControl*, NvapiStatus> FanCoolersSetControl =
        (delegate* unmanaged[Cdecl]<nint, NvFanCoolerControl*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuClientFanCoolersSetControl);

    /// <summary>(gpu, coolerIndexes, indexCount); null/0 restores every cooler.</summary>
    internal readonly delegate* unmanaged[Cdecl]<nint, uint*, uint, NvapiStatus> RestoreCoolerSettings =
        (delegate* unmanaged[Cdecl]<nint, uint*, uint, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuRestoreCoolerSettings);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvVoltRailsStatus*, NvapiStatus> VoltRailsGetStatus =
        (delegate* unmanaged[Cdecl]<nint, NvVoltRailsStatus*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuClientVoltRailsGetStatus);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvThermalPoliciesInfo*, NvapiStatus> ThermalPoliciesGetInfo =
        (delegate* unmanaged[Cdecl]<nint, NvThermalPoliciesInfo*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuClientThermalPoliciesGetInfo);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvThermalPoliciesStatus*, NvapiStatus> ThermalPoliciesGetStatus =
        (delegate* unmanaged[Cdecl]<nint, NvThermalPoliciesStatus*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuClientThermalPoliciesGetStatus);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvThermalPoliciesStatus*, NvapiStatus> ThermalPoliciesSetStatus =
        (delegate* unmanaged[Cdecl]<nint, NvThermalPoliciesStatus*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuClientThermalPoliciesSetStatus);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvVoltageBoostPercent*, NvapiStatus> GetVoltageBoost =
        (delegate* unmanaged[Cdecl]<nint, NvVoltageBoostPercent*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetCoreVoltageBoostPercent);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvVoltageBoostPercent*, NvapiStatus> SetVoltageBoost =
        (delegate* unmanaged[Cdecl]<nint, NvVoltageBoostPercent*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuSetCoreVoltageBoostPercent);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvVfpCurve*, NvapiStatus> GetVfpCurve =
        (delegate* unmanaged[Cdecl]<nint, NvVfpCurve*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetVfpCurve);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvClockMasks*, NvapiStatus> GetClockBoostMask =
        (delegate* unmanaged[Cdecl]<nint, NvClockMasks*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetClockBoostMask);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvClockTable*, NvapiStatus> GetClockBoostTable =
        (delegate* unmanaged[Cdecl]<nint, NvClockTable*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuGetClockBoostTable);

    internal readonly delegate* unmanaged[Cdecl]<nint, NvClockTable*, NvapiStatus> SetClockBoostTable =
        (delegate* unmanaged[Cdecl]<nint, NvClockTable*, NvapiStatus>)NvapiNative.Resolve(NvapiIds.GpuSetClockBoostTable);

    private NvapiGpuFunctions()
    {
    }

    /// <summary>The process-wide table (resolved on first access, thread-safe, exactly once).</summary>
    internal static NvapiGpuFunctions Resolved => Instance.Value;
}
