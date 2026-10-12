using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The verdict on a clear-all, judged from the table read back after it
/// (F-06/F-43/F-57). GpuTuner.ClearVfPointOffsets and ResetToDefaults both
/// report exactly this; neither may call a clear "ok" on the driver's return
/// code alone any more.
/// </summary>
public class VfPointClearTests
{
    private static NvapiGpu.VfpTablePoint P(int index, int offset) => new(index, 800 + index, 1500 + index, offset);

    private static List<NvapiGpu.VfpTablePoint> Uniform(int offset, int count = 5) =>
        Enumerable.Range(0, count).Select(i => P(i, offset)).ToList();

    [Fact]
    public void A_clear_the_driver_accepted_but_did_not_apply_fails()
    {
        // The case the readback exists for: NVAPI_OK, offsets still live. The
        // old path printed "ok — cleared" and exit 0 here.
        var after = Uniform(0);
        after[2] = P(2, -150);

        var result = VfPointPlanner.VerifyClear(NvapiStatus.Ok, after, globalCoreOffsetMHz: 0);

        Assert.False(result.Applied);
        Assert.Equal(
            "the driver accepted the clear, but per-point offsets are still in force: 1 of 5 points differ from the 0 MHz the rest read",
            result.Detail);
    }

    [Fact]
    public void The_measured_outcome_is_verified_and_names_the_surviving_global_offset()
    {
        // RTX 5090 / 616.56: with a global +100 applied, the table read +100 on
        // every point after the clear. That is a successful clear, and the
        // report says the global offset is still there (F-52).
        var result = VfPointPlanner.VerifyClear(NvapiStatus.Ok, Uniform(100), globalCoreOffsetMHz: 100);

        Assert.True(result.Applied);
        Assert.Equal(
            "cleared (verified: no per-point offsets left; every point reads the +100 MHz global core offset, which the clear left in place)",
            result.Detail);
    }

    [Fact]
    public void A_stock_table_after_a_clear_is_verified()
    {
        var result = VfPointPlanner.VerifyClear(NvapiStatus.Ok, Uniform(0), globalCoreOffsetMHz: 0);

        Assert.True(result.Applied);
        Assert.Equal("cleared (verified: every point reads 0 MHz)", result.Detail);
    }

    [Fact]
    public void A_table_that_no_longer_matches_the_reported_core_offset_is_called_out()
    {
        // Another driver might zero the global offset with the table. Shape is
        // gone (what was asked), but the user must hear that the table and the
        // reported core offset now disagree.
        var result = VfPointPlanner.VerifyClear(NvapiStatus.Ok, Uniform(0), globalCoreOffsetMHz: 150);

        Assert.True(result.Applied);
        Assert.Contains("every point reads 0 MHz while the driver reports a +150 MHz core offset", result.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_readable_core_offset_the_uniform_value_is_still_reported()
    {
        var result = VfPointPlanner.VerifyClear(NvapiStatus.Ok, Uniform(-30), globalCoreOffsetMHz: null);

        Assert.True(result.Applied);
        Assert.Equal("cleared (verified: no per-point offsets left; every point reads -30 MHz)", result.Detail);
    }

    [Fact]
    public void A_clear_that_could_not_be_read_back_is_never_called_verified()
    {
        var unreadable = VfPointPlanner.VerifyClear(NvapiStatus.Error, [], globalCoreOffsetMHz: 0);
        Assert.True(unreadable.Applied); // the driver did accept the write…
        Assert.Contains("not verified", unreadable.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("(verified", unreadable.Detail, StringComparison.Ordinal); // …and nobody checked it

        var empty = VfPointPlanner.VerifyClear(NvapiStatus.Ok, [], globalCoreOffsetMHz: 0);
        Assert.Contains("not verified", empty.Detail, StringComparison.Ordinal);
    }
}
