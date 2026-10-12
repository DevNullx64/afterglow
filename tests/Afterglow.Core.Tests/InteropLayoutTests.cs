using System.Reflection;
using System.Runtime.InteropServices;
using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Tests;

/// <summary>
/// Pins the exact wire layout (size and every field offset) of every struct
/// Afterglow hands to nvml.dll, nvapi64.dll, and gdi32.dll. The numbers are
/// hard-coded on purpose: they were measured from the shipped layouts, and a
/// struct that drifts by one byte would send garbage to GPU firmware on a SET
/// call. Cross-checks against independently known sizes: clock-boost mask /
/// VFP curve / clock table 6188 / 7208 / 9248 bytes, DRS PROFILE v1 4116,
/// APPLICATION v4 20492, SETTING v1 12320.
/// </summary>
public class InteropLayoutTests
{
    private const string Nvml = "Afterglow.Core.Interop.Nvml.";
    private const string Nvapi = "Afterglow.Core.Interop.Nvapi.";
    private const string Stress = "Afterglow.Core.Stress.";

    /// <summary>Type name → (size, every instance field with its byte offset).</summary>
    private static readonly Dictionary<string, (int Size, (string Field, int Offset)[] Fields)> Expected = new()
    {
        // --- NVML ---------------------------------------------------------------
        [Nvml + "NvmlUtilization"] = (8, [("Gpu", 0), ("Memory", 4)]),
        [Nvml + "NvmlMemory"] = (24, [("Total", 0), ("Free", 8), ("Used", 16)]),
        [Nvml + "NvmlBar1Memory"] = (24, [("Total", 0), ("Free", 8), ("Used", 16)]),
        [Nvml + "NvmlClockOffset"] = (24,
        [
            ("Version", 0), ("Type", 4), ("Pstate", 8), ("ClockOffsetMHz", 12),
            ("MinClockOffsetMHz", 16), ("MaxClockOffsetMHz", 20),
        ]),
        [Nvml + "NvmlFanSpeedInfo"] = (12, [("Version", 0), ("Fan", 4), ("SpeedRpm", 8)]),
        [Nvml + "NvmlMarginTemperature"] = (8, [("Version", 0), ("MarginTemperatureC", 4)]),
        [Nvml + "NvmlFieldValue"] = (40,
        [
            ("FieldId", 0), ("ScopeId", 4), ("Timestamp", 8), ("LatencyUsec", 16),
            ("ValueType", 24), ("Status", 28), ("Value", 32),
        ]),
        [Nvml + "NvmlPciInfo"] = (68,
        [
            ("BusIdLegacy", 0), ("Domain", 16), ("Bus", 20), ("Device", 24),
            ("PciDeviceId", 28), ("PciSubSystemId", 32), ("BusId", 36),
        ]),

        // --- NVAPI sensors / fans / voltage / thermal policy ---------------------
        [Nvapi + "NvThermalSensors"] = (168, [("Version", 0), ("Mask", 4), ("Reserved", 8), ("Temperatures", 40)]),
        [Nvapi + "NvFanCoolersStatusItem"] = (52,
        [
            ("CoolerId", 0), ("CurrentRpm", 4), ("CurrentMinLevel", 8), ("CurrentMaxLevel", 12),
            ("CurrentLevel", 16), ("Reserved", 20),
        ]),
        [Nvapi + "NvFanCoolersStatus"] = (1704,
        [
            ("Version", 0), ("Count", 4), ("Reserved1", 8), ("Reserved2", 16), ("Reserved3", 24),
            ("Reserved4", 32), ("Items", 40),
        ]),
        [Nvapi + "NvFanCoolerControlItem"] = (44, [("CoolerId", 0), ("Level", 4), ("ControlMode", 8), ("Reserved", 12)]),
        [Nvapi + "NvFanCoolerControl"] = (1452, [("Version", 0), ("Reserved", 4), ("Count", 8), ("Reserved2", 12), ("Items", 44)]),
        [Nvapi + "NvVoltRailsStatus"] = (76, [("Version", 0), ("CoreMicrovolts", 40), ("ReservedAfterCoreMicrovolts", 44)]),
        [Nvapi + "NvDynamicPstate"] = (8, [("IsPresent", 0), ("Percentage", 4)]),
        [Nvapi + "NvDynamicPstatesInfo"] = (72, [("Version", 0), ("Flags", 4), ("Utilizations", 8)]),
        [Nvapi + "NvThermalPoliciesInfoEntry"] = (24,
        [
            ("Controller", 0), ("Unknown1", 4), ("MinimumTemp", 8), ("DefaultTemp", 12),
            ("MaximumTemp", 16), ("Unknown2", 20),
        ]),
        [Nvapi + "NvThermalPoliciesInfo"] = (104, [("Version", 0), ("Count", 4), ("Unknown", 5), ("Entries", 8)]),
        [Nvapi + "NvThermalPoliciesStatusEntry"] = (12, [("Controller", 0), ("TargetTemp", 4), ("PstateId", 8)]),
        [Nvapi + "NvThermalPoliciesStatus"] = (56, [("Version", 0), ("Count", 4), ("Entries", 8)]),
        [Nvapi + "NvVoltageBoostPercent"] = (40, [("Version", 0), ("Percent", 4), ("Reserved", 8)]),

        // --- NVAPI per-point V/F curve (clock-boost interfaces) -------------------
        [Nvapi + "NvClockMaskEntry"] = (24, [("ClockType", 0), ("Enabled", 4), ("Unknown", 5)]),
        [Nvapi + "NvClockMasks"] = (6188, [("Version", 0), ("Mask", 4), ("Unknown1", 36), ("Clocks", 68)]),
        [Nvapi + "NvVfpCurveEntry"] = (28, [("ClockType", 0), ("FrequencyKHz", 4), ("VoltageMicroV", 8), ("Unknown", 12)]),
        [Nvapi + "NvVfpCurve"] = (7208, [("Version", 0), ("Mask", 4), ("Unknown1", 36), ("Clocks", 68)]),
        [Nvapi + "NvClockTableEntry"] = (36, [("ClockType", 0), ("Unknown1", 4), ("FrequencyDeltaKHz", 20), ("Unknown2", 24)]),
        [Nvapi + "NvClockTable"] = (9248, [("Version", 0), ("Mask", 4), ("Unknown1", 36), ("Clocks", 68)]),

        // --- NVAPI DRS (driver settings store; strings are NvU16 / UTF-16) --------
        [Nvapi + "DrsApi+NvdrsProfile"] = (4116,
        [
            ("Version", 0), ("ProfileName", 4), ("GpuSupport", 4100), ("IsPredefined", 4104),
            ("NumOfApps", 4108), ("NumOfSettings", 4112),
        ]),
        [Nvapi + "DrsApi+NvdrsApplication"] = (20492,
        [
            ("Version", 0), ("IsPredefined", 4), ("AppName", 8), ("UserFriendlyName", 4104),
            ("Launcher", 8200), ("FileInFolder", 12296), ("Flags", 16392), ("CommandLine", 16396),
        ]),
        [Nvapi + "DrsApi+NvdrsSetting"] = (12320,
        [
            ("Version", 0), ("SettingName", 4), ("SettingId", 4100), ("SettingType", 4104),
            ("SettingLocation", 4108), ("IsCurrentPredefined", 4112), ("IsPredefinedValid", 4116),
            ("PredefinedValue", 4120), ("PredefinedPad", 4124), ("CurrentValue", 8220), ("CurrentPad", 8224),
        ]),

        // --- gdi32 D3DKMT adapter-address query (stress adapter binding) ----------
        [Stress + "StressAdapter+KmtOpenAdapterFromLuid"] = (12, [("LowPart", 0), ("HighPart", 4), ("AdapterHandle", 8)]),
        [Stress + "StressAdapter+KmtAdapterAddress"] = (12, [("BusNumber", 0), ("DeviceNumber", 4), ("FunctionNumber", 8)]),
        [Stress + "StressAdapter+KmtQueryAdapterInfo"] = (24,
        [
            ("AdapterHandle", 0), ("Type", 4), ("PrivateData", 8), ("PrivateDataSize", 16),
        ]),
        [Stress + "StressAdapter+KmtCloseAdapter"] = (4, [("AdapterHandle", 0)]),
    };

    public static TheoryData<string> StructNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in Expected.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StructNames))]
    public void Native_struct_keeps_its_exact_size_and_field_offsets(string typeName)
    {
        var (size, fields) = Expected[typeName];
        var (measuredSize, measuredOffsets) = Measure(typeName);

        Assert.Equal(size, measuredSize);

        // Every declared field must be pinned — a new field must come with a number.
        Assert.Equal(
            fields.Select(f => f.Field).Order(StringComparer.Ordinal),
            measuredOffsets.Keys.Order(StringComparer.Ordinal));
        foreach (var (field, offset) in fields)
        {
            Assert.True(measuredOffsets[field] == offset, $"{typeName}.{field}: expected offset {offset}, measured {measuredOffsets[field]}");
        }
    }

    [Fact]
    public void Every_interop_struct_in_scope_is_pinned()
    {
        // Guards against a new driver struct slipping in without a layout entry.
        var assembly = typeof(GpuManager).Assembly;
        // Driver structs are the Nv*/Kmt* value types; NvapiGpu's managed result
        // records (FanInfo, VfPoint, ...) never cross the boundary.
        var declared = assembly.GetTypes()
            .Where(t => t.IsValueType && !t.IsEnum &&
                        (t.Namespace is "Afterglow.Core.Interop.Nvml" or "Afterglow.Core.Interop.Nvapi" ||
                         t.DeclaringType?.FullName == "Afterglow.Core.Stress.StressAdapter") &&
                        (t.Name.StartsWith("Nv", StringComparison.Ordinal) || t.Name.StartsWith("Kmt", StringComparison.Ordinal)) &&
                        !IsArrayStorage(t))
            .Select(t => t.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(declared.Except(Expected.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void Nvapi_version_words_stamp_the_pinned_sizes()
    {
        Assert.Equal((uint)(168 | (2 << 16)), NvapiNative.MakeVersion<NvThermalSensors>(2));
        Assert.Equal((uint)(1704 | (1 << 16)), NvapiNative.MakeVersion<NvFanCoolersStatus>(1));
        Assert.Equal((uint)(1452 | (1 << 16)), NvapiNative.MakeVersion<NvFanCoolerControl>(1));
        Assert.Equal((uint)(76 | (1 << 16)), NvapiNative.MakeVersion<NvVoltRailsStatus>(1));
        Assert.Equal((uint)(72 | (1 << 16)), NvapiNative.MakeVersion<NvDynamicPstatesInfo>(1));
        Assert.Equal((uint)(104 | (2 << 16)), NvapiNative.MakeVersion<NvThermalPoliciesInfo>(2));
        Assert.Equal((uint)(56 | (2 << 16)), NvapiNative.MakeVersion<NvThermalPoliciesStatus>(2));
        Assert.Equal((uint)(40 | (1 << 16)), NvapiNative.MakeVersion<NvVoltageBoostPercent>(1));
        Assert.Equal((uint)(6188 | (1 << 16)), NvapiNative.MakeVersion<NvClockMasks>(1));
        Assert.Equal((uint)(7208 | (1 << 16)), NvapiNative.MakeVersion<NvVfpCurve>(1));
        Assert.Equal((uint)(9248 | (1 << 16)), NvapiNative.MakeVersion<NvClockTable>(1));
    }

    [Fact]
    public void Nvml_version_constants_match_the_pinned_sizes()
    {
        Assert.Equal(24u | (1u << 24), NvmlClockOffset.Version1);
        Assert.Equal(12u | (1u << 24), NvmlFanSpeedInfo.Version1);
        Assert.Equal(8u | (1u << 24), NvmlMarginTemperature.Version1);
    }

    // --- Measurement -----------------------------------------------------------

    /// <summary>[InlineArray] storage types: measured through the struct that embeds them.</summary>
    private static bool IsArrayStorage(Type type) =>
        type.GetCustomAttribute<System.Runtime.CompilerServices.InlineArrayAttribute>() is not null;

    /// <summary>
    /// Measures the layout the classic interop marshaller sends: Marshal.SizeOf /
    /// Marshal.OffsetOf over every instance field.
    /// </summary>
    private static (int Size, Dictionary<string, int> Offsets) Measure(string typeName)
    {
        var type = typeof(GpuManager).Assembly.GetType(typeName, throwOnError: true)!;
        var offsets = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .ToDictionary(f => f.Name, f => (int)Marshal.OffsetOf(type, f.Name), StringComparer.Ordinal);
        return (Marshal.SizeOf(type), offsets);
    }
}
