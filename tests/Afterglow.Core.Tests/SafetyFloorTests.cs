using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Values the tuner takes from the driver and then acts on with no human in
/// the loop: the fixed-fan spin floor (F-22) and the defaults the panic/TDR
/// reset writes back (F-24). Each must fail safe when the driver's answer is
/// missing or implausible.
/// </summary>
public class SafetyFloorTests
{
    [Fact]
    public void A_failed_min_fan_query_keeps_the_conservative_floor()
    {
        // NVML writes the out value (0) even when the call fails; the old code
        // took it as the floor, so Fixed 5 % reached the fans as 5 %.
        Assert.Equal(30u, TuningMath.ResolveMinSpinDuty(querySucceeded: false, reportedMinPct: 0));
        Assert.Equal(30u, TuningMath.ResolveMinSpinDuty(querySucceeded: false, reportedMinPct: 45));
        Assert.Equal(30u, TuningMath.NormalizeFixedFanDuty(5, TuningMath.ResolveMinSpinDuty(false, 0)));

        // A stop request is still a stop request.
        Assert.Equal(0u, TuningMath.NormalizeFixedFanDuty(0, TuningMath.ResolveMinSpinDuty(false, 0)));
    }

    [Fact]
    public void Only_a_plausible_successful_report_replaces_the_floor()
    {
        Assert.Equal(30u, TuningMath.ResolveMinSpinDuty(true, 30)); // what the RTX 5090 reports
        Assert.Equal(45u, TuningMath.ResolveMinSpinDuty(true, 45));
        Assert.Equal(30u, TuningMath.ResolveMinSpinDuty(true, 0));
        Assert.Equal(30u, TuningMath.ResolveMinSpinDuty(true, 101));
    }

    [Fact]
    public void A_default_inside_the_drivers_own_range_is_written_back()
    {
        Assert.Equal(83, TuningMath.UsableDefault(83, 65, 90));
        Assert.Equal(575, TuningMath.UsableDefault(575, 400, 575)); // RTX 5090 power: default == max
        Assert.Equal(65, TuningMath.UsableDefault(65, 65, 90));
    }

    [Fact]
    public void A_missing_or_out_of_range_default_is_refused()
    {
        // F-24: a 0 C thermal target from a half-populated policy entry would pin
        // the GPU into throttling on the panic path; anything outside the same
        // driver's range is not a default worth trusting either.
        Assert.Null(TuningMath.UsableDefault(0, 65, 90));
        Assert.Null(TuningMath.UsableDefault(64, 65, 90));
        Assert.Null(TuningMath.UsableDefault(91, 65, 90));
        Assert.Null(TuningMath.UsableDefault(0, 400, 575)); // power default query failed
        Assert.Null(TuningMath.UsableDefault(80, 90, 65)); // inverted range
    }
}
