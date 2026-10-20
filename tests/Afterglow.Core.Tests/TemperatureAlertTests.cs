using Afterglow.Core.Services;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests;

public sealed class TemperatureAlertTests
{
    private static GpuSnapshot Snap(uint? gpu, double? mem = null, uint device = 0) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        DeviceIndex = device,
        GpuTempC = gpu,
        MemJunctionTempC = mem,
    };

    [Fact]
    public void Fires_once_per_crossing_not_every_tick()
    {
        var monitor = new TemperatureAlertMonitor();

        Assert.Empty(monitor.Evaluate(Snap(79), 80, 0));
        var first = Assert.Single(monitor.Evaluate(Snap(80), 80, 0));
        Assert.Equal(AlertSensor.Gpu, first.Sensor);
        Assert.Equal(80, first.ValueC);

        // A card parked above the line for a whole session raises nothing more.
        for (uint t = 81; t < 90; t++)
        {
            Assert.Empty(monitor.Evaluate(Snap(t), 80, 0));
        }
    }

    [Fact]
    public void Rearms_only_after_cooling_past_the_margin()
    {
        var monitor = new TemperatureAlertMonitor();
        Assert.Single(monitor.Evaluate(Snap(82), 80, 0));

        // Hovering just under the line is not a new event...
        Assert.Empty(monitor.Evaluate(Snap(78), 80, 0));
        Assert.Empty(monitor.Evaluate(Snap(81), 80, 0));

        // ...cooling 3 °C below it re-arms.
        Assert.Empty(monitor.Evaluate(Snap(77), 80, 0));
        Assert.Single(monitor.Evaluate(Snap(80), 80, 0));
    }

    [Fact]
    public void Sensors_and_gpus_latch_independently()
    {
        var monitor = new TemperatureAlertMonitor();

        var both = monitor.Evaluate(Snap(85, 96), 80, 95);
        Assert.Equal(2, both.Count);

        // The second card is news even though the first is still latched.
        var other = Assert.Single(monitor.Evaluate(Snap(85, null, device: 1), 80, 95));
        Assert.Equal(1u, other.DeviceIndex);

        // Memory cools and re-arms while the GPU sensor stays latched.
        Assert.Empty(monitor.Evaluate(Snap(85, 90), 80, 95));
        var mem = Assert.Single(monitor.Evaluate(Snap(85, 97), 80, 95));
        Assert.Equal(AlertSensor.MemJunction, mem.Sensor);
    }

    [Fact]
    public void Disabled_thresholds_and_missing_sensors_never_fire()
    {
        var monitor = new TemperatureAlertMonitor();
        Assert.Empty(monitor.Evaluate(Snap(99, 110), 0, 0));
        Assert.Empty(monitor.Evaluate(Snap(null, null), 80, 95));
    }

    [Fact]
    public void Turning_an_alert_off_and_on_again_reports_the_current_breach()
    {
        var monitor = new TemperatureAlertMonitor();
        Assert.Single(monitor.Evaluate(Snap(85), 80, 0));
        Assert.Empty(monitor.Evaluate(Snap(85), 0, 0));
        Assert.Single(monitor.Evaluate(Snap(85), 80, 0));
    }
}
