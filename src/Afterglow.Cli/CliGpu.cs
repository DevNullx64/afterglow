using System.Globalization;
using Afterglow.Core.Hardware;

namespace Afterglow.Cli;

/// <summary>Shared `--gpu N` handling for commands that bind work to one card.</summary>
internal static class CliGpu
{
    /// <summary>
    /// Finds `--gpu N`, telling "not given" apart from "given but unusable"
    /// (F-09). The old parser returned null for both, and every caller read
    /// null as "use GPU 0" — so `--gpu --clear`, `--gpu one` or `--gpu -1` on a
    /// two-card box silently tuned the card the user did not name.
    /// </summary>
    /// <returns>
    /// True with <paramref name="index"/> null when no --gpu was given, true
    /// with the index when it parsed, false with <paramref name="error"/> when
    /// the flag is present but its value is missing, malformed, or repeated.
    /// </returns>
    public static bool TryParseIndex(string[] args, out uint? index, out string? error)
    {
        index = null;
        error = null;
        bool seen = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] != "--gpu")
            {
                continue;
            }

            if (seen)
            {
                index = null;
                error = "--gpu was given more than once; name exactly one GPU index.";
                return false;
            }

            seen = true;
            if (i + 1 >= args.Length)
            {
                error = "--gpu needs a GPU index (e.g. --gpu 1).";
                return false;
            }

            // Digits only: a sign, a word or the next option is a typo, never "GPU 0".
            string value = args[i + 1];
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
            {
                error = $"--gpu needs a GPU index (0, 1, ...); got '{value}'.";
                return false;
            }

            index = parsed;
            i++;
        }

        return true;
    }

    /// <summary>
    /// `--gpu N` for commands that resolve the card themselves: null when no
    /// --gpu was given, the index when it parsed. A present-but-unusable value
    /// is a usage error: the reason goes to stderr and the process exits with
    /// code 2 before any hardware is touched (the argparse convention), so no
    /// caller can fall back to GPU 0 on a typo.
    /// </summary>
    public static uint? ParseIndex(string[] args)
    {
        if (TryParseIndex(args, out uint? index, out string? error))
        {
            return index;
        }

        Console.Error.WriteLine(error);
        Environment.Exit(2);
        return null; // not reached
    }

    /// <summary>
    /// Resolves `--gpu N` to that GPU's PCI bus so the D3D stress engines bind
    /// to the exact card NVML tunes. Returns (null, null) when no --gpu was
    /// given — the engines then fall back to the largest NVIDIA adapter, which
    /// needs no NVML at all. A malformed --gpu exits 2 (see <see cref="ParseIndex"/>).
    /// </summary>
    public static (uint? Bus, string? Error) ResolveBus(string[] args)
    {
        if (ParseIndex(args) is not { } index)
        {
            return (null, null);
        }

        using var manager = new GpuManager();
        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == index);
        if (gpu is null)
        {
            return (null, $"GPU {index} not found — {manager.Gpus.Count} NVIDIA GPU(s) detected.");
        }

        if (gpu.PciBusId is null)
        {
            return (null, $"GPU {index} ({gpu.Name}) did not report a PCI bus id; cannot bind the test to it.");
        }

        return (gpu.PciBusId, null);
    }
}
