using Afterglow.Cli;

namespace Afterglow.Core.Tests;

/// <summary>
/// CLI argument handling that decides WHICH card a write lands on (F-09) and
/// WHETHER a write happens at all (F-12). Only the pure parsers run here —
/// nothing in this class opens a GPU.
/// </summary>
public class CliArgumentTests
{
    [Theory]
    [InlineData("vfpoints", "--gpu", "--clear")] // index forgotten: '--clear' was parsed as the index
    [InlineData("vfpoints", "--gpu", "one", "--flatten", "875:1875")]
    [InlineData("set", "--gpu", "-1", "--core-offset", "200")]
    [InlineData("set", "--gpu", "1x")]
    [InlineData("vfpoints", "--set", "0=-100", "--gpu")] // value lost at the end
    [InlineData("vfpoints", "--gpu", "0", "--gpu", "1")] // ambiguous
    public void A_present_but_unusable_gpu_value_is_an_error_not_gpu_0(params string[] args)
    {
        // The old parser returned null here — the same as "no --gpu" — and every
        // caller then picked GPU 0.
        Assert.False(CliGpu.TryParseIndex(args, out uint? index, out string? error));
        Assert.Null(index);
        Assert.NotNull(error);
        Assert.Contains("--gpu", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Absent_and_valid_gpu_values_still_parse()
    {
        Assert.True(CliGpu.TryParseIndex(["vfpoints", "--clear"], out uint? none, out string? noError));
        Assert.Null(none);
        Assert.Null(noError);

        Assert.True(CliGpu.TryParseIndex(["vfpoints", "--gpu", "1", "--clear"], out uint? one, out _));
        Assert.Equal(1u, one);
    }

    [Theory]
    [InlineData("vfpoints", "--flatten")]
    [InlineData("vfpoints", "--set")]
    [InlineData("vfpoints", "--gpu", "1", "--set")]
    [InlineData("vfpoints", "--set", "--clear")]
    [InlineData("vfpoints", "--flatten", "--gpu", "1")]
    public void A_write_flag_without_its_value_is_refused_not_turned_into_a_listing(params string[] args)
    {
        // F-12: these printed the table and exited 0, so a script checking the
        // exit code concluded the undervolt had been applied.
        Assert.Null(VfPointsCommand.Parse(args, out string? error));
        Assert.Matches("^--(set|flatten) needs a value", error);
    }

    [Theory]
    [InlineData("vfpoints", "--set", "120=-90", "--clear")]
    [InlineData("vfpoints", "--clear", "--flatten", "875:1875")]
    [InlineData("vfpoints", "--clear", "--clear")]
    [InlineData("vfpoints", "--sett", "120=-90")]
    [InlineData("vfpoints", "120=-90")]
    [InlineData("vfpoints", "--set", "120=-90", "--set", "121=-90")]
    public void Anything_but_exactly_one_well_formed_request_is_refused(params string[] args)
    {
        Assert.Null(VfPointsCommand.Parse(args, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Out_of_range_set_values_are_refused_before_the_gpu_is_opened()
    {
        Assert.Null(VfPointsCommand.Parse(["vfpoints", "--set", "30=9000"], out string? error));
        Assert.Equal(
            "Slot 30: +9000 MHz is outside the driver's ±1500 MHz per-point range — nothing was written.", error);

        Assert.Null(VfPointsCommand.Parse(["vfpoints", "--set", "30=10,30=20"], out string? duplicate));
        Assert.Equal("Slot 30 appears more than once in --set.", duplicate);
    }

    [Fact]
    public void Well_formed_requests_parse_to_exactly_what_was_asked()
    {
        var list = VfPointsCommand.Parse(["vfpoints"], out _);
        Assert.Equal(VfPointsCommand.VfPointsAction.List, list?.Action);
        Assert.Null(list?.GpuIndex);

        var clear = VfPointsCommand.Parse(["vfpoints", "--gpu", "1", "--clear"], out _);
        Assert.Equal(VfPointsCommand.VfPointsAction.Clear, clear?.Action);
        Assert.Equal(1u, clear?.GpuIndex);

        var set = VfPointsCommand.Parse(["vfpoints", "--set", "120=-90,121=0"], out string? none);
        Assert.Null(none);
        Assert.Equal(VfPointsCommand.VfPointsAction.Set, set?.Action);
        Assert.Equal(new Dictionary<int, int> { [120] = -90, [121] = 0 }, set?.Offsets);

        var flatten = VfPointsCommand.Parse(["vfpoints", "--flatten", "875:1875", "--gpu", "0"], out _);
        Assert.Equal(VfPointsCommand.VfPointsAction.Flatten, flatten?.Action);
        Assert.Equal(875d, flatten?.FlattenVoltageMv);
        Assert.Equal(1875d, flatten?.FlattenClockMHz);
        Assert.Equal(0u, flatten?.GpuIndex);
    }
}
