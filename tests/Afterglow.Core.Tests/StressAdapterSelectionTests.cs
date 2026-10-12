using Afterglow.Core.Stress;
using Candidate = Afterglow.Core.Stress.StressAdapter.AdapterCandidate;

namespace Afterglow.Core.Tests;

/// <summary>
/// The burn-adapter selection policy, without DXGI: which enumerated NVIDIA
/// adapter a stress/VRAM/stepper/certify run loads for the card being tuned.
/// Evidence gathered on the wrong card certifies an overclock nobody tested,
/// so a known mismatch must be refused, never relabelled (F-17, F-62).
/// </summary>
public class StressAdapterSelectionTests
{
    [Fact]
    public void Requested_bus_picks_the_adapter_on_that_bus()
    {
        var candidates = new[]
        {
            new Candidate("RTX 5090", 32L << 30, Bus: 1),
            new Candidate("RTX 4070", 12L << 30, Bus: 13),
        };

        int pick = StressAdapter.Choose(candidates, pciBusId: 13, out string description);

        Assert.Equal(1, pick);
        Assert.Contains("PCI bus 13", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_adapter_whose_bus_query_failed_is_used_and_labelled_unverified()
    {
        // The case the fallback exists for: one NVIDIA adapter, D3DKMT could not
        // say which bus it is on, so there is nothing to contradict the request.
        var candidates = new[] { new Candidate("RTX 5090", 32L << 30, Bus: null) };

        int pick = StressAdapter.Choose(candidates, pciBusId: 1, out string description);

        Assert.Equal(0, pick);
        Assert.Contains("bus unverified", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_adapter_known_to_be_on_another_bus_is_refused_and_says_so()
    {
        // NVML sees a second card (TCC/compute mode, hidden from DXGI) on bus 13;
        // DXGI's only NVIDIA adapter answered the bus query: bus 1. Loading it
        // would burn the display card and certify the tuned one untested.
        var candidates = new[] { new Candidate("RTX 5090", 32L << 30, Bus: 1) };

        int pick = StressAdapter.Choose(candidates, pciBusId: 13, out string description);

        Assert.Equal(-1, pick);
        Assert.Contains("PCI bus 1", description, StringComparison.Ordinal);
        Assert.Contains("13", description, StringComparison.Ordinal);

        // The query did not fail — it answered, and the answer was "not this card".
        Assert.DoesNotContain("unavailable", description, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_adapters_none_on_the_requested_bus_are_refused()
    {
        var candidates = new[]
        {
            new Candidate("RTX 5090", 32L << 30, Bus: 1),
            new Candidate("RTX 4070", 12L << 30, Bus: null),
        };

        int pick = StressAdapter.Choose(candidates, pciBusId: 13, out string description);

        Assert.Equal(-1, pick);
        Assert.Contains("refusing", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_bus_the_largest_vram_adapter_is_chosen()
    {
        var candidates = new[]
        {
            new Candidate("RTX 4070", 12L << 30, Bus: 13),
            new Candidate("RTX 5090", 32L << 30, Bus: 1),
        };

        int pick = StressAdapter.Choose(candidates, pciBusId: null, out string description);

        Assert.Equal(1, pick);
        Assert.Equal("RTX 5090", description);
    }

    [Fact]
    public void No_nvidia_adapter_means_no_pick()
    {
        Assert.Equal(-1, StressAdapter.Choose([], pciBusId: 1, out _));
        Assert.Equal(-1, StressAdapter.Choose([], pciBusId: null, out _));
    }
}
