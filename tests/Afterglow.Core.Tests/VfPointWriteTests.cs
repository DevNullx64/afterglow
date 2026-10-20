using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The two pure decisions around every per-point write: may it go ahead at
/// all (F-10), and did the driver end up holding exactly what was asked
/// (F-58). GpuTuner runs both around the NVAPI write; neither may clamp.
/// </summary>
public class VfPointWriteTests
{
    private static readonly HashSet<int> Live = [30, 31, 32];

    private static NvapiGpu.VfpTablePoint P(int index, int offset) => new(index, 800 + index, 1500 + index, offset);

    [Fact]
    public void An_out_of_range_offset_is_refused_not_clamped()
    {
        // The finding's input: `vfpoints --set "30=9000"` or 9000 typed into
        // the offset box. It used to reach the driver as +1500.
        Assert.Equal(
            "Slot 30: +9000 MHz is outside the driver's ±1500 MHz per-point range — nothing was written.",
            VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [30] = 9000 }, Live));
        Assert.Equal(
            "Slot 31: -1501 MHz is outside the driver's ±1500 MHz per-point range — nothing was written.",
            VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [30] = 0, [31] = -1501 }, Live));

        // The limit itself is a legal request, on both sides.
        Assert.Null(VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [30] = 1500, [31] = -1500 }, Live));
    }

    [Fact]
    public void The_hardware_free_check_still_refuses_range_errors()
    {
        // The CLI runs this before it opens the GPU at all.
        Assert.NotNull(VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [30] = 9000 }, liveSlots: null));
        Assert.Equal(
            "Slot 300 is outside the table (0..254) — nothing was written.",
            VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [300] = 50 }, liveSlots: null));
        Assert.NotNull(VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [-1] = 50 }, liveSlots: null));
        Assert.Null(VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [254] = 50 }, liveSlots: null));
    }

    [Fact]
    public void A_slot_that_is_not_a_live_core_point_is_refused_before_writing()
    {
        // The interop skips such a slot silently; the write would land on the
        // others and then fail readback — half-applied. Refuse it whole.
        string? refusal = VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [30] = -50, [5] = -50 }, Live);
        Assert.NotNull(refusal);
        Assert.StartsWith("Slot 5 is not a live core V/F point on this GPU", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_request_is_refused()
    {
        Assert.Equal(
            "No point offsets were given — nothing was written.",
            VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int>(), Live));
    }

    [Fact]
    public void Readback_is_compared_to_the_request_not_to_a_clamped_copy_of_it()
    {
        // F-58: the old verifier clamped the request with the write's own clamp,
        // so asking for +9000 and reading back +1500 "matched". It must not.
        var result = VfPointPlanner.VerifyPointWrite(
            new Dictionary<int, int> { [30] = 9000 }, [P(30, 1500), P(31, 0)]);

        Assert.False(result.Applied);
        Assert.Equal(
            "driver accepted the write but readback matched only 0/1 points (slot 30: asked +9000 MHz, reads +1500 MHz)",
            result.Detail);
    }

    [Fact]
    public void Only_an_exact_readback_is_verified()
    {
        var ok = VfPointPlanner.VerifyPointWrite(
            new Dictionary<int, int> { [30] = -90, [31] = 0 }, [P(30, -90), P(31, 0), P(32, 15)]);
        Assert.True(ok.Applied);
        Assert.Equal("2 point offsets (verified)", ok.Detail);

        var driverMoved = VfPointPlanner.VerifyPointWrite(
            new Dictionary<int, int> { [30] = -90, [31] = 0 }, [P(30, -75), P(31, 0)]);
        Assert.False(driverMoved.Applied);
        Assert.Contains("matched only 1/2 points (slot 30: asked -90 MHz, reads -75 MHz)", driverMoved.Detail,
            StringComparison.Ordinal);

        var missing = VfPointPlanner.VerifyPointWrite(new Dictionary<int, int> { [31] = 10 }, [P(30, 10)]);
        Assert.False(missing.Applied);
        Assert.Contains("slot 31: missing from the readback", missing.Detail, StringComparison.Ordinal);
    }
}
