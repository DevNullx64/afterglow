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

    /// <summary>Highest raw table slot (the clock-boost tables carry 255 slots, 0..254).</summary>
    public const int MaxSlotIndex = 254;

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

    /// <summary>
    /// Refuses a per-point write before anything is written, or returns null
    /// when it may go ahead. Out-of-range offsets used to be clamped silently by
    /// the interop and then compared against the same clamp, so "30=9000" wrote
    /// +1500 and reported "verified" (F-10/F-58). The same ±limit a saved
    /// profile is validated against now applies at every entry point, and the
    /// request is refused rather than reduced.
    /// </summary>
    /// <param name="liveSlots">
    /// The live core slots read from the table, or null to check only what can
    /// be checked without hardware (the CLI does that before opening the GPU).
    /// A slot outside this set would be skipped by the driver write and then
    /// fail readback — refusing it up front keeps the write all-or-nothing.
    /// </param>
    public static string? ValidatePointOffsets(
        IReadOnlyDictionary<int, int> offsetsMHzByIndex, IReadOnlySet<int>? liveSlots)
    {
        if (offsetsMHzByIndex.Count == 0)
        {
            return "No point offsets were given — nothing was written.";
        }

        foreach (var (index, offset) in offsetsMHzByIndex.OrderBy(pair => pair.Key))
        {
            if (index is < 0 or > MaxSlotIndex)
            {
                return FormattableString.Invariant(
                    $"Slot {index} is outside the table (0..{MaxSlotIndex}) — nothing was written.");
            }

            if (liveSlots is not null && !liveSlots.Contains(index))
            {
                return FormattableString.Invariant(
                    $"Slot {index} is not a live core V/F point on this GPU (list them with `afterglow-cli vfpoints`) — nothing was written.");
            }

            if (Math.Abs(offset) > NvapiGpu.VfpOffsetLimitMHz)
            {
                return FormattableString.Invariant(
                    $"Slot {index}: {offset:+0;-0} MHz is outside the driver's ±{NvapiGpu.VfpOffsetLimitMHz} MHz per-point range — nothing was written.");
            }
        }

        return null;
    }

    /// <summary>
    /// Verifies a per-point write against what was REQUESTED (F-58: the old
    /// check compared against the request after the same clamp the write
    /// applied, so a silently reduced write always matched). Requests reaching
    /// here have passed <see cref="ValidatePointOffsets"/>, so nothing was
    /// clamped and the only way to match is for the driver to hold exactly the
    /// asked-for value.
    /// </summary>
    public static KnobResult VerifyPointWrite(
        IReadOnlyDictionary<int, int> requested, IReadOnlyList<NvapiGpu.VfpTablePoint> readback)
    {
        const string knob = "V/F points";
        var byIndex = new Dictionary<int, int>();
        foreach (var point in readback)
        {
            byIndex[point.Index] = point.OffsetMHz;
        }

        int matched = 0;
        string? firstMismatch = null;
        foreach (var (index, offset) in requested.OrderBy(pair => pair.Key))
        {
            if (byIndex.TryGetValue(index, out int actual) && actual == offset)
            {
                matched++;
                continue;
            }

            firstMismatch ??= byIndex.TryGetValue(index, out actual)
                ? FormattableString.Invariant($"slot {index}: asked {offset:+0;-0;0} MHz, reads {actual:+0;-0;0} MHz")
                : FormattableString.Invariant($"slot {index}: missing from the readback");
        }

        return matched == requested.Count
            ? KnobResult.Ok(knob, FormattableString.Invariant($"{matched} point offsets (verified)"))
            : KnobResult.Fail(knob, FormattableString.Invariant(
                $"driver accepted the write but readback matched only {matched}/{requested.Count} points ({firstMismatch})"));
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
