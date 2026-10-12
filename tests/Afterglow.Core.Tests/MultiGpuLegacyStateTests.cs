using System.Text.Json;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Two cards against the pre-multi-GPU applied-state file. Version 1.1.0 wrote
/// one applied-state.json stamped with the tuned card's UUID; after an upgrade
/// on a two-card machine, the OTHER card must never read it, seed a write from
/// it, or retire it — or card A's tracked clock lock becomes card B's on the
/// next launch, a silent range lock nobody applied to B (F-14, F-54, F-59).
/// The earlier tests used one UUID throughout, so none of this was covered.
/// </summary>
[Collection("AppPaths")]
public sealed class MultiGpuLegacyStateTests : IDisposable
{
    private const string UuidA = "GPU-aaaa1111-2222-3333-4444-555566667777";
    private const string UuidB = "GPU-bbbb1111-2222-3333-4444-555566667777";

    private readonly string _root;

    public MultiGpuLegacyStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"afterglow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;
        AppliedStateStore.DetectedGpuCount = 0;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>What 1.1.0 left behind: card A's "Daily OC" with a 2800 MHz lock, in the single legacy file.</summary>
    private static void WriteLegacyRecordOfCardA() =>
        File.WriteAllText(
            AppPaths.AppliedStateFile,
            JsonSerializer.Serialize(new AppliedStateStore.AppliedState(
                "Daily OC", DateTimeOffset.Now, AllKnobsSucceeded: true, CleanShutdown: true,
                LockedCoreClockMHz: 2800, GpuUuid: UuidA)));

    private static void WriteUnstampedLegacyRecord() =>
        File.WriteAllText(
            AppPaths.AppliedStateFile,
            JsonSerializer.Serialize(new AppliedStateStore.AppliedState(
                "Daily OC", DateTimeOffset.Now, AllKnobsSucceeded: true, CleanShutdown: false,
                LockedCoreClockMHz: 2800)));

    [Fact]
    public void With_two_cards_an_unstamped_legacy_record_is_adopted_by_neither()
    {
        WriteUnstampedLegacyRecord();
        AppliedStateStore.DetectedGpuCount = 2;

        Assert.Null(AppliedStateStore.Load(UuidA));
        Assert.Null(AppliedStateStore.Load(UuidB));

        // Still visible to the crash scan: it was not a clean shutdown.
        Assert.Contains(AppliedStateStore.LoadAll(), s => !s.CleanShutdown && s.LockedCoreClockMHz == 2800u);
    }

    [Fact]
    public void With_one_card_an_unstamped_legacy_record_still_migrates()
    {
        WriteUnstampedLegacyRecord();
        AppliedStateStore.DetectedGpuCount = 1;

        Assert.Equal(2800u, AppliedStateStore.Load(UuidA)!.LockedCoreClockMHz);
    }

    [Fact]
    public void Card_b_never_reads_a_legacy_record_stamped_for_card_a()
    {
        WriteLegacyRecordOfCardA();

        // GpuTuner's constructor adopts the tracked lock from exactly this read.
        Assert.Null(AppliedStateStore.Load(UuidB));
        Assert.Equal(2800u, AppliedStateStore.Load(UuidA)!.LockedCoreClockMHz);
    }

    [Fact]
    public void A_fan_write_on_card_b_does_not_inherit_card_as_lock()
    {
        // The upgrade scenario: the user's first action on card B is a fixed fan duty.
        WriteLegacyRecordOfCardA();

        AppliedStateStore.RecordFans("fixed", 70, UuidB);

        var b = AppliedStateStore.Load(UuidB)!;
        Assert.Equal(UuidB, b.GpuUuid);
        Assert.Equal("fixed", b.FanMode);
        Assert.Null(b.LockedCoreClockMHz);
        Assert.NotEqual("Daily OC", b.ProfileName);

        // Card A's record is still card A's, and nothing is reported twice.
        Assert.True(File.Exists(AppPaths.AppliedStateFile));
        Assert.Equal(2800u, AppliedStateStore.Load(UuidA)!.LockedCoreClockMHz);
        var all = AppliedStateStore.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Single(all, s => s.LockedCoreClockMHz == 2800u);
    }

    [Fact]
    public void An_interrupted_apply_on_card_b_does_not_inherit_card_as_lock()
    {
        // A crash between RecordPending and Record leaves the pending record as is.
        WriteLegacyRecordOfCardA();

        AppliedStateStore.RecordPending("card B profile", UuidB);

        var b = AppliedStateStore.Load(UuidB)!;
        Assert.True(b.Pending);
        Assert.Null(b.LockedCoreClockMHz);
    }

    [Fact]
    public void Releasing_card_bs_fans_writes_no_copy_of_card_as_record()
    {
        WriteLegacyRecordOfCardA();

        AppliedStateStore.RecordFans(null, null, UuidB);

        Assert.False(File.Exists(AppliedStateStore.PathFor(UuidB)));
        Assert.Equal(2800u, AppliedStateStore.Load(UuidA)!.LockedCoreClockMHz);
    }

    [Fact]
    public void Clearing_card_b_leaves_card_as_legacy_record_alone()
    {
        WriteLegacyRecordOfCardA();

        AppliedStateStore.Clear(UuidB);

        Assert.True(File.Exists(AppPaths.AppliedStateFile));
        Assert.Equal(2800u, AppliedStateStore.Load(UuidA)!.LockedCoreClockMHz);
    }

    [Fact]
    public void Card_as_own_first_write_supersedes_and_retires_its_legacy_record()
    {
        WriteLegacyRecordOfCardA();

        AppliedStateStore.Record(new TuningProfile { Name = "fresh" }, allSucceeded: true, lockedClock: null, UuidA);

        Assert.False(File.Exists(AppPaths.AppliedStateFile));
        var a = Assert.Single(AppliedStateStore.LoadAll());
        Assert.Equal("fresh", a.ProfileName);
        Assert.Null(a.LockedCoreClockMHz);
    }
}
