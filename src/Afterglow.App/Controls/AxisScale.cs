namespace Afterglow.App.Controls;

/// <summary>Round-number value axes for the graphs (pure math, unit-tested).</summary>
internal static class AxisScale
{
    /// <summary>Most intervals an axis is divided into.</summary>
    public const int MaxTicks = 5;

    /// <summary>
    /// Snaps an axis range outward to a round step (1, 2, 2.5 or 5 × 10ⁿ) with at
    /// most <see cref="MaxTicks"/> intervals, so gridlines and labels land on round
    /// numbers. A fixed bound is kept as given; with both fixed, the step must divide
    /// the range evenly, else the range is cut in quarters.
    /// </summary>
    public static (double Min, double Max, double Step) Nice(
        double min, double max, double fixedMin, double fixedMax)
    {
        bool fixLo = !double.IsNaN(fixedMin);
        bool fixHi = !double.IsNaN(fixedMax);
        double span = max - min;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(span / MaxTicks)));
        ReadOnlySpan<double> multipliers = [1, 2, 2.5, 5, 10, 20, 25, 50, 100];
        foreach (double m in multipliers)
        {
            double step = m * magnitude;
            if (fixLo && fixHi)
            {
                double intervals = span / step;
                if (intervals <= MaxTicks + 1e-9 && Math.Abs(intervals - Math.Round(intervals)) < 1e-6)
                {
                    return (min, max, step);
                }

                continue;
            }

            double lo = fixLo ? min : Math.Floor((min / step) + 1e-9) * step;
            double hi = fixHi ? max : Math.Ceiling((max / step) - 1e-9) * step;
            if (fixHi)
            {
                lo = hi - (Math.Ceiling(((hi - min) / step) - 1e-9) * step);
            }
            else if (fixLo)
            {
                hi = lo + (Math.Ceiling(((max - lo) / step) - 1e-9) * step);
            }

            if ((hi - lo) / step <= MaxTicks + 1e-9)
            {
                return (lo, hi, step);
            }
        }

        return (min, max, span / 4);
    }
}
