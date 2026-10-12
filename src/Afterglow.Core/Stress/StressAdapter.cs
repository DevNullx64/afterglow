using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Afterglow.Core.Stress;

/// <summary>
/// Explicit D3D adapter selection for the stress engines. A null adapter in
/// D3D11CreateDevice takes whatever DXGI enumerates first — correct on most
/// desktops by luck, wrong on any machine where an iGPU drives the display.
/// When the caller knows which card is being tuned, the adapter is bound by
/// PCI bus number (resolved from the DXGI adapter's LUID via the documented
/// D3DKMT kernel-thunk query) — the same key the NVML/NVAPI pairing uses —
/// so on a multi-NVIDIA-GPU system the test can never land on the wrong
/// card because of enumeration order. Without a bus, it falls back to the
/// NVIDIA adapter with the largest dedicated VRAM, and it fails loudly
/// rather than silently burning a different card.
/// </summary>
public static class StressAdapter
{
    private const uint NvidiaVendorId = 0x10DE;

    /// <summary>Legacy selection: the NVIDIA adapter with the most VRAM.</summary>
    public static IDXGIAdapter1? SelectNvidia(out string description) =>
        Select(null, out description);

    /// <summary>
    /// Picks the NVIDIA adapter on the given PCI bus; with a null bus, the
    /// largest-VRAM NVIDIA adapter. When a bus is requested but cannot be
    /// matched, this returns null (with the reason in <paramref name="description"/>)
    /// unless exactly one NVIDIA adapter exists AND its bus query failed — then
    /// nothing contradicts the request, so it is used with a "bus unverified"
    /// note instead of making every stress test unusable on systems where the
    /// query fails. A lone adapter that reports a different bus is refused:
    /// NVML can see a card DXGI does not, so "only one adapter" does not mean
    /// "the right adapter" (see <see cref="Choose"/>).
    /// </summary>
    public static IDXGIAdapter1? Select(uint? pciBusId, out string description)
    {
        description = string.Empty;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        var candidates = new List<(IDXGIAdapter1 Adapter, string Name, long Vram, uint? Bus)>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            var desc = adapter.Description1;
            if ((uint)desc.VendorId != NvidiaVendorId)
            {
                adapter.Dispose();
                continue;
            }

            candidates.Add((adapter, desc.Description, (long)(ulong)desc.DedicatedVideoMemory,
                TryGetBusNumber(desc.Luid)));
        }

        try
        {
            int pick = Choose(
                candidates.Select(c => new AdapterCandidate(c.Name, c.Vram, c.Bus)).ToList(),
                pciBusId,
                out description);
            if (pick < 0)
            {
                return null;
            }

            var chosen = candidates[pick].Adapter;
            candidates.RemoveAt(pick);
            return chosen;
        }
        finally
        {
            foreach (var (adapter, _, _, _) in candidates)
            {
                adapter.Dispose();
            }
        }
    }

    /// <summary>One enumerated NVIDIA adapter, as the selection policy sees it (Bus null = query failed).</summary>
    internal readonly record struct AdapterCandidate(string Name, long Vram, uint? Bus);

    /// <summary>
    /// The selection policy, separated from DXGI enumeration so it can be
    /// tested: the index of the adapter to load, or -1 with the reason in
    /// <paramref name="description"/>. A requested bus that no adapter is on
    /// is refused, with one exception — a lone adapter whose bus query failed,
    /// because then nothing contradicts the request. A lone adapter whose
    /// query answered with a DIFFERENT bus is not that exception: the answer
    /// says it is another card (the tuned one is hidden from DXGI — TCC or
    /// compute mode, say), and loading it would gather stress, VRAM, stepper
    /// and certification evidence from a card nobody tuned (F-17, F-62).
    /// </summary>
    internal static int Choose(IReadOnlyList<AdapterCandidate> candidates, uint? pciBusId, out string description)
    {
        description = string.Empty;
        if (candidates.Count == 0)
        {
            return -1;
        }

        int pick;
        if (pciBusId is { } bus)
        {
            pick = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Bus == bus)
                {
                    pick = i;
                    break;
                }
            }

            if (pick >= 0)
            {
                description = $"{candidates[pick].Name} (PCI bus {bus})";
            }
            else if (candidates.Count == 1 && candidates[0].Bus is null)
            {
                pick = 0;
                description = $"{candidates[0].Name} (bus unverified — DXGI bus query unavailable)";
            }
            else if (candidates.Count == 1)
            {
                description = $"the only NVIDIA adapter DirectX exposes ({candidates[0].Name}) is on PCI bus " +
                    $"{candidates[0].Bus}, not the tuned card's bus {bus} — refusing to load a different card.";
                return -1;
            }
            else
            {
                description = $"none of the {candidates.Count} NVIDIA adapters matched PCI bus {bus} — " +
                    "refusing to guess which card to load.";
                return -1;
            }
        }
        else
        {
            pick = 0;
            for (int i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].Vram > candidates[pick].Vram)
                {
                    pick = i;
                }
            }

            description = candidates[pick].Name;
        }

        return pick;
    }

    /// <summary>
    /// DXGI adapter LUID → PCI bus number via the D3DKMT adapter-address
    /// query (gdi32 kernel thunks; the mechanism hardware monitors use).
    /// Null when the query fails.
    /// </summary>
    private static uint? TryGetBusNumber(Vortice.Luid adapterLuid)
    {
        var open = new KmtOpenAdapterFromLuid
        {
            LowPart = (uint)adapterLuid.LowPart,
            HighPart = adapterLuid.HighPart,
        };
        if (D3DKMTOpenAdapterFromLuid(ref open) < 0)
        {
            return null;
        }

        try
        {
            var address = default(KmtAdapterAddress);
            unsafe
            {
                var query = new KmtQueryAdapterInfo
                {
                    AdapterHandle = open.AdapterHandle,
                    Type = KmtQueryAdapterAddress,
                    PrivateData = (nint)(&address),
                    PrivateDataSize = (uint)sizeof(KmtAdapterAddress),
                };
                if (D3DKMTQueryAdapterInfo(ref query) < 0)
                {
                    return null;
                }
            }

            return address.BusNumber;
        }
        finally
        {
            var close = new KmtCloseAdapter { AdapterHandle = open.AdapterHandle };
            _ = D3DKMTCloseAdapter(ref close);
        }
    }

    // KMTQAITYPE_ADAPTERADDRESS from d3dkmthk.h (UMDRIVERPRIVATE=0 … FLIPQUEUEINFO=5, ADAPTERADDRESS=6).
    private const uint KmtQueryAdapterAddress = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtOpenAdapterFromLuid
    {
        public uint LowPart;
        public int HighPart;
        public uint AdapterHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtAdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtQueryAdapterInfo
    {
        public uint AdapterHandle;
        public uint Type;
        public nint PrivateData;
        public uint PrivateDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtCloseAdapter
    {
        public uint AdapterHandle;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref KmtOpenAdapterFromLuid adapter);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref KmtQueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref KmtCloseAdapter adapter);
}
