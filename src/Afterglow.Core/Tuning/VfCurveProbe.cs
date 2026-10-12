using Afterglow.Core.Diagnostics;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tuning;

public sealed record VfProbeProgress(
    bool Running,
    int StepIndex,
    int StepCount,
    uint TargetClockMHz,
    double? MeasuredVoltageMv,
    double? MeasuredClockMHz,
    string Phase);

/// <summary>
/// Actively probes the GPU's voltage/frequency curve: locks the core clock at a
/// series of frequencies, applies a compute load so the GPU actually boosts to
/// the locked clock, and records the voltage the driver selects for it. That is
/// the curve, measured point by point on the real silicon — including the effect
/// of any applied offset.
///
/// This complements the driver's stored V/F table (which Afterglow reads and
/// writes on every GPU that answers the probe, RTX 50 included): the table says
/// what the driver intends, the probe measures what the GPU actually runs under
/// load. (This comment used to say RTX 50 removed the curve query and rejected
/// curve writes — retracted: that was a struct-layout bug of ours, F-51.)
/// Requires elevation (clock locking is a privileged operation) and restores
/// the previous lock state when finished or cancelled.
/// </summary>
public sealed class VfCurveProbe
{
    private readonly GpuTuner _tuner;
    private readonly Func<Telemetry.GpuSnapshot> _sample;
    private volatile bool _cancel;
    private Thread? _thread;

    /// <summary>Seconds to hold each clock before sampling (settle time).</summary>
    public double SettleSeconds { get; set; } = 2.5;

    /// <summary>Seconds of sampling per clock step.</summary>
    public double SampleSeconds { get; set; } = 1.5;

    /// <summary>Clock step in MHz.</summary>
    public uint StepMHz { get; set; } = 150;

    /// <summary>Lowest clock to probe.</summary>
    public uint MinClockMHz { get; set; } = 600;

    public event Action<VfProbeProgress>? ProgressChanged;

    public bool IsRunning => _thread is { IsAlive: true };

    public VfCurveProbe(GpuTuner tuner, Func<Telemetry.GpuSnapshot> sample)
    {
        _tuner = tuner;
        _sample = sample;
    }

    /// <summary>Binds the probe's load to the tuned card on multi-GPU systems.</summary>
    public uint? TargetPciBusId { get; set; }

    public void Cancel() => _cancel = true;

    /// <summary>
    /// False when the last run ended without the driver accepting the restore of
    /// the pre-probe lock state — the probe's exact clock pin may still be in
    /// force. True before any run and after a restore the driver accepted.
    /// </summary>
    public bool LastRestoreSucceeded { get; private set; } = true;

    /// <summary>
    /// Cancels a running sweep and waits for its unwind (load teardown + lock
    /// restore). False when the thread was still running at the deadline. The
    /// probe thread is a background thread: a process exiting without this wait
    /// kills it before the restore and leaves the clock pinned.
    /// </summary>
    public bool CancelAndWait(TimeSpan timeout)
    {
        _cancel = true;
        return _thread is not { } thread || thread.Join(timeout);
    }

    /// <summary>Runs the sweep on a background thread, feeding points into the recorder.</summary>
    public void Start(VfCurveRecorder recorder)
    {
        if (IsRunning)
        {
            return;
        }

        _cancel = false;
        _thread = new Thread(() => Run(recorder))
        {
            Name = "Afterglow VF probe",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Runs the sweep synchronously (CLI path).</summary>
    public void Run(VfCurveRecorder recorder)
    {
        uint maxClock = _tuner.Capabilities.MaxCoreClockMHz;
        if (maxClock == 0)
        {
            Report(new VfProbeProgress(false, 0, 0, 0, null, null, "Clock locking is not supported on this GPU."));
            return;
        }

        var targets = new List<uint>();
        for (uint clock = MinClockMHz; clock <= maxClock; clock += StepMHz)
        {
            targets.Add(clock);
        }

        if (targets.Count == 0 || targets[^1] < maxClock - 50)
        {
            targets.Add(maxClock);
        }

        uint? previousLock = _tuner.AppliedLockMHz;

        // Whether this run ever pinned a clock: a sweep refused at its first
        // lock left nothing to undo, so its restore outcome must not count.
        bool pinned = false;
        using var load = new GpuStressTest { IterationsPerDispatch = 2048, TargetPciBusId = TargetPciBusId };
        load.Start();

        try
        {
            for (int i = 0; i < targets.Count && !_cancel; i++)
            {
                uint target = targets[i];
                Report(new VfProbeProgress(true, i, targets.Count, target, null, null, "settling"));

                if (_tuner.LockClockForProbe(target) != NvmlReturn.Success)
                {
                    Report(new VfProbeProgress(true, i, targets.Count, target, null, null,
                        "clock lock refused (administrator rights required)"));
                    break;
                }

                pinned = true;

                Sleep(SettleSeconds);
                if (_cancel)
                {
                    break;
                }

                double voltageSum = 0, clockSum = 0;
                int samples = 0;
                var until = DateTime.UtcNow.AddSeconds(SampleSeconds);
                while (DateTime.UtcNow < until && !_cancel)
                {
                    Thread.Sleep(150);
                    var snapshot = _sample();
                    recorder.Add(snapshot);
                    if (snapshot.CoreVoltageMv is double mv && snapshot.CoreClockMHz is uint mhz)
                    {
                        voltageSum += mv;
                        clockSum += mhz;
                        samples++;
                    }
                }

                Report(new VfProbeProgress(true, i + 1, targets.Count, target,
                    samples > 0 ? voltageSum / samples : null,
                    samples > 0 ? clockSum / samples : null,
                    "measured"));
            }
        }
        finally
        {
            load.StopAndWait(TimeSpan.FromSeconds(5));

            // Restore whatever lock state existed before the probe — as the
            // RANGE lock profiles apply, never as an exact pin (which would
            // hold full clocks at idle).
            bool restored = previousLock is uint restore
                ? _tuner.RestoreTuningLock(restore) == NvmlReturn.Success
                : _tuner.ForceUnlock().Applied;
            LastRestoreSucceeded = restored || !pinned;
            if (!LastRestoreSucceeded)
            {
                Log.Warn("V/F probe: the driver did not accept restoring the pre-probe clock lock state; " +
                         "the probe's clock pin may still be applied.");
            }

            recorder.Save();
            Log.Info($"V/F probe finished ({(_cancel ? "cancelled" : "complete")}).");
            Report(new VfProbeProgress(false, targets.Count, targets.Count, 0, null, null,
                _cancel ? "cancelled" : "complete"));
        }
    }

    private void Sleep(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !_cancel)
        {
            Thread.Sleep(100);
        }
    }

    private void Report(VfProbeProgress progress) => ProgressChanged?.Invoke(progress);
}
