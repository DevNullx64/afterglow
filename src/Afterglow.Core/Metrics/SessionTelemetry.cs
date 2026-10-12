using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Metrics;

/// <summary>Board power and temperatures averaged over one capture session.</summary>
public readonly record struct SessionTelemetryAverages(double PowerW, double GpuTempC, double MemJunctionC, int Samples);

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
    /// Zero samples (all averages 0) when the device has no history — e.g. no
    /// NVIDIA GPU, where the lookup used to throw KeyNotFoundException and leave
    /// the capture impossible to stop (F-18).
    /// </summary>
    public static SessionTelemetryAverages Since(TelemetryService telemetry, uint deviceIndex, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (telemetry.TryHistoryFor(deviceIndex) is not { } history)
        {
            return default;
        }

        double power = 0;
        double gpuTemp = 0;
        double memTemp = 0;
        int samples = 0;
        foreach (var snapshot in history.GetAll())
        {
            if (snapshot.Timestamp < since)
            {
                continue;
            }

            power += snapshot.PowerW ?? 0;
            gpuTemp += snapshot.GpuTempC ?? 0;
            memTemp += snapshot.MemJunctionTempC ?? 0;
            samples++;
        }

        return samples == 0
            ? default
            : new SessionTelemetryAverages(power / samples, gpuTemp / samples, memTemp / samples, samples);
    }
}
