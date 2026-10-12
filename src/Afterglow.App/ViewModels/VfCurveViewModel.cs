using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Hardware;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.App.ViewModels;

public partial class VfCurveViewModel : ObservableObject, ILivePage
{
    private readonly AppServices _services;
    private GpuContext? _gpu;

    /// <summary>
    /// The SELECTED card's curve recorder — it moves with the title-bar
    /// selector, so anything long-running (the probe) must capture its recorder
    /// once at start instead of reading this on every tick.
    /// </summary>
    private Core.Tuning.VfCurveRecorder Recorder =>
        _gpu is { } gpu ? _services.VfCurveFor(gpu.Index) : _services.VfCurve;

    [ObservableProperty] public partial IReadOnlyList<VfBin>? Curve { get; set; }
    [ObservableProperty] public partial long PeakSamples { get; set; }
    [ObservableProperty] public partial double LiveVoltage { get; set; }
    [ObservableProperty] public partial double LiveClock { get; set; }
    [ObservableProperty] public partial double TargetVoltage { get; set; }
    [ObservableProperty] public partial double TargetClock { get; set; }
    [ObservableProperty] public partial string SampleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlanText { get; set; } =
        "Click a point on the curve to target it. Afterglow computes the exact core offset and clock lock " +
        "that hold that clock at that voltage — pick a point up and to the left of the curve for an undervolt.";
    [ObservableProperty] public partial bool HasPlan { get; set; }
    [ObservableProperty] public partial string ApplyResultText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool LastApplyFailed { get; set; }

    private UndervoltPlan? _plan;
    private int _tick;
    private VfCurveProbe? _probe;

    /// <summary>The card a running probe is bound to — captured at start, never re-read from _gpu.</summary>
    private GpuContext? _probeGpu;

    [ObservableProperty] public partial bool ProbeRunning { get; set; }
    [ObservableProperty] public partial string ProbeStatusText { get; set; } = string.Empty;

    public bool CanApply => _services.DemoMode || (_services.IsElevated && _gpu is not null);

    public string GateText => CanApply
        ? string.Empty
        : "Applying an undervolt needs administrator rights.";

    public string MethodNote { get; } =
        "Two curves, two truths: the gold dashed line is the driver's stored V/F table (editable per point " +
        "below), and the blue measured curve is what the GPU actually did — including the power limit and " +
        "throttling, which the stored table cannot show. Bar shading is how much time was spent at each " +
        "voltage. Click the chart to compute a lock+offset undervolt from measured reality, or edit the " +
        "stored table point-by-point; either way, validate on the Stability page before trusting it.";

    public VfCurveViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.SelectedGpu;
        services.Telemetry.SnapshotTaken += OnSnapshot;
        WatchTuner();
        RefreshCurve();
        RefreshVfPoints();
    }

    // --- Per-point curve editor --------------------------------------------
    // Shown on whatever GPU answers the clock-boost interfaces: the capability
    // is probed live (GpuTuner.DiscoverCapabilities), never assumed from the
    // generation — it is verified on RTX 50 and expected on RTX 20/30/40 (F-51).

    // --- Writes made elsewhere (F-08) ----------------------------------------
    // The table was re-read only by this page's own commands, so after a TDR or
    // panic reset, a profile apply or a Tuning-page apply the gold curve, the
    // point readout and the offset box kept showing a table the card no longer
    // had. Every tuner write now announces itself; this page re-reads on it.

    private GpuTuner? _watchedTuner;

    private void WatchTuner()
    {
        var tuner = _gpu?.Tuner;
        if (ReferenceEquals(tuner, _watchedTuner))
        {
            return;
        }

        if (_watchedTuner is not null)
        {
            _watchedTuner.StateWritten -= OnTunerStateWritten;
        }

        _watchedTuner = tuner;
        if (tuner is not null)
        {
            tuner.StateWritten += OnTunerStateWritten;
        }
    }

    // Raised on the writing thread after the tuner released its lock; hop to
    // the UI thread without blocking the writer. This page's own commands
    // re-read synchronously right after their write, so their echo finds an
    // unchanged table and does nothing.
    private void OnTunerStateWritten(GpuTuner tuner) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_gpu?.Tuner, tuner))
            {
                RefreshVfPoints(announceOutsideChange: true);
            }
        });

    private IReadOnlyList<Core.Interop.Nvapi.NvapiGpu.VfpTablePoint> _vfpPoints = [];

    [ObservableProperty] public partial IReadOnlyList<VfBin>? DriverCurve { get; set; }
    [ObservableProperty] public partial double VfpPointSlider { get; set; }
    [ObservableProperty] public partial string VfpPointText { get; set; } = "—";
    [ObservableProperty] public partial string VfpOffsetText { get; set; } = "0";
    [ObservableProperty] public partial string VfpTargetClockText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VfpStatusText { get; set; } = string.Empty;

    public bool SupportsVfPoints => _gpu?.Tuner.Capabilities.SupportsVfPoints == true;

    public double VfpPointMax => Math.Max(0, _vfpPoints.Count - 1);

    public string VfPointsNote { get; } =
        "Direct edits to the driver's stored V/F table — the mechanism Afterburner's curve editor uses. " +
        "Verified working on RTX 50 (5090, driver 616.56); expected to work on RTX 20/30/40 with the same " +
        "interfaces. The global core offset shares this table, so applying a core offset rewrites every " +
        "point at once and erases per-point edits — set the offset first, shape the curve second. Clearing " +
        "point offsets left the global core offset in place on the 5090 tested; the status line reports what " +
        "every point reads afterwards on yours. Applying any profile also removes point offsets that profile " +
        "doesn't carry, so the curve matches what you applied. Every write, clear included, is checked by " +
        "reading the table back, and the status line says so when that readback isn't possible. Validate " +
        "with the Stability page afterwards — a point that applies is not a point that's stable.";

    /// <param name="announceOutsideChange">
    /// Re-read because a write happened somewhere else: do nothing if the table
    /// is unchanged (so text being typed survives), and say so if it changed.
    /// </param>
    private void RefreshVfPoints(bool announceOutsideChange = false)
    {
        if (!SupportsVfPoints || _gpu is null)
        {
            _vfpPoints = [];
            DriverCurve = null;
            return;
        }

        if (_gpu.Tuner.TryReadVfPoints(out var points) != Core.Interop.Nvapi.NvapiStatus.Ok)
        {
            _vfpPoints = [];
            DriverCurve = null;
            return;
        }

        if (announceOutsideChange)
        {
            if (points.SequenceEqual(_vfpPoints))
            {
                return;
            }

            VfpStatusText = "The table changed outside this editor (a reset, a profile, the Tuning page or a " +
                            "stability run) — the curve, the point readout and the offset box were re-read from the card.";
        }

        _vfpPoints = points;
        DriverCurve = points
            .Select(p => new VfBin(p.VoltageMv, p.ClockMHz + p.OffsetMHz, p.ClockMHz + p.OffsetMHz, 1))
            .ToArray();
        OnPropertyChanged(nameof(VfpPointMax));
        UpdateVfpPointText();
    }

    private Core.Interop.Nvapi.NvapiGpu.VfpTablePoint? SelectedVfpPoint =>
        _vfpPoints.Count == 0
            ? null
            : _vfpPoints[Math.Clamp((int)Math.Round(VfpPointSlider), 0, _vfpPoints.Count - 1)];

    partial void OnVfpPointSliderChanged(double value) => UpdateVfpPointText();

    private void UpdateVfpPointText()
    {
        if (SelectedVfpPoint is not { } point)
        {
            VfpPointText = "—";
            return;
        }

        VfpPointText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{point.VoltageMv:F0} mV → {point.ClockMHz:F0} MHz stored, offset {point.OffsetMHz:+0;-0;0} MHz (slot {point.Index})");
        VfpOffsetText = point.OffsetMHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void ApplyVfPointOffset()
    {
        if (_gpu is null || SelectedVfpPoint is not { } point)
        {
            return;
        }

        if (!int.TryParse(VfpOffsetText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int offset))
        {
            VfpStatusText = "Offset must be a whole number of MHz.";
            return;
        }

        // Refused here, not clamped downstream (F-10): typing 9000 used to write
        // +1500 and report it "verified". The tuner re-checks against the table.
        var request = new Dictionary<int, int> { [point.Index] = offset };
        if (VfPointPlanner.ValidatePointOffsets(request, liveSlots: null) is string refusal)
        {
            VfpStatusText = refusal;
            return;
        }

        var result = _gpu.Tuner.SetVfPointOffsets(request);
        VfpStatusText = $"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}";
        RefreshVfPoints();
    }

    [RelayCommand]
    private void ApplyVfFlatten()
    {
        if (_gpu is null || SelectedVfpPoint is not { } point)
        {
            return;
        }

        if (!double.TryParse(VfpTargetClockText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double targetClock))
        {
            VfpStatusText = "Enter the target clock in MHz (e.g. 1875).";
            return;
        }

        var plan = Core.Tuning.VfPointPlanner.PlanFlatten(_vfpPoints, point.VoltageMv, targetClock, out string? refusal);
        if (plan is null)
        {
            VfpStatusText = refusal ?? "No plan.";
            return;
        }

        var result = _gpu.Tuner.SetVfPointOffsets(plan.OffsetsMHz);
        string lower = plan.LowerPointsCapped > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $", {plan.LowerPointsCapped} lower points brought down to the target")
            : string.Empty;
        VfpStatusText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Flatten @ {plan.AnchorVoltageMv:F0} mV → {plan.TargetClockMHz:F0} MHz ({plan.PointsFlattened} higher points capped{lower}): " +
            $"{(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
        RefreshVfPoints();
    }

    [RelayCommand]
    private void ClearVfPoints()
    {
        if (_gpu is null)
        {
            return;
        }

        var result = _gpu.Tuner.ClearVfPointOffsets();
        VfpStatusText = $"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}";
        RefreshVfPoints();
    }

    /// <summary>
    /// The UI moved to another GPU: show its curve and plan against its ranges.
    /// A probe already in flight keeps the card it started on (its tuner,
    /// sampler, load and recorder were bound at start); it is never silently
    /// redirected and never silently cancelled — the status line names that card
    /// for as long as it differs from the selected one.
    /// </summary>
    public void RebindGpu()
    {
        _gpu = _services.SelectedGpu;
        WatchTuner();
        _plan = null;
        HasPlan = false;
        PlanText = string.Empty;
        TargetVoltage = 0;
        TargetClock = 0;
        VfpStatusText = string.Empty;
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(GateText));
        OnPropertyChanged(nameof(SupportsVfPoints));

        if (ProbeRunning && _probeGpu is { } probing && probing.Index != _gpu?.Index)
        {
            ProbeStatusText =
                $"Probe still running on GPU {probing.Index} — {probing.Name}; it finishes on that card, and " +
                "its readings are that card's. The curve below is the selected card's.";
        }
        else if (!ProbeRunning)
        {
            // Don't leave the previous card's verdict standing over this card's curve.
            ProbeStatusText = string.Empty;
        }

        RefreshCurve();
        RefreshVfPoints();
    }

    private volatile bool _isLive;

    /// <inheritdoc />
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            if (value && _services.Telemetry.TryHistoryFor(LiveDeviceIndex)?.Latest is { } latest)
            {
                ApplySnapshot(latest);
                RefreshCurve();
            }
        }
    }

    private uint LiveDeviceIndex => _gpu?.Index ?? 0;

    private void OnSnapshot(GpuSnapshot snapshot)
    {
        // Display only: the curve recorder samples every tick in AppServices,
        // and a running probe samples on its own thread.
        if (!_isLive || snapshot.DeviceIndex != LiveDeviceIndex)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplySnapshot(snapshot);

            // Redraw the curve a few times a minute; sampling itself is continuous.
            if (++_tick % 5 == 0)
            {
                RefreshCurve();
            }
        });
    }

    private void ApplySnapshot(GpuSnapshot snapshot)
    {
        LiveVoltage = snapshot.CoreVoltageMv ?? 0;
        LiveClock = snapshot.CoreClockMHz ?? 0;
    }

    private void RefreshCurve()
    {
        var recorder = Recorder;
        Curve = recorder.GetCurve();
        PeakSamples = recorder.PeakBinSamples();
        SampleText = Curve.Count == 0
            ? "Collecting… run a game or the burn test to draw the curve."
            : $"{Curve.Count} voltage points from {recorder.TotalSamples:N0} samples under load";
    }

    /// <summary>Called by the chart when the user picks a target point.</summary>
    public void OnTargetPicked(double voltageMv, double clockMHz)
    {
        TargetVoltage = Math.Round(voltageMv / 5) * 5;
        TargetClock = Math.Round(clockMHz / 15) * 15;

        int currentOffset = _gpu?.Tuner.ReadCurrent().CoreOffsetMHz ?? 0;
        _plan = Recorder.PlanUndervolt(TargetVoltage, TargetClock, currentOffset, _gpu?.Tuner.Capabilities);

        if (_plan is null)
        {
            HasPlan = false;
            PlanText = $"No plan for {TargetVoltage:F0} mV / {TargetClock:F0} MHz — either the curve has too " +
                       "few samples near that voltage yet (keep gaming or run the probe), or the required " +
                       "offset/lock would fall outside what this GPU's driver accepts.";
            return;
        }

        HasPlan = true;
        PlanText = _plan.Describe();
    }

    [RelayCommand]
    private void ApplyPlan()
    {
        if (_plan is null)
        {
            return;
        }

        if (_gpu is null)
        {
            ApplyResultText = "Applied (demo mode — no hardware was changed).";
            LastApplyFailed = false;
            return;
        }

        var caps = _gpu.Tuner.Capabilities;
        var current = _gpu.Tuner.ReadCurrent();
        var profile = new Core.Profiles.TuningProfile
        {
            Name = "V/F undervolt",
            CoreOffsetMHz = Math.Clamp(_plan.CoreOffsetMHz, caps.CoreOffsetMinMHz, caps.CoreOffsetMaxMHz),
            MemOffsetMHz = current.MemOffsetMHz,
            VoltageBoostPct = current.VoltageBoostPct,
            LockedCoreClockMHz = _plan.LockClockMHz,
        };

        var result = _gpu.Tuner.Apply(profile);
        ApplyResultText = result.Summary;
        LastApplyFailed = !result.AllSucceeded;

        // The core offset just written lives in the stored table too: show it.
        // (The Tuning page learns of this write through GpuTuner.StateWritten.)
        RefreshVfPoints();
    }

    /// <summary>
    /// Maps the whole curve in about a minute: locks the clock at each step under
    /// load and records the voltage the driver selects. Restores the previous
    /// lock state when done.
    /// </summary>
    /// <summary>
    /// App exit: cancel a running probe and wait for it to put the pre-probe
    /// lock state back. True when nothing was running or the restore landed;
    /// false means the probe's exact clock pin may still be applied, so App
    /// must not record a clean shutdown (same contract as the stepper, F-27).
    /// </summary>
    public bool StopForShutdown()
    {
        if (_probe is not { } probe)
        {
            return true;
        }

        // Settle (~2 s) and the stress load's teardown (bounded at 5 s) are the
        // longest steps a cancel can wait behind.
        bool finished = probe.CancelAndWait(TimeSpan.FromSeconds(10));
        return finished && probe.LastRestoreSucceeded;
    }

    /// <summary>Starts a probe's cancellation without waiting (exit runs the waits afterwards, overlapped).</summary>
    public void BeginShutdown() => _probe?.Cancel();

    [RelayCommand]
    private void ToggleProbe()
    {
        if (ProbeRunning)
        {
            _probe?.Cancel();
            return;
        }

        // Bind the whole probe to ONE card, captured here: _gpu moves with the
        // title-bar selector, but a probe that locked clocks on this card must
        // keep sampling this card, loading this card, and saving into this
        // card's curve until it finishes. Reading _gpu per sample would report
        // another card's voltage as this card's measured V/F point.
        var gpu = _gpu;
        if (gpu is null)
        {
            ProbeStatusText = _services.DemoMode
                ? "Probe unavailable in demo mode — the demo curve is synthetic."
                : "No GPU available.";
            return;
        }

        var recorder = _services.VfCurveFor(gpu.Index);
        _probeGpu = gpu;
        _probe = new VfCurveProbe(gpu.Tuner, () => gpu.Poller.Poll()) { TargetPciBusId = gpu.PciBusId };
        _probe.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Name the probed card whenever it is no longer the one on
                // screen: these volts are not the selected card's.
                string card = _gpu is { } selected && selected.Index == gpu.Index
                    ? string.Empty
                    : $" [GPU {gpu.Index} — {gpu.Name}]";

                ProbeRunning = progress.Running;
                ProbeStatusText = progress.Running
                    ? progress.MeasuredVoltageMv is double mv
                        ? $"Step {progress.StepIndex}/{progress.StepCount}: {progress.TargetClockMHz} MHz measured at {mv:F0} mV{card}"
                        : $"Step {progress.StepIndex + 1}/{progress.StepCount}: locking {progress.TargetClockMHz} MHz…{card}"
                    : progress.Phase switch
                    {
                        "complete" => card.Length == 0
                            ? "Probe complete — the curve below is your GPU's measured V/F map."
                            : $"Probe complete on GPU {gpu.Index} — {gpu.Name}; the readings went to that card's curve. The curve below is the selected card's.",
                        "cancelled" => $"Probe cancelled; previous clock state restored.{card}",
                        _ => $"Probe stopped: {progress.Phase}{card}",
                    };
                if (!progress.Running)
                {
                    _probeGpu = null;
                    RefreshCurve();
                }
            });

        ProbeRunning = true;
        ProbeStatusText = "Starting probe…";
        _probe.Start(recorder);
    }

    [RelayCommand]
    private void ResetCurve()
    {
        Recorder.Clear();
        Recorder.Save();
        _plan = null;
        HasPlan = false;
        PlanText = string.Empty;
        TargetVoltage = 0;
        TargetClock = 0;
        RefreshCurve();
        ApplyResultText = "Curve cleared — it rebuilds as the GPU runs under load.";
        LastApplyFailed = false;
    }
}
