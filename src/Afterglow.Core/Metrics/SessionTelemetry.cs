using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Metrics;

/// <summary>
/// Board power and temperatures averaged over one capture session. Each average
/// is null when its sensor never read during the session: a card without a
/// memory-junction sensor must export "—", not a measured 0 °C.
/// </summary>
public readonly record struct SessionTelemetryAverages(double? PowerW, double? GpuTempC, double? MemJunctionC, int Samples);

/// <summary>
/// The telemetry half of a session report. FPS capture is PresentMon/ETW and
/// needs no NVIDIA GPU at all, so the card's history is optional here: a
/// machine where NVML found nothing has no history to average, and that must
/// cost the report its telemetry columns — not the report itself.
/// </summary>
public static class SessionTelemetry
{
    /// <summary>
    /// Averages of the device's snapshots taken at or after <paramref name="since"/>.
    /// Zero samples (all averages null) when the device has no history — e.g. no
    /// supported GPU, where the lookup used to throw KeyNotFoundException and
    /// leave the capture impossible to stop (F-18). Each metric keeps its own
    /// counter, advanced only when that sensor actually read: one shared counter
    /// added 0 to a sum and 1 to its divisor for every tick a sensor was absent.
    /// </summary>
    public static SessionTelemetryAverages Since(TelemetryService telemetry, uint deviceIndex, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (telemetry.TryHistoryFor(deviceIndex) is not { } history)
        {
            return default;
        }

        double power = 0, gpuTemp = 0, memTemp = 0;
        int samples = 0, powerSamples = 0, gpuTempSamples = 0, memTempSamples = 0;
        foreach (var snapshot in history.GetAll())
        {
            if (snapshot.Timestamp < since)
            {
                continue;
            }

            samples++;
            if (snapshot.PowerW is { } w)
            {
                power += w;
                powerSamples++;
            }

            if (snapshot.GpuTempC is { } t)
            {
                gpuTemp += t;
                gpuTempSamples++;
            }

            if (snapshot.MemJunctionTempC is { } mj)
            {
                memTemp += mj;
                memTempSamples++;
            }
        }

        return new SessionTelemetryAverages(
            powerSamples > 0 ? power / powerSamples : null,
            gpuTempSamples > 0 ? gpuTemp / gpuTempSamples : null,
            memTempSamples > 0 ? memTemp / memTempSamples : null,
            samples);
    }
}
