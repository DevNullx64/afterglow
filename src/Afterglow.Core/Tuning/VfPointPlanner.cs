using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Core.Tuning;

/// <summary>
/// Pure math for per-point curve edits. The classic flatten undervolt (what
/// Afterburner users do by hand): raise the point at the target voltage to the
/// target clock, cap every higher-voltage point to the same clock, and write
/// every lower point explicitly so the result is deterministic — back to stock,
/// or down to the target where stock already runs faster than it. The GPU then
/// runs the target clock at the target voltage and no point anywhere on the
/// curve asks for more than the target.
/// </summary>
public static class VfPointPlanner
{
    /// <summary>Largest per-point delta the planner will ask for (driver limit is wider).</summary>
    public const int MaxPlannedOffsetMHz = 1000;

    /// <summary>
    /// True when the table carries per-point SHAPE — at least two live slots
    /// with different deltas. A global core offset alone reads the same delta on
    /// every slot (measured on RTX 5090 / driver 616.56); the converse does not
    /// follow, since a per-point edit that happens to be uniform reads the same
    /// way. This detects a shaped curve, not every possible edit.
    /// </summary>
    public static bool HasPerPointShape(IReadOnlyList<NvapiGpu.VfpTablePoint> points)
    {
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].OffsetMHz != points[0].OffsetMHz)
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="PointsFlattened">Higher-voltage points capped to the target.</param>
    /// <param name="LowerPointsCapped">
    /// Lower-voltage points whose stock clock already exceeded the target and
    /// were brought down to it (a target below the stored curve); zero for the
    /// usual upward flatten, where every lower point simply returns to stock.
    /// </param>
    public sealed record FlattenPlan(
        IReadOnlyDictionary<int, int> OffsetsMHz,
        int AnchorIndex,
        double AnchorVoltageMv,
        double AnchorStoredClockMHz,
        double TargetClockMHz,
        int PointsFlattened,
        int LowerPointsCapped = 0);

    /// <summary>
    /// Builds the full-table offset set for a flatten undervolt, or null with a
    /// reason when the request is not sane against the stored curve.
    /// </summary>
    public static FlattenPlan? PlanFlatten(
        IReadOnlyList<NvapiGpu.VfpTablePoint> points,
        double targetVoltageMv,
        double targetClockMHz,
        out string? refusal)
    {
        refusal = null;
        if (points.Count < 2)
        {
            refusal = "The driver exposed no per-point curve to edit.";
            return null;
        }

        if (targetClockMHz is < 300 or > 4500)
        {
            refusal = "Target clock outside 300..4500 MHz.";
            return null;
        }

        // Anchor: the stored point nearest the requested voltage.
        var anchor = points[0];
        foreach (var point in points)
        {
            if (Math.Abs(point.VoltageMv - targetVoltageMv) < Math.Abs(anchor.VoltageMv - targetVoltageMv))
            {
                anchor = point;
            }
        }

        int anchorDelta = (int)Math.Round(targetClockMHz - anchor.ClockMHz);
        if (Math.Abs(anchorDelta) > MaxPlannedOffsetMHz)
        {
            refusal = FormattableString.Invariant(
                $"Reaching {targetClockMHz:F0} MHz at {anchor.VoltageMv:F0} mV needs a ") +
                FormattableString.Invariant(
                    $"{anchorDelta:+0;-0} MHz point offset — outside the ±{MaxPlannedOffsetMHz} MHz the ") +
                "planner considers sane. Pick a target closer to the stored curve.";
            return null;
        }

        var offsets = new Dictionary<int, int>();
        int flattened = 0;
        int lowerCapped = 0;
        foreach (var point in points)
        {
            int delta;
            if (point.Index == anchor.Index)
            {
                delta = anchorDelta;
            }
            else if (point.VoltageMv < anchor.VoltageMv)
            {
                // Deterministic write: lower points explicitly return to stock —
                // unless stock already runs faster than the target, which is what
                // a target below the stored curve means. Zeroing those (F-11)
                // left e.g. 1643 MHz at 850 mV under a "flatten at 875 mV to
                // 1500 MHz", a higher clock at a lower voltage than the cap the
                // user just pinned. They are capped to the target like the rest.
                delta = Math.Min(0, (int)Math.Round(targetClockMHz - point.ClockMHz));
                if (delta < 0)
                {
                    lowerCapped++;
                }
            }
            else
            {
                delta = (int)Math.Round(targetClockMHz - point.ClockMHz);
                flattened++;
            }

            if (Math.Abs(delta) > NvapiGpu.VfpOffsetLimitMHz)
            {
                refusal = FormattableString.Invariant(
                    $"Capping the {point.VoltageMv:F0} mV point to {targetClockMHz:F0} MHz needs ") +
                    FormattableString.Invariant(
                        $"{delta:+0;-0} MHz — outside the driver's ±{NvapiGpu.VfpOffsetLimitMHz} MHz range.");
                return null;
            }

            offsets[point.Index] = delta;
        }

        return new FlattenPlan(
            offsets, anchor.Index, anchor.VoltageMv, anchor.ClockMHz, targetClockMHz, flattened, lowerCapped);
    }
}
