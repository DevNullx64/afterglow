using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Services;

public enum AlertSensor
{
    Gpu,
    MemJunction,
}

public sealed record TemperatureAlert(uint DeviceIndex, AlertSensor Sensor, double ValueC, int ThresholdC);

/// <summary>
/// Edge-triggered temperature alerts: one alert when a sensor crosses its
/// threshold, then silence until it has cooled <see cref="RearmMarginC"/>
/// below it. The previous design re-checked every tick and leaned on the
/// tray's rate limit to stop the spam — which, being shared by every
/// notification, let a card sitting above its alert line swallow automation
/// and fan-failure notices for as long as the game ran. Tracked per
/// (GPU, sensor). Called from the telemetry thread only; not thread-safe.
/// </summary>
public sealed class TemperatureAlertMonitor
{
    /// <summary>Cooling needed below the threshold before the alert can fire again.</summary>
    public const double RearmMarginC = 3;

    private readonly HashSet<(uint Device, AlertSensor Sensor)> _latched = [];

    /// <summary>
    /// Alerts newly raised by this snapshot (usually none). A threshold of 0
    /// means the alert is off.
    /// </summary>
    public IReadOnlyList<TemperatureAlert> Evaluate(GpuSnapshot snapshot, int gpuThresholdC, int memJunctionThresholdC)
    {
        List<TemperatureAlert>? raised = null;
        Check(snapshot.DeviceIndex, AlertSensor.Gpu, snapshot.GpuTempC, gpuThresholdC, ref raised);
        Check(snapshot.DeviceIndex, AlertSensor.MemJunction, snapshot.MemJunctionTempC, memJunctionThresholdC, ref raised);
        return raised is null ? [] : raised;
    }

    private void Check(uint device, AlertSensor sensor, double? value, int threshold, ref List<TemperatureAlert>? raised)
    {
        var key = (device, sensor);
        if (threshold <= 0 || value is not double v)
        {
            // Off, or the sensor went quiet: forget the latch so a later
            // reading above the line is news again.
            _latched.Remove(key);
            return;
        }

        if (v >= threshold)
        {
            if (_latched.Add(key))
            {
                (raised ??= []).Add(new TemperatureAlert(device, sensor, v, threshold));
            }
        }
        else if (v <= threshold - RearmMarginC)
        {
            _latched.Remove(key);
        }
    }
}
