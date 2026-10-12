using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Hardware;
using Afterglow.Core.Stress;

namespace Afterglow.App.ViewModels;

public partial class StabilityViewModel : ObservableObject, IDisposable, ILivePage
{
    private readonly AppServices _services;
    private GpuContext? _gpu;
    private GpuStressTest? _stress;
    private StabilityStepper? _stepper;

    /// <summary>The card the current/last stepper run tunes (the selection may have moved since).</summary>
    private GpuContext? _stepperGpu;

    /// <summary>Outcome of <see cref="StopForShutdown"/>, once it has run.</summary>
    private bool? _shutdownSettled;

    [ObservableProperty] public partial bool StressRunning { get; set; }
    [ObservableProperty] public partial string StressStatusText { get; set; } = "Idle. The burn test loads the GPU with a deterministic compute workload and verifies every result bit-for-bit.";
    [ObservableProperty] public partial bool StressFailed { get; set; }
    [ObservableProperty] public partial string StressRateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StressElapsedText { get; set; } = string.Empty;
    [ObservableProperty] public partial double Intensity { get; set; } = 4096;

    /// <summary>0 = sustained burn, 1 = transition cycling, 2 = boost excursions.</summary>
    [ObservableProperty] public partial int PatternIndex { get; set; }

    public string PatternDescription => PatternIndex switch
    {
        1 => "Cycles between load and idle to force P-state and memory-clock transitions — the regime " +
             "where memory offsets fail even though they pass sustained burns. VRAM contents are " +
             "re-verified across every transition; a mismatch there is transition corruption, caught red-handed.",
        2 => "Short saturating bursts with idle gaps: each burst rides the boost overshoot through the top " +
             "clock bins before power management clamps, sweeping the full clock range dozens of times a " +
             "minute — the bursty desktop regime where raw core offsets crash even after passing burns.",
        _ => "Continuous full load with bit-exact verification — validates sustained clocks and power. " +
             "Note: passing here does not validate light-load boost or clock transitions; run all three " +
             "patterns before trusting a daily overclock.",
    };

    private VramTest? _vram;

    [ObservableProperty] public partial bool VramRunning { get; set; }
    [ObservableProperty] public partial bool VramFailed { get; set; }
    [ObservableProperty] public partial string VramStatusText { get; set; } =
        "Idle. Fills as much VRAM as the OS safely allows with a deterministic pattern and verifies " +
        "every element on the GPU; alternate rounds invert the pattern so every bit is exercised both ways.";
    [ObservableProperty] public partial string VramStatsText { get; set; } = string.Empty;

    [RelayCommand]
    private void ToggleVram()
    {
        if (VramRunning)
        {
            _vram?.Stop();
            return;
        }

        _vram?.Dispose();
        _vram = new VramTest { TargetPciBusId = _gpu?.PciBusId };
        _vram.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnVramProgress(progress));
        VramFailed = false;
        VramRunning = true;
        VramStatusText = "Allocating…";
        _services.Flight?.Marker("vram-start");
        _vram.Start();
    }

    private void OnVramProgress(VramProgress progress)
    {
        double gib = progress.PlannedBytes / (double)(1L << 30);
        VramStatsText = progress.State == StressState.Running
            ? $"{gib:F1} GiB · round {progress.Rounds + 1} · {progress.GiBPerSecond:F0} GiB/s verified"
            : string.Empty;

        switch (progress.State)
        {
            case StressState.Running:
                VramStatusText = "Testing — every element is written and read back on the GPU.";
                break;
            case StressState.Stopped:
                VramRunning = false;
                VramStatusText = progress.Rounds >= 1
                    ? $"Stopped after {progress.Elapsed:hh\\:mm\\:ss}: {gib:F1} GiB × {progress.Rounds} full " +
                      "rounds, 0 errors — VRAM is stable at the current memory clocks."
                    : $"Stopped after {progress.Elapsed:hh\\:mm\\:ss} before a full round completed — run " +
                      "longer for a verdict.";
                break;
            case StressState.ArtifactDetected:
            case StressState.DeviceLost:
            case StressState.Failed:
                VramRunning = false;
                VramFailed = true;
                VramStatusText = progress.Detail ?? "VRAM test failed.";
                break;
            default:
                break;
        }

        if (progress.State is not (StressState.Running or StressState.Idle))
        {
            _services.Flight?.Marker($"vram-end state={progress.State} rounds={progress.Rounds}");
        }
    }

    [ObservableProperty] public partial bool StepperRunning { get; set; }
    [ObservableProperty] public partial string StepperPhaseText { get; set; } = string.Empty;
    [ObservableProperty] public partial double StepperProgress { get; set; }
    [ObservableProperty] public partial string StepperLog { get; set; } = string.Empty;
    [ObservableProperty] public partial double StepMHz { get; set; } = 30;
    [ObservableProperty] public partial double SecondsPerStep { get; set; } = 60;
    [ObservableProperty] public partial double MaxOffset { get; set; } = 300;

    public bool CanStep => !_services.DemoMode && _services.IsElevated && _gpu is not null;

    public string StepGateText => CanStep
        ? string.Empty
        : "The stepper applies clock offsets, so it needs a real GPU and administrator rights.";

    public string IntensityLabel => $"{Intensity:F0} iterations/dispatch";

    [ObservableProperty]
    public partial string LiveStatsText { get; set; } = string.Empty;

    public bool HasCrashReport => _services.LastCrashReport is not null;

    public string CrashReportHeadline => _services.LastCrashReport?.Headline ?? string.Empty;

    public string CrashReportText => _services.LastCrashReport?.ReportText ??
        "No crash captured. If a session ever ends in a hard reset, the flight recorder's last seconds " +
        "are correlated with the Windows event log and the postmortem appears here.";

    [RelayCommand]
    private void CopyCrashReport()
    {
        if (_services.LastCrashReport is { } report)
        {
            Clipboard.SetText(report.ReportText);
        }
    }

    public StabilityViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.SelectedGpu;
        services.Telemetry.SnapshotTaken += OnSnapshot;
    }

    /// <summary>
    /// The UI moved to another GPU. A run already in flight keeps burning the
    /// card it started on (its adapter was bound at start); new runs bind to
    /// the new selection.
    /// </summary>
    public void RebindGpu() => _gpu = _services.SelectedGpu;

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
            }
        }
    }

    private uint LiveDeviceIndex => _gpu?.Index ?? 0;

    private void OnSnapshot(Core.Telemetry.GpuSnapshot snapshot)
    {
        if (!_isLive || snapshot.DeviceIndex != LiveDeviceIndex)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(Core.Telemetry.GpuSnapshot snapshot)
    {
        string hot = snapshot.HotSpotTempC is double hs ? $" (hot {hs:F0}°)" : string.Empty;
        string mem = snapshot.MemJunctionTempC is double mj ? $" · mem {mj:F0}°C" : string.Empty;
        LiveStatsText =
            $"{snapshot.CoreClockMHz ?? 0} MHz · {snapshot.GpuTempC ?? 0}°C{hot}{mem} · " +
            $"{snapshot.PowerW ?? 0:F0} W · fans {snapshot.MaxFanPercent ?? 0}%";
    }

    partial void OnIntensityChanged(double value) => OnPropertyChanged(nameof(IntensityLabel));

    partial void OnPatternIndexChanged(int value) => OnPropertyChanged(nameof(PatternDescription));

    private StressPattern SelectedPattern => PatternIndex switch
    {
        1 => StressPattern.Transitions,
        2 => StressPattern.BoostExcursions,
        _ => StressPattern.Sustained,
    };

    [RelayCommand]
    private void ToggleStress()
    {
        if (StressRunning)
        {
            _stress?.Stop();
            return;
        }

        _stress?.Dispose();
        _stress = new GpuStressTest
        {
            IterationsPerDispatch = (uint)Intensity,
            Pattern = SelectedPattern,
            TargetPciBusId = _gpu?.PciBusId,
        };
        _stress.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnStressProgress(progress));
        StressFailed = false;
        StressRunning = true;
        StressStatusText = "Burning…";
        _services.Flight?.Marker($"stress-start pattern={SelectedPattern}");
        _stress.Start();
    }

    private void OnStressProgress(StressProgress progress)
    {
        StressElapsedText = $"{progress.Elapsed:hh\\:mm\\:ss}";
        StressRateText = progress.DispatchesPerSecond > 0
            ? $"{progress.DispatchesPerSecond:F1} dispatches/s"
            : string.Empty;

        switch (progress.State)
        {
            case StressState.Running:
                StressStatusText = progress.Phase switch
                {
                    "load" => $"Transition cycling — load phase. {progress.Transitions} transitions verified clean so far.",
                    "idle" => $"Transition cycling — idle phase (clocks dropping). {progress.Transitions} transitions verified clean so far.",
                    "burst" => $"Boost excursions — riding the boost overshoot into the top clock bins. " +
                               $"{progress.Transitions} excursions verified clean so far.",
                    _ => "Burning — all results verified correct so far.",
                };
                break;
            case StressState.Stopped:
                StressRunning = false;
                StressStatusText = progress.Transitions > 0
                    ? $"Stopped after {progress.Elapsed:hh\\:mm\\:ss} with 0 errors across {progress.Transitions} " +
                      "clock excursions — stable in this regime."
                    : $"Stopped after {progress.Elapsed:hh\\:mm\\:ss} with 0 errors — stable under this load.";
                break;
            case StressState.ArtifactDetected:
                StressRunning = false;
                StressFailed = true;
                StressStatusText = progress.Detail ?? "Computation errors detected — unstable.";
                break;
            case StressState.DeviceLost:
                StressRunning = false;
                StressFailed = true;
                StressStatusText = progress.Detail ?? "GPU driver reset during load — unstable.";
                break;
            case StressState.Failed:
                StressRunning = false;
                StressFailed = true;
                StressStatusText = $"Stress test could not run: {progress.Detail}";
                break;
            default:
                break;
        }

        if (progress.State is not (StressState.Running or StressState.Idle))
        {
            _services.Flight?.Marker($"stress-end state={progress.State} transitions={progress.Transitions}");
        }
    }

    [RelayCommand]
    private void ToggleStepper()
    {
        if (StepperRunning)
        {
            _stepper?.Cancel();
            return;
        }

        if (_gpu is null)
        {
            return;
        }

        _stepperGpu = _gpu;
        _stepper = new StabilityStepper(_gpu.Tuner) { TargetPciBusId = _gpu.PciBusId };
        _stepper.StatusChanged += status =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnStepperStatus(status));
        StepperRunning = true;
        _stepper.Start(new StepperOptions
        {
            StepMHz = (int)StepMHz,
            SecondsPerStep = (int)SecondsPerStep,
            MaxOffsetMHz = (int)MaxOffset,
        });
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _ = StopForShutdown();
    }

    /// <summary>
    /// Stops every run for app exit and says whether the card was left where
    /// the stepper meant to leave it. App records a clean shutdown only when
    /// this is true: otherwise the offset under test may still be applied, and
    /// the next launch's crash banner is the only thing that would say so (F-27).
    /// </summary>
    public bool StopForShutdown()
    {
        if (_shutdownSettled is bool done)
        {
            return done;
        }

        var stepper = _stepper;
        bool wasRunning = stepper?.IsRunning == true;

        // A stepper mid-run has an untested offset applied and its cancel path
        // puts the starting offset back, so give that a bounded moment to land
        // before the process goes away (in practice well under a second; the
        // bound covers an offset apply already retrying). Ask everything to
        // stop first, then wait once: the stepper's unwind and the burn/VRAM
        // teardowns then overlap instead of adding up.
        stepper?.Cancel();
        _stress?.Stop();
        _vram?.Stop();

        bool unwound = stepper is null || stepper.CancelAndWait(TimeSpan.FromSeconds(15));
        _stress?.Dispose();
        _vram?.Dispose();

        // The run's word that it restored is not enough — the restoring apply
        // can fail (a driver still refusing calls after a TDR) and the run
        // reports "cancelled" regardless. The card's own readback decides.
        int? live = null;
        if (wasRunning && unwound && _stepperGpu is { } gpu &&
            gpu.Nvml.TryGetClockOffset(Core.Interop.Nvml.NvmlClockType.Graphics, out var offset) ==
                Core.Interop.Nvml.NvmlReturn.Success)
        {
            live = offset.ClockOffsetMHz;
        }

        bool settled = stepper is null || StepperShutdown.LeftSettled(wasRunning, unwound, stepper.Status, live);
        if (!settled)
        {
            string what = !unwound
                ? "still stopping 15 s into shutdown"
                : live is int l
                    ? $"stopped, but the card reads {l} MHz instead of the {stepper!.Status.CurrentOffsetMHz} MHz it meant to leave"
                    : $"stopped, but the card's core offset could not be read back to confirm the {stepper!.Status.CurrentOffsetMHz} MHz it meant to leave";
            Core.Diagnostics.Log.Warn(
                $"Stepper was {what} — the offset under test may still be applied; this exit is not recorded " +
                "as a clean shutdown, so the next launch says so.");
        }

        _shutdownSettled = settled;
        return settled;
    }

    private void OnStepperStatus(StepperStatus status)
    {
        StepperRunning = status.Running;
        StepperPhaseText = status switch
        {
            // Cancelled: the burn is being stopped and the starting offset put
            // back. Shown until the run publishes its terminal status, so the
            // stop is visible for the seconds the unwind actually takes.
            { Phase: "stopping" } => "Stopping — ending the burn and restoring the core offset you started from…",
            { Running: true } =>
                $"Testing {(status.CurrentOffsetMHz >= 0 ? "+" : string.Empty)}{status.CurrentOffsetMHz} MHz — " +
                $"{status.StepElapsed:mm\\:ss} / {status.StepDuration:mm\\:ss}",
            { ResultOffsetMHz: int result } =>
                $"Finished — stable core offset: {(result >= 0 ? "+" : string.Empty)}{result} MHz",
            _ => status.Phase,
        };
        StepperProgress = status.StepDuration.TotalSeconds > 0
            ? Math.Clamp(status.StepElapsed.TotalSeconds / status.StepDuration.TotalSeconds, 0, 1)
            : 0;
        StepperLog = string.Join(Environment.NewLine, status.Log.TakeLast(14));
    }
}
