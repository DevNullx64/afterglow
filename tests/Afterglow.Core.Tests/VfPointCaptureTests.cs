using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Save → re-apply must reproduce the saved curve (F-02/F-07). Re-apply is
/// modelled the way GpuTuner.Apply performs it: write the core offset, then
/// write only the slots the profile carries; every other slot keeps what the
/// table held. Both table models are checked — the measured one (RTX 5090 /
/// 616.56: the core-offset write levels every slot to it) and one where the
/// core offset does not touch the table at all — because the capture must not
/// depend on which one a given driver follows.
/// </summary>
public class VfPointCaptureTests
{
    private const int CoreOffset = 150;

    // The planner fixture: 700..1050 mV, 1300..2100 MHz, slots 10..24.
    private static List<NvapiGpu.VfpTablePoint> Stock()
    {
        var points = new List<NvapiGpu.VfpTablePoint>();
        int index = 10;
        for (double mv = 700; mv <= 1050; mv += 25)
        {
            points.Add(new NvapiGpu.VfpTablePoint(index++, mv, Math.Round(1300 + ((mv - 700) / 350.0 * 800)), 0));
        }

        return points;
    }

    private static List<NvapiGpu.VfpTablePoint> WithOffsets(
        List<NvapiGpu.VfpTablePoint> table, Func<NvapiGpu.VfpTablePoint, int> offset) =>
        table.Select(p => p with { OffsetMHz = offset(p) }).ToList();

    /// <summary>The table after a flatten at 875 mV → 1875 MHz written over a +150 core offset.</summary>
    private static List<NvapiGpu.VfpTablePoint> Flattened()
    {
        var stock = Stock();
        var plan = VfPointPlanner.PlanFlatten(stock, 875, 1875, out _)!;
        return WithOffsets(stock, p => plan.OffsetsMHz[p.Index]);
    }

    private static Dictionary<int, int> Replay(
        List<NvapiGpu.VfpTablePoint> tableAtApply,
        IReadOnlyDictionary<int, int>? carried,
        int coreOffset,
        bool coreOffsetLevelsTable)
    {
        var slots = tableAtApply.ToDictionary(p => p.Index, p => coreOffsetLevelsTable ? coreOffset : p.OffsetMHz);
        foreach (var (index, offset) in carried ?? new Dictionary<int, int>())
        {
            slots[index] = offset;
        }

        return slots;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_saved_flatten_reapplies_exactly_including_its_explicit_zeros(bool coreOffsetLevelsTable)
    {
        // The finding's scenario: core offset +150 applied, then a flatten that
        // writes 0 to every point below 875 mV. Saving used to keep only the
        // non-zero slots, so re-apply left those points at +150 (or at whatever
        // they held) while reporting the carried slots as verified.
        var saved = Flattened();
        var carried = VfPointPlanner.CaptureForProfile(saved, CoreOffset);

        Assert.NotNull(carried);
        Assert.Equal(saved.Count, carried.Count);
        Assert.All(saved.Where(p => p.VoltageMv < 875), p => Assert.Equal(0, carried[p.Index]));

        // Re-apply onto a table that has since drifted: a later manual +200 on
        // the 800 mV point (the corrected statement's hardware-free variant).
        var drifted = WithOffsets(saved, p => p.VoltageMv == 800 ? 200 : p.OffsetMHz);
        var replayed = Replay(drifted, carried, CoreOffset, coreOffsetLevelsTable);

        Assert.Equal(saved.ToDictionary(p => p.Index, p => p.OffsetMHz), replayed);
    }

    [Fact]
    public void A_table_holding_only_the_global_core_offset_records_no_points()
    {
        // Re-applying the core offset alone reproduces it; carrying 15 copies of
        // +150 would only pin a stale value over a later core-offset change.
        var uniform = WithOffsets(Stock(), _ => CoreOffset);

        Assert.Null(VfPointPlanner.CaptureForProfile(uniform, CoreOffset));
        Assert.Null(VfPointPlanner.CaptureForProfile(Stock(), 0));
    }

    [Fact]
    public void A_uniform_edit_that_is_not_the_core_offset_is_recorded_whole()
    {
        // Every point set to -50 by hand while the driver reports a 0 MHz core
        // offset: no "shape", but re-applying a 0 offset would lose it.
        var edited = WithOffsets(Stock(), _ => -50);
        var carried = VfPointPlanner.CaptureForProfile(edited, 0);

        Assert.NotNull(carried);
        Assert.Equal(edited.Count, carried.Count);
        Assert.All(carried.Values, v => Assert.Equal(-50, v));
    }

    [Fact]
    public void A_captured_table_passes_profile_validation()
    {
        var profile = new Profiles.TuningProfile
        {
            Name = "flatten",
            CoreOffsetMHz = CoreOffset,
            VfPointOffsetsMHz = VfPointPlanner.CaptureForProfile(Flattened(), CoreOffset),
            CapturedVfPoints = true,
        };

        Assert.Null(profile.Validate());
    }
}
