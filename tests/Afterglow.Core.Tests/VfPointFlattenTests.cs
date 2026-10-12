using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// What a flatten must guarantee about the whole curve, not just the points
/// at and above the anchor (F-11), and the per-point driver-limit refusal the
/// suite never reached before (F-60).
/// </summary>
public class VfPointFlattenTests
{
    // The same Ampere-ish fixture as VfPointPlannerTests: 700..1050 mV in 25 mV
    // steps, 1300..2100 MHz (850 mV = 1643, 875 mV = 1700, 1025 mV = 2043).
    private static List<NvapiGpu.VfpTablePoint> Curve()
    {
        var points = new List<NvapiGpu.VfpTablePoint>();
        int index = 10;
        for (double mv = 700; mv <= 1050; mv += 25)
        {
            double mhz = 1300 + ((mv - 700) / 350.0 * 800);
            points.Add(new NvapiGpu.VfpTablePoint(index++, mv, Math.Round(mhz), 0));
        }

        return points;
    }

    /// <summary>A 5090-shaped table: 600..1100 mV linear to 400..3000 MHz, 21 points.</summary>
    private static List<NvapiGpu.VfpTablePoint> WideCurve()
    {
        var points = new List<NvapiGpu.VfpTablePoint>();
        for (int i = 0; i <= 20; i++)
        {
            double mv = 600 + (i * 25);
            points.Add(new NvapiGpu.VfpTablePoint(i, mv, 400 + ((mv - 600) / 500.0 * 2600), 0));
        }

        return points;
    }

    private static double Effective(NvapiGpu.VfpTablePoint point, VfPointPlanner.FlattenPlan plan) =>
        point.ClockMHz + plan.OffsetsMHz[point.Index];

    [Fact]
    public void A_target_below_the_stored_curve_caps_the_lower_points_too()
    {
        // The finding's scenario: flatten at 875 mV to 1500 MHz. The old planner
        // zeroed every lower point, leaving 1643 MHz at 850 mV — a higher clock
        // at a LOWER voltage than the cap the user just pinned.
        var curve = Curve();
        var plan = VfPointPlanner.PlanFlatten(curve, 875, 1500, out string? refusal);

        Assert.Null(refusal);
        Assert.NotNull(plan);
        foreach (var point in curve)
        {
            Assert.True(Effective(point, plan) <= 1500,
                $"{point.VoltageMv} mV would run {Effective(point, plan)} MHz, above the 1500 MHz target");
        }

        Assert.Equal(1500 - 1643, plan.OffsetsMHz[curve.Single(p => p.VoltageMv == 850).Index]);

        // Lower points stock already keeps at or under the target stay stock.
        foreach (var point in curve.Where(p => p.VoltageMv < 875 && p.ClockMHz <= 1500))
        {
            Assert.Equal(0, plan.OffsetsMHz[point.Index]);
        }

        // 850/825/800 mV store 1643/1586/1529 MHz — three points brought down.
        Assert.Equal(3, plan.LowerPointsCapped);
    }

    [Fact]
    public void Anchoring_on_the_top_point_still_holds_the_whole_curve_to_the_target()
    {
        // The degenerate case from the finding: nothing above the anchor to
        // cap (PointsFlattened = 0), and the old planner left 2043 MHz
        // reachable at 1025 mV while reporting a successful flatten to 1500.
        var curve = Curve();
        var plan = VfPointPlanner.PlanFlatten(curve, 1050, 1500, out _);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.PointsFlattened);
        Assert.Equal(1500, curve.Max(p => Effective(p, plan)));
        Assert.Equal(curve.Count(p => p.VoltageMv < 1050 && p.ClockMHz > 1500), plan.LowerPointsCapped);
    }

    [Fact]
    public void No_successful_plan_ever_leaves_a_point_above_the_target()
    {
        // Every stored voltage as the anchor, targets on both sides of the curve:
        // whatever the planner accepts, the anchor lands on the target and no
        // point anywhere exceeds it. Rounding is to whole MHz on integer clocks.
        var curve = Curve();
        int checkedPlans = 0;
        foreach (var anchor in curve)
        {
            for (double target = 1000; target <= 2800; target += 50)
            {
                if (VfPointPlanner.PlanFlatten(curve, anchor.VoltageMv, target, out _) is not { } plan)
                {
                    continue;
                }

                checkedPlans++;
                Assert.Equal(target, Effective(anchor, plan));
                Assert.All(curve, point => Assert.True(Effective(point, plan) <= target));
                Assert.Equal(curve.Count, plan.OffsetsMHz.Count); // every point written: deterministic
            }
        }

        Assert.True(checkedPlans > 100, $"only {checkedPlans} plans exercised");
    }

    [Fact]
    public void An_upward_flatten_still_returns_every_lower_point_to_stock()
    {
        var curve = Curve();
        var plan = VfPointPlanner.PlanFlatten(curve, 875, 1875, out _);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.LowerPointsCapped);
        Assert.All(curve.Where(p => p.VoltageMv < 875), p => Assert.Equal(0, plan.OffsetsMHz[p.Index]));
    }

    [Fact]
    public void A_point_needing_more_than_the_driver_limit_refuses_the_whole_plan()
    {
        // F-60: reachable on a realistic wide table and never exercised before —
        // deleting the guard kept the suite green. Anchor 700 mV stores 920 MHz
        // (+80, inside the planner limit) but capping 1025 mV (2610 MHz) to
        // 1000 MHz needs -1610, past the driver's ±1500. Nothing may be planned.
        var plan = VfPointPlanner.PlanFlatten(WideCurve(), 700, 1000, out string? refusal);

        Assert.Null(plan);
        Assert.Equal(
            "Capping the 1025 mV point to 1000 MHz needs -1610 MHz — outside the driver's ±1500 MHz range.",
            refusal);
    }

    [Fact]
    public void The_driver_limit_is_exactly_1500_MHz_per_point()
    {
        // Anchor delta 0; the top point needs exactly -1500 (accepted) or -1501
        // (refused). Pins VfpOffsetLimitMHz as the bound the planner enforces.
        List<NvapiGpu.VfpTablePoint> Table(double top) =>
            [new(0, 700, 1000, 0), new(1, 800, 1500, 0), new(2, 900, top, 0)];

        var atLimit = VfPointPlanner.PlanFlatten(Table(2500), 700, 1000, out string? none);
        Assert.Null(none);
        Assert.NotNull(atLimit);
        Assert.Equal(-1500, atLimit.OffsetsMHz[2]);

        Assert.Null(VfPointPlanner.PlanFlatten(Table(2501), 700, 1000, out string? refusal));
        Assert.Equal(
            "Capping the 900 mV point to 1000 MHz needs -1501 MHz — outside the driver's ±1500 MHz range.",
            refusal);
    }
}
