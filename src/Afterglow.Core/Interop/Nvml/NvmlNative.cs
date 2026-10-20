using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Afterglow.Core.Interop.Nvml;

/// <summary>nvmlUtilization_t</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

/// <summary>nvmlMemory_t (v1)</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

/// <summary>nvmlBAR1Memory_t</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlBar1Memory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

/// <summary>
/// nvmlClockOffset_v1_t (24 bytes). Version constant = sizeof | (1 &lt;&lt; 24).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlClockOffset
{
    public const uint Version1 = 24 | (1u << 24);

    public uint Version;
    public NvmlClockType Type;
    public uint Pstate;
    public int ClockOffsetMHz;
    public int MinClockOffsetMHz;
    public int MaxClockOffsetMHz;
}

/// <summary>nvmlFanSpeedInfo_v1_t (12 bytes): out RPM for one fan.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlFanSpeedInfo
{
    public const uint Version1 = 12 | (1u << 24);

    public uint Version;
    public uint Fan;
    public uint SpeedRpm;
}

/// <summary>nvmlMarginTemperature_v1_t (8 bytes): headroom to the nearest slowdown threshold.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlMarginTemperature
{
    public const uint Version1 = 8 | (1u << 24);

    public uint Version;
    public int MarginTemperatureC;
}

/// <summary>nvmlFieldValue_t (40 bytes). Set FieldId before calling GetFieldValues.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NvmlFieldValue
{
    public uint FieldId;
    public uint ScopeId;
    public long Timestamp;
    public long LatencyUsec;
    public uint ValueType;
    public NvmlReturn Status;
    public ulong Value;

    public const uint FiPowerInstant = 186;
    public const uint FiTemperatureShutdownTLimit = 193;
    public const uint FiTemperatureSlowdownTLimit = 194;
    public const uint FiTemperatureMemMaxTLimit = 195;
    public const uint FiTemperatureGpuMaxTLimit = 196;
    public const uint FiPcieCountTxBytes = 197;
    public const uint FiPcieCountRxBytes = 198;
}

/// <summary>nvmlPciInfo_t (68 bytes, unversioned).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NvmlPciInfo
{
    public fixed byte BusIdLegacy[16];
    public uint Domain;
    public uint Bus;
    public uint Device;
    public uint PciDeviceId;
    public uint PciSubSystemId;
    public fixed byte BusId[32];
}

/// <summary>
/// Raw P/Invoke surface of nvml.dll (ships with the NVIDIA driver in System32).
/// Signatures follow nvml.h; all functions here are part of NVIDIA's documented,
/// ABI-stable NVML API. Newer exports may be missing on old drivers — callers must
/// treat <see cref="EntryPointNotFoundException"/> as "not supported".
/// Source-generated (<see cref="LibraryImportAttribute"/>): every parameter is
/// blittable, so the generated stubs only pin and forward. The generated inner
/// P/Invokes still name "nvml.dll", so the resolver below keeps deciding where
/// the library is loaded from.
/// </summary>
internal static unsafe partial class NvmlNative
{
    // The resolver pins this name to System32; a private copy here could drift
    // from the resolver's and silently hand the load back to default probing.
    private const string Lib = VendorLibraryResolver.NvmlLib;

    /// <summary>
    /// Installs the native-library resolver for this assembly exactly once. Must be
    /// called before the first NVML P/Invoke (done by <see cref="NvmlApi.TryCreate"/>).
    /// The resolver itself lives in <see cref="VendorLibraryResolver"/> because
    /// .NET permits only one per assembly and the Intel libraries need the same
    /// System32 pinning; NVML's own resolution order is unchanged.
    /// </summary>
    internal static void EnsureResolverInstalled() => VendorLibraryResolver.EnsureInstalled();

    // --- Lifecycle -----------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlInit_v2();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlShutdown();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlSystemGetDriverVersion(byte* version, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlSystemGetNVMLVersion(byte* version, uint length);

    // --- Device enumeration / identity --------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetName(nint device, byte* name, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetUUID(nint device, byte* uuid, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetVbiosVersion(nint device, byte* version, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetBoardPartNumber(nint device, byte* partNumber, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetArchitecture(nint device, out uint architecture);

    // --- Temperatures --------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetTemperature(nint device, NvmlTemperatureSensor sensor, out uint tempC);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetTemperatureThreshold(
        nint device, NvmlTemperatureThreshold threshold, out uint tempC);

    // --- Clocks --------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetClockInfo(nint device, NvmlClockType type, out uint clockMHz);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetMaxClockInfo(nint device, NvmlClockType type, out uint clockMHz);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetClock(
        nint device, NvmlClockType type, NvmlClockId clockId, out uint clockMHz);

    // --- Utilization / memory ------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetEncoderUtilization(
        nint device, out uint utilization, out uint samplingPeriodUs);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetDecoderUtilization(
        nint device, out uint utilization, out uint samplingPeriodUs);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetMemoryInfo(nint device, out NvmlMemory memory);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetBAR1MemoryInfo(nint device, out NvmlBar1Memory bar1Memory);

    // --- Power ---------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetPowerManagementLimitConstraints(
        nint device, out uint minMilliwatts, out uint maxMilliwatts);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetPowerManagementDefaultLimit(nint device, out uint milliwatts);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceSetPowerManagementLimit(nint device, uint milliwatts);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetTotalEnergyConsumption(nint device, out ulong millijoules);

    // --- Performance state / throttling --------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetPerformanceState(nint device, out uint pstate);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetCurrentClocksThrottleReasons(nint device, out ulong reasons);

    // --- Fans ----------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetNumFans(nint device, out uint numFans);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetFanSpeed(nint device, out uint speedPercent);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetFanSpeed_v2(nint device, uint fan, out uint speedPercent);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetMinMaxFanSpeed(nint device, out uint minPercent, out uint maxPercent);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetFanControlPolicy_v2(
        nint device, uint fan, out NvmlFanControlPolicy policy);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceSetFanControlPolicy(
        nint device, uint fan, NvmlFanControlPolicy policy);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceSetFanSpeed_v2(nint device, uint fan, uint speedPercent);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceSetDefaultFanSpeed_v2(nint device, uint fan);

    // --- Locked clocks (documented overclock-adjacent controls) ---------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceSetGpuLockedClocks(nint device, uint minClockMHz, uint maxClockMHz);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceResetGpuLockedClocks(nint device);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceSetMemoryLockedClocks(nint device, uint minClockMHz, uint maxClockMHz);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceResetMemoryLockedClocks(nint device);

    // --- PCIe ----------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetCurrPcieLinkGeneration(nint device, out uint generation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetCurrPcieLinkWidth(nint device, out uint width);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetMaxPcieLinkGeneration(nint device, out uint generation);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetMaxPcieLinkWidth(nint device, out uint width);

    /// <summary>Note: the driver samples for ~20 ms inside this call; do not call on a UI thread.</summary>
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetPcieThroughput(
        nint device, NvmlPcieUtilCounter counter, out uint kilobytesPerSecond);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetPciInfo_v3(nint device, out NvmlPciInfo pciInfo);

    // --- Clock offsets (modern overclocking API, driver R555+) -----------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetClockOffsets(nint device, ref NvmlClockOffset info);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceSetClockOffsets(nint device, ref NvmlClockOffset info);

    // --- Newer fan / temperature reads ----------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetFanSpeedRPM(nint device, ref NvmlFanSpeedInfo fanSpeed);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetTargetFanSpeed(nint device, uint fan, out uint targetSpeedPercent);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetMarginTemperature(nint device, ref NvmlMarginTemperature margin);

    // --- Batched field values ---------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NvmlReturn nvmlDeviceGetFieldValues(
        nint device, int valuesCount, NvmlFieldValue* values);
}
