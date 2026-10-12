using System.Reflection;
using System.Runtime.CompilerServices;
using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests;

/// <summary>
/// Pins the exact wire layout (size and every field offset) of every struct
/// Afterglow hands to nvml.dll, nvapi64.dll, and gdi32.dll. The numbers are
/// hard-coded on purpose: they were measured from the marshalled layouts the
/// app shipped with (Marshal.SizeOf / Marshal.OffsetOf, before the structs
/// became blittable), and a struct that drifts by one byte would send garbage
/// to GPU firmware on a SET call. Cross-checks against independently known
/// sizes: clock-boost mask / VFP curve / clock table 6188 / 7208 / 9248 bytes,
/// DRS PROFILE v1 4116, APPLICATION v4 20492, SETTING v1 12320.
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

    [Fact]
    public void Core_assembly_never_runs_the_runtime_marshaller()
    {
        // With runtime marshalling disabled, every struct crosses as its exact
        // managed bytes and `char` is UTF-16 — the premise of the measurement below.
        Assert.NotNull(typeof(GpuManager).Assembly.GetCustomAttribute<DisableRuntimeMarshallingAttribute>());
    }

    [Fact]
    public unsafe void Drs_strings_are_utf16_on_the_wire()
    {
        // NvAPI_UnicodeString is NvU16[2048]. The classic marshaller used to send
        // only the first character of a `fixed char` buffer, as one ANSI byte.
        var profile = default(DrsApi.NvdrsProfile);
        "Base Profile".AsSpan().CopyTo(new Span<char>(profile.ProfileName, 2048));
        var wire = new ReadOnlySpan<byte>(&profile, sizeof(DrsApi.NvdrsProfile));

        Assert.Equal(4116, wire.Length);
        Assert.Equal("Base Profile\0", System.Text.Encoding.Unicode.GetString(wire.Slice(4, 26)));
    }

    // --- Measurement -----------------------------------------------------------

    /// <summary>[InlineArray] storage types: measured through the struct that embeds them.</summary>
    private static bool IsArrayStorage(Type type) => type.GetCustomAttribute<InlineArrayAttribute>() is not null;

    /// <summary>
    /// Measures the layout that actually crosses the boundary. Every interop
    /// struct is unmanaged and passed by pointer with runtime marshalling
    /// disabled, so the wire layout IS the managed layout: Unsafe.SizeOf plus
    /// Unsafe.ByteOffset from the struct's first byte to each field. Every
    /// declared instance field must be probed, so a new field cannot go unmeasured.
    /// </summary>
    private static (int Size, Dictionary<string, int> Offsets) Measure(string typeName)
    {
        var (size, offsets) = Probe(typeName);
        var declared = typeof(GpuManager).Assembly.GetType(typeName, throwOnError: true)!
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => f.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(declared, offsets.Keys.Order(StringComparer.Ordinal));
        return (size, offsets);
    }

    private static unsafe (int Size, Dictionary<string, int> Offsets) Probe(string typeName) => typeName switch
    {
        Nvml + "NvmlUtilization" => Of<NvmlUtilization>(p => p
            .At("Gpu", ref p.Value.Gpu).At("Memory", ref p.Value.Memory)),
        Nvml + "NvmlMemory" => Of<NvmlMemory>(p => p
            .At("Total", ref p.Value.Total).At("Free", ref p.Value.Free).At("Used", ref p.Value.Used)),
        Nvml + "NvmlBar1Memory" => Of<NvmlBar1Memory>(p => p
            .At("Total", ref p.Value.Total).At("Free", ref p.Value.Free).At("Used", ref p.Value.Used)),
        Nvml + "NvmlClockOffset" => Of<NvmlClockOffset>(p => p
            .At("Version", ref p.Value.Version).At("Type", ref p.Value.Type).At("Pstate", ref p.Value.Pstate)
            .At("ClockOffsetMHz", ref p.Value.ClockOffsetMHz).At("MinClockOffsetMHz", ref p.Value.MinClockOffsetMHz)
            .At("MaxClockOffsetMHz", ref p.Value.MaxClockOffsetMHz)),
        Nvml + "NvmlFanSpeedInfo" => Of<NvmlFanSpeedInfo>(p => p
            .At("Version", ref p.Value.Version).At("Fan", ref p.Value.Fan).At("SpeedRpm", ref p.Value.SpeedRpm)),
        Nvml + "NvmlMarginTemperature" => Of<NvmlMarginTemperature>(p => p
            .At("Version", ref p.Value.Version).At("MarginTemperatureC", ref p.Value.MarginTemperatureC)),
        Nvml + "NvmlFieldValue" => Of<NvmlFieldValue>(p => p
            .At("FieldId", ref p.Value.FieldId).At("ScopeId", ref p.Value.ScopeId).At("Timestamp", ref p.Value.Timestamp)
            .At("LatencyUsec", ref p.Value.LatencyUsec).At("ValueType", ref p.Value.ValueType)
            .At("Status", ref p.Value.Status).At("Value", ref p.Value.Value)),
        Nvml + "NvmlPciInfo" => Of<NvmlPciInfo>(p => p
            .At("BusIdLegacy", ref p.Value.BusIdLegacy[0]).At("Domain", ref p.Value.Domain).At("Bus", ref p.Value.Bus)
            .At("Device", ref p.Value.Device).At("PciDeviceId", ref p.Value.PciDeviceId)
            .At("PciSubSystemId", ref p.Value.PciSubSystemId).At("BusId", ref p.Value.BusId[0])),

        Nvapi + "NvThermalSensors" => Of<NvThermalSensors>(p => p
            .At("Version", ref p.Value.Version).At("Mask", ref p.Value.Mask)
            .At("Reserved", ref p.Value.Reserved[0]).At("Temperatures", ref p.Value.Temperatures[0])),
        Nvapi + "NvFanCoolersStatusItem" => Of<NvFanCoolersStatusItem>(p => p
            .At("CoolerId", ref p.Value.CoolerId).At("CurrentRpm", ref p.Value.CurrentRpm)
            .At("CurrentMinLevel", ref p.Value.CurrentMinLevel).At("CurrentMaxLevel", ref p.Value.CurrentMaxLevel)
            .At("CurrentLevel", ref p.Value.CurrentLevel).At("Reserved", ref p.Value.Reserved[0])),
        Nvapi + "NvFanCoolersStatus" => Of<NvFanCoolersStatus>(p => p
            .At("Version", ref p.Value.Version).At("Count", ref p.Value.Count)
            .At("Reserved1", ref p.Value.Reserved1).At("Reserved2", ref p.Value.Reserved2)
            .At("Reserved3", ref p.Value.Reserved3).At("Reserved4", ref p.Value.Reserved4).At("Items", ref p.Value.Items)),
        Nvapi + "NvFanCoolerControlItem" => Of<NvFanCoolerControlItem>(p => p
            .At("CoolerId", ref p.Value.CoolerId).At("Level", ref p.Value.Level)
            .At("ControlMode", ref p.Value.ControlMode).At("Reserved", ref p.Value.Reserved[0])),
        Nvapi + "NvFanCoolerControl" => Of<NvFanCoolerControl>(p => p
            .At("Version", ref p.Value.Version).At("Reserved", ref p.Value.Reserved).At("Count", ref p.Value.Count)
            .At("Reserved2", ref p.Value.Reserved2[0]).At("Items", ref p.Value.Items)),
        Nvapi + "NvVoltRailsStatus" => Of<NvVoltRailsStatus>(p => p
            .At("Version", ref p.Value.Version).At("CoreMicrovolts", ref p.Value.CoreMicrovolts)
            .At("ReservedAfterCoreMicrovolts", ref p.Value.ReservedAfterCoreMicrovolts)),
        Nvapi + "NvDynamicPstate" => Of<NvDynamicPstate>(p => p
            .At("IsPresent", ref p.Value.IsPresent).At("Percentage", ref p.Value.Percentage)),
        Nvapi + "NvDynamicPstatesInfo" => Of<NvDynamicPstatesInfo>(p => p
            .At("Version", ref p.Value.Version).At("Flags", ref p.Value.Flags).At("Utilizations", ref p.Value.Utilizations)),
        Nvapi + "NvThermalPoliciesInfoEntry" => Of<NvThermalPoliciesInfoEntry>(p => p
            .At("Controller", ref p.Value.Controller).At("Unknown1", ref p.Value.Unknown1)
            .At("MinimumTemp", ref p.Value.MinimumTemp).At("DefaultTemp", ref p.Value.DefaultTemp)
            .At("MaximumTemp", ref p.Value.MaximumTemp).At("Unknown2", ref p.Value.Unknown2)),
        Nvapi + "NvThermalPoliciesInfo" => Of<NvThermalPoliciesInfo>(p => p
            .At("Version", ref p.Value.Version).At("Count", ref p.Value.Count)
            .At("Unknown", ref p.Value.Unknown).At("Entries", ref p.Value.Entries)),
        Nvapi + "NvThermalPoliciesStatusEntry" => Of<NvThermalPoliciesStatusEntry>(p => p
            .At("Controller", ref p.Value.Controller).At("TargetTemp", ref p.Value.TargetTemp)
            .At("PstateId", ref p.Value.PstateId)),
        Nvapi + "NvThermalPoliciesStatus" => Of<NvThermalPoliciesStatus>(p => p
            .At("Version", ref p.Value.Version).At("Count", ref p.Value.Count).At("Entries", ref p.Value.Entries)),
        Nvapi + "NvVoltageBoostPercent" => Of<NvVoltageBoostPercent>(p => p
            .At("Version", ref p.Value.Version).At("Percent", ref p.Value.Percent).At("Reserved", ref p.Value.Reserved[0])),

        Nvapi + "NvClockMaskEntry" => Of<NvClockMaskEntry>(p => p
            .At("ClockType", ref p.Value.ClockType).At("Enabled", ref p.Value.Enabled).At("Unknown", ref p.Value.Unknown[0])),
        Nvapi + "NvClockMasks" => Of<NvClockMasks>(p => p
            .At("Version", ref p.Value.Version).At("Mask", ref p.Value.Mask[0])
            .At("Unknown1", ref p.Value.Unknown1[0]).At("Clocks", ref p.Value.Clocks)),
        Nvapi + "NvVfpCurveEntry" => Of<NvVfpCurveEntry>(p => p
            .At("ClockType", ref p.Value.ClockType).At("FrequencyKHz", ref p.Value.FrequencyKHz)
            .At("VoltageMicroV", ref p.Value.VoltageMicroV).At("Unknown", ref p.Value.Unknown[0])),
        Nvapi + "NvVfpCurve" => Of<NvVfpCurve>(p => p
            .At("Version", ref p.Value.Version).At("Mask", ref p.Value.Mask[0])
            .At("Unknown1", ref p.Value.Unknown1[0]).At("Clocks", ref p.Value.Clocks)),
        Nvapi + "NvClockTableEntry" => Of<NvClockTableEntry>(p => p
            .At("ClockType", ref p.Value.ClockType).At("Unknown1", ref p.Value.Unknown1[0])
            .At("FrequencyDeltaKHz", ref p.Value.FrequencyDeltaKHz).At("Unknown2", ref p.Value.Unknown2[0])),
        Nvapi + "NvClockTable" => Of<NvClockTable>(p => p
            .At("Version", ref p.Value.Version).At("Mask", ref p.Value.Mask[0])
            .At("Unknown1", ref p.Value.Unknown1[0]).At("Clocks", ref p.Value.Clocks)),

        Nvapi + "DrsApi+NvdrsProfile" => Of<DrsApi.NvdrsProfile>(p => p
            .At("Version", ref p.Value.Version).At("ProfileName", ref p.Value.ProfileName[0])
            .At("GpuSupport", ref p.Value.GpuSupport).At("IsPredefined", ref p.Value.IsPredefined)
            .At("NumOfApps", ref p.Value.NumOfApps).At("NumOfSettings", ref p.Value.NumOfSettings)),
        Nvapi + "DrsApi+NvdrsApplication" => Of<DrsApi.NvdrsApplication>(p => p
            .At("Version", ref p.Value.Version).At("IsPredefined", ref p.Value.IsPredefined)
            .At("AppName", ref p.Value.AppName[0]).At("UserFriendlyName", ref p.Value.UserFriendlyName[0])
            .At("Launcher", ref p.Value.Launcher[0]).At("FileInFolder", ref p.Value.FileInFolder[0])
            .At("Flags", ref p.Value.Flags).At("CommandLine", ref p.Value.CommandLine[0])),
        Nvapi + "DrsApi+NvdrsSetting" => Of<DrsApi.NvdrsSetting>(p => p
            .At("Version", ref p.Value.Version).At("SettingName", ref p.Value.SettingName[0])
            .At("SettingId", ref p.Value.SettingId).At("SettingType", ref p.Value.SettingType)
            .At("SettingLocation", ref p.Value.SettingLocation).At("IsCurrentPredefined", ref p.Value.IsCurrentPredefined)
            .At("IsPredefinedValid", ref p.Value.IsPredefinedValid).At("PredefinedValue", ref p.Value.PredefinedValue)
            .At("PredefinedPad", ref p.Value.PredefinedPad[0]).At("CurrentValue", ref p.Value.CurrentValue)
            .At("CurrentPad", ref p.Value.CurrentPad[0])),

        Stress + "StressAdapter+KmtOpenAdapterFromLuid" => Of<StressAdapter.KmtOpenAdapterFromLuid>(p => p
            .At("LowPart", ref p.Value.LowPart).At("HighPart", ref p.Value.HighPart)
            .At("AdapterHandle", ref p.Value.AdapterHandle)),
        Stress + "StressAdapter+KmtAdapterAddress" => Of<StressAdapter.KmtAdapterAddress>(p => p
            .At("BusNumber", ref p.Value.BusNumber).At("DeviceNumber", ref p.Value.DeviceNumber)
            .At("FunctionNumber", ref p.Value.FunctionNumber)),
        Stress + "StressAdapter+KmtQueryAdapterInfo" => Of<StressAdapter.KmtQueryAdapterInfo>(p => p
            .At("AdapterHandle", ref p.Value.AdapterHandle).At("Type", ref p.Value.Type)
            .At("PrivateData", ref p.Value.PrivateData).At("PrivateDataSize", ref p.Value.PrivateDataSize)),
        Stress + "StressAdapter+KmtCloseAdapter" => Of<StressAdapter.KmtCloseAdapter>(p => p
            .At("AdapterHandle", ref p.Value.AdapterHandle)),

        _ => throw new ArgumentOutOfRangeException(nameof(typeName), typeName, "no layout probe for this struct"),
    };

    private static (int Size, Dictionary<string, int> Offsets) Of<T>(Func<LayoutProbe<T>, LayoutProbe<T>> fields)
        where T : unmanaged
    {
        var probe = fields(new LayoutProbe<T>());
        return (Unsafe.SizeOf<T>(), probe.Offsets);
    }

    /// <summary>Holds one zeroed T and records Unsafe.ByteOffset from its first byte to each named field.</summary>
    private sealed class LayoutProbe<T> where T : unmanaged
    {
#pragma warning disable CA1051 // the probe exposes its value so callers can take field refs
        public T Value;
#pragma warning restore CA1051

        public Dictionary<string, int> Offsets { get; } = new(StringComparer.Ordinal);

        public LayoutProbe<T> At<TField>(string field, ref TField location)
        {
            Offsets.Add(field, (int)Unsafe.ByteOffset(ref Unsafe.As<T, byte>(ref Value), ref Unsafe.As<TField, byte>(ref location)));
            return this;
        }
    }
}
