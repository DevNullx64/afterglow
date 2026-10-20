using Afterglow.Core.Metrics;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests;

/// <summary>
/// The telemetry averages a session report carries. FPS capture works on any
/// GPU, so stopping a capture must not depend on an NVIDIA card existing (F-18).
/// </summary>
public class SessionTelemetryTests
{
    private sealed class NoSource : ISensorSource
    {
        public NoSource(uint deviceIndex) => DeviceIndex = deviceIndex;

        public uint DeviceIndex { get; }

        public GpuSnapshot Poll() => throw new InvalidOperationException("never polled in these tests");
    }

    [Fact]
    public void No_nvidia_gpu_yields_empty_averages_instead_of_throwing()
    {
        // What AppServices builds when NVML finds nothing: zero pollers, so
        // there is no history for device 0 — the index RecordSession falls back to.
        using var telemetry = new TelemetryService([], TimeSpan.FromSeconds(1));

        var averages = SessionTelemetry.Since(telemetry, deviceIndex: 0, DateTimeOffset.MinValue);

        Assert.Equal(0, averages.Samples);
        Assert.Null(averages.PowerW);
    }

    [Fact]
    public void Averages_only_the_snapshots_taken_during_the_session()
    {
        using var telemetry = new TelemetryService([new NoSource(1)], TimeSpan.FromSeconds(1));
        var start = new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);
        var history = telemetry.TryHistoryFor(1)!;

        // Before the capture started: must not count.
        history.Add(new GpuSnapshot { Timestamp = start.AddSeconds(-5), DeviceIndex = 1, PowerW = 999, GpuTempC = 99 });
        history.Add(new GpuSnapshot { Timestamp = start, DeviceIndex = 1, PowerW = 300, GpuTempC = 60, MemJunctionTempC = 80 });
        history.Add(new GpuSnapshot { Timestamp = start.AddSeconds(1), DeviceIndex = 1, PowerW = 400, GpuTempC = 70, MemJunctionTempC = 90 });

        var averages = SessionTelemetry.Since(telemetry, deviceIndex: 1, start);

        Assert.Equal(2, averages.Samples);
        Assert.Equal(350, averages.PowerW!.Value, precision: 6);
        Assert.Equal(65, averages.GpuTempC!.Value, precision: 6);
        Assert.Equal(85, averages.MemJunctionC!.Value, precision: 6);
    }

    [Fact]
    public void Each_metric_averages_only_the_ticks_its_sensor_read_and_a_silent_one_is_null()
    {
        using var telemetry = new TelemetryService([new NoSource(1)], TimeSpan.FromSeconds(1));
        var history = telemetry.TryHistoryFor(1)!;
        var start = DateTimeOffset.UtcNow;

        // Power read on two ticks of three; memory junction never read.
        history.Add(new GpuSnapshot { Timestamp = start, DeviceIndex = 1, PowerW = 300, GpuTempC = 60 });
        history.Add(new GpuSnapshot { Timestamp = start.AddSeconds(1), DeviceIndex = 1, GpuTempC = 70 });
        history.Add(new GpuSnapshot { Timestamp = start.AddSeconds(2), DeviceIndex = 1, PowerW = 400, GpuTempC = 80 });

        var averages = SessionTelemetry.Since(telemetry, deviceIndex: 1, start);

        Assert.Equal(3, averages.Samples);
        Assert.Equal(350, averages.PowerW!.Value, precision: 6); // (300 + 400) / 2, not / 3
        Assert.Equal(70, averages.GpuTempC!.Value, precision: 6);
        Assert.Null(averages.MemJunctionC);                       // never read: "—", not 0 °C
    }

    [Fact]
    public void A_device_with_no_session_samples_reports_zero_samples()
    {
        using var telemetry = new TelemetryService([new NoSource(0)], TimeSpan.FromSeconds(1));

        var averages = SessionTelemetry.Since(telemetry, deviceIndex: 0, DateTimeOffset.UtcNow);

        Assert.Equal(default, averages);
    }
}
