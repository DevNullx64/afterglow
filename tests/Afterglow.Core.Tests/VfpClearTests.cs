using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Core.Tests;

/// <summary>
/// F-03/F-23: clearing the per-point offsets must touch only live core-domain
/// deltas. The old clear wrote a fabricated all-zero table, which relabelled
/// other-domain slots as core and blanked their deltas and opaque bytes.
/// </summary>
public sealed unsafe class VfpClearTests
{
    [Fact]
    public void Clear_zeroes_live_core_deltas_and_preserves_everything_else()
    {
        var masks = default(NvClockMasks);
        var table = default(NvClockTable);

        // Slot 0: live core point with an offset — the only thing to clear.
        masks.Clocks[0].Enabled = 1;
        table.Clocks[0].FrequencyDeltaKHz = 100_000;
        table.Clocks[0].Unknown1[3] = 0x5A;

        // Slot 1: live slot of another clock domain, with its own delta.
        masks.Clocks[1].Enabled = 1;
        table.Clocks[1].ClockType = 1;
        table.Clocks[1].FrequencyDeltaKHz = 50_000;
        table.Clocks[1].Unknown1[0] = 0x7E;

        // Slot 2: core-domain but masked off by the driver.
        table.Clocks[2].FrequencyDeltaKHz = 7_000;

        NvapiGpu.ClearCoreDeltas(&masks, &table);

        Assert.Equal(0, table.Clocks[0].FrequencyDeltaKHz);
        Assert.Equal(0x5A, table.Clocks[0].Unknown1[3]);      // opaque bytes ride through

        Assert.Equal(1u, table.Clocks[1].ClockType);
        Assert.Equal(50_000, table.Clocks[1].FrequencyDeltaKHz);
        Assert.Equal(0x7E, table.Clocks[1].Unknown1[0]);

        Assert.Equal(7_000, table.Clocks[2].FrequencyDeltaKHz);
    }
}
