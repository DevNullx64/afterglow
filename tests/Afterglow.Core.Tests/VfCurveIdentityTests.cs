using System.Text.Json;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// A measured V/F curve is one card's silicon and is turned into a core offset
/// plus a clock lock, so its file must belong to a GPU identity — never to
/// "whichever card NVML enumerates first" (F-15, F-55). Each test redirects
/// AppPaths into a throwaway directory.
/// </summary>
[Collection("AppPaths")]
public sealed class VfCurveIdentityTests : IDisposable
{
    private const string UuidA = "GPU-aaaa1111-2222-3333-4444-555566667777";
    private const string UuidB = "GPU-bbbb1111-2222-3333-4444-555566667777";

    private readonly string _root;

    public VfCurveIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"afterglow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A well-populated curve: past the plan gate (PlanMinBinSamples) at every bin.</summary>
    private static void Measure(VfCurveRecorder recorder, uint deviceIndex, double clockAt900Mv)
    {
        for (int i = 0; i < 30; i++)
        {
            foreach (var (mv, mhz) in new[] { (850.0, clockAt900Mv - 100), (900.0, clockAt900Mv), (950.0, clockAt900Mv + 100) })
            {
                recorder.Add(new GpuSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    DeviceIndex = deviceIndex,
                    CoreVoltageMv = mv,
                    CoreClockMHz = (uint)mhz,
                    GpuUtilPct = 95,
                });
            }
        }
    }

    /// <summary>Card A's months of history, as earlier versions left it: an unstamped vf-curve.json.</summary>
    private static void WriteLegacySharedCurveOfCardA()
    {
        var cardA = new VfCurveRecorder();
        Measure(cardA, deviceIndex: 0, clockAt900Mv: 2050);
        cardA.Save(VfCurveRecorder.DefaultPath);

        // Earlier versions wrote a bare array; strip the stamp to match.
        using var document = JsonDocument.Parse(File.ReadAllText(VfCurveRecorder.DefaultPath));
        string bins = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.GetRawText()
            : document.RootElement.GetProperty("Bins").GetRawText();
        File.WriteAllText(VfCurveRecorder.DefaultPath, bins);
    }

    [Fact]
    public void A_new_card_in_the_first_slot_does_not_inherit_the_previous_cards_curve()
    {
        // Card B now enumerates first (a swap, an upgrade, a slot change). Built
        // exactly the way the app and the CLI built the primary card's recorder.
        WriteLegacySharedCurveOfCardA();
        var cardB = new VfCurveRecorder
        {
            DeviceIndex = 0,
            PersistPath = VfCurveRecorder.PathFor(UuidB, isPrimary: true),
        };

        cardB.Load();

        // Nothing of card A's to draw, and above all nothing to plan from: on
        // card A's data this click would have asked for +550 MHz on card B.
        Assert.Empty(cardB.GetCurve());
        Assert.Null(cardB.PlanUndervolt(900, 2600, currentOffsetMHz: 0));
    }

    [Fact]
    public void Curve_files_are_keyed_by_identity_not_position()
    {
        Assert.NotEqual(VfCurveRecorder.DefaultPath, VfCurveRecorder.PathFor(UuidA, isPrimary: true));
        Assert.Equal(VfCurveRecorder.PathFor(UuidA, isPrimary: true), VfCurveRecorder.PathFor(UuidA, isPrimary: false));
        Assert.NotEqual(VfCurveRecorder.PathFor(UuidA), VfCurveRecorder.PathFor(UuidB));
        Assert.Equal(VfCurveRecorder.PathFor(UuidA), VfCurveRecorder.ForGpu(3, UuidA).PersistPath);
    }

    [Fact]
    public void A_cards_curve_follows_it_to_another_index()
    {
        var first = VfCurveRecorder.ForGpu(0, UuidA);
        Measure(first, deviceIndex: 0, clockAt900Mv: 2800);
        first.Save();

        // Same card, enumerated second after another card was added.
        var again = VfCurveRecorder.ForGpu(1, UuidA);
        again.Load();
        Assert.Equal(2800, again.ClockAt(900)!.Value, precision: 1);

        // The card that took index 0 starts from nothing.
        var other = VfCurveRecorder.ForGpu(0, UuidB);
        other.Load();
        Assert.Empty(other.GetCurve());
    }

    [Fact]
    public void The_saved_file_carries_the_cards_identity()
    {
        var recorder = VfCurveRecorder.ForGpu(0, UuidA);
        Measure(recorder, deviceIndex: 0, clockAt900Mv: 2800);
        recorder.Save();

        using var document = JsonDocument.Parse(File.ReadAllText(VfCurveRecorder.PathFor(UuidA)!));
        Assert.Equal(UuidA, document.RootElement.GetProperty("GpuUuid").GetString());
    }

    [Fact]
    public void A_file_stamped_for_another_card_is_refused()
    {
        // Card A's curve at the path card B would use (a copied file, or a
        // collision of the 12-character file-name key).
        var cardA = VfCurveRecorder.ForGpu(0, UuidA);
        Measure(cardA, deviceIndex: 0, clockAt900Mv: 2050);
        cardA.Save(VfCurveRecorder.PathFor(UuidB));

        var cardB = VfCurveRecorder.ForGpu(0, UuidB);
        cardB.Load();

        Assert.Empty(cardB.GetCurve());
    }

    [Fact]
    public void An_unstamped_per_card_file_from_an_earlier_version_still_loads()
    {
        // Earlier versions already gave secondary cards UUID-keyed files (bare
        // arrays); the file name is the identity, so those curves stay.
        var cardA = new VfCurveRecorder();
        Measure(cardA, deviceIndex: 1, clockAt900Mv: 2700);
        string path = VfCurveRecorder.PathFor(UuidA)!;
        cardA.Save(path);
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            File.WriteAllText(path, document.RootElement.GetProperty("Bins").GetRawText());
        }

        var reloaded = VfCurveRecorder.ForGpu(1, UuidA);
        reloaded.Load();

        Assert.Equal(2700, reloaded.ClockAt(900)!.Value, precision: 1);
    }

    [Fact]
    public void A_card_with_no_identity_persists_nothing()
    {
        WriteLegacySharedCurveOfCardA();
        string legacyBefore = File.ReadAllText(VfCurveRecorder.DefaultPath);

        var unidentified = VfCurveRecorder.ForGpu(0, gpuUuid: null);
        Assert.Null(unidentified.PersistPath);

        unidentified.Load();
        Assert.Empty(unidentified.GetCurve());

        Measure(unidentified, deviceIndex: 0, clockAt900Mv: 2800);
        unidentified.Save();

        // No file was claimed, and the legacy file was neither read nor overwritten.
        Assert.Equal(legacyBefore, File.ReadAllText(VfCurveRecorder.DefaultPath));
        Assert.Single(Directory.GetFiles(_root, "vf-curve*.json"));
    }
}
