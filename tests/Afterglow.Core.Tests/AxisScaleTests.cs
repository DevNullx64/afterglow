using Afterglow.App.Controls;

namespace Afterglow.Core.Tests;

/// <summary>
/// The graphs' round-number value axis (src/Afterglow.App/Controls/AxisScale.cs, compiled
/// in like WindowEffectsPlan). Expected values are worked by hand in each comment.
/// </summary>
public sealed class AxisScaleTests
{
    [Fact]
    public void Automatic_range_snaps_outward_to_a_round_step_with_at_most_five_intervals()
    {
        // Demo core clock 1,108..3,005 MHz padded 4 % = 1,032.12..3,080.88. Span/5 = 409.8, so
        // steps of 100, 200, 250 need 21, 11, 9 intervals; 500 needs 1,000..3,500 = 5.
        var (min, max, step) = AxisScale.Nice(1032.12, 3080.88, double.NaN, double.NaN);

        Assert.Equal(1000, min);
        Assert.Equal(3500, max);
        Assert.Equal(500, step);
    }

    [Fact]
    public void Fractional_steps_are_allowed_for_small_ranges()
    {
        // Frametimes 2.748..12.252 ms: step 1 → 2..13 (11), 2 → 2..14 (6), 2.5 → 2.5..12.5 (4).
        var (min, max, step) = AxisScale.Nice(2.748, 12.252, double.NaN, double.NaN);

        Assert.Equal(2.5, min, 9);
        Assert.Equal(12.5, max, 9);
        Assert.Equal(2.5, step, 9);
    }

    [Fact]
    public void Both_bounds_fixed_keeps_them_and_picks_a_step_that_divides_the_range()
    {
        // 0..100 %: step 10 gives 10 intervals (> 5); 20 gives exactly 5.
        Assert.Equal((0d, 100d, 20d), AxisScale.Nice(0, 100, 0, 100));

        // 20..100 °C: 10 → 8 intervals; 20 → 4.
        Assert.Equal((20d, 100d, 20d), AxisScale.Nice(20, 100, 20, 100));
    }

    [Fact]
    public void Both_bounds_fixed_with_no_round_divisor_falls_back_to_quarters()
    {
        // 25..95: 70 / {10, 20, 25, 50, 100} = 7, 3.5, 2.8, 1.4, 0.7 — none fits.
        Assert.Equal((25d, 95d, 17.5), AxisScale.Nice(25, 95, 25, 95));
    }

    [Fact]
    public void Fixed_lower_bound_stays_put_and_the_top_rounds_up()
    {
        // VRAM 0..13.6 GB with FixedMin 0: step 1 → 14, 2 → 7, 2.5 → 6, 5 → 0..15 (3).
        Assert.Equal((0d, 15d, 5d), AxisScale.Nice(0, 13.6, 0, double.NaN));
    }

    [Fact]
    public void Fixed_upper_bound_stays_put_and_the_bottom_rounds_down()
    {
        // 37..100 with FixedMax 100: step 10 → 30..100 (7); 20 → 20..100 (4).
        Assert.Equal((20d, 100d, 20d), AxisScale.Nice(37, 100, double.NaN, 100));
    }

    [Fact]
    public void Ranges_crossing_zero_round_on_both_sides()
    {
        // −5..5: step 1 → 10 intervals; 2 → −6..6 (6); 2.5 → −5..5 (4).
        Assert.Equal((-5d, 5d, 2.5), AxisScale.Nice(-5, 5, double.NaN, double.NaN));
    }
}
