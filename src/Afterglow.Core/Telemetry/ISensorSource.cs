namespace Afterglow.Core.Telemetry;

/// <summary>
/// Anything that can produce <see cref="GpuSnapshot"/>s: the real NVML-backed
/// poller, or the demo source used for development, CI, and screenshots on
/// machines without NVIDIA hardware.
/// </summary>
public interface ISensorSource
{
    uint DeviceIndex { get; }

    GpuSnapshot Poll();

    /// <summary>
    /// Whether to read the sensors only the CSV log shows (on NVIDIA they include
    /// a ~40 ms blocking PCIe call). The app turns them off until CSV logging
    /// starts; a source with no such sensors ignores it.
    /// </summary>
    bool ExtendedMetrics
    {
        get => true;
        set { }
    }
}
