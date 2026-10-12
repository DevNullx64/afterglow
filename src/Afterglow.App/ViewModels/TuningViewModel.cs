using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Hardware;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.App.ViewModels;

public partial class TuningViewModel : ObservableObject
{
    private readonly AppServices _services;
    private GpuContext? _gpu;

    public TuningCapabilities Capabilities { get; private set; }

    // Pending values (sliders)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoreOffsetLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _coreOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemOffsetLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _memOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerLimitLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _powerLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoltageBoostLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _voltageBoost;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _lockEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockClockLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _lockClock = 2500;

    [ObservableProperty]
    private string _applyResultText = string.Empty;

    [ObservableProperty]
    private bool _lastApplyFailed;

    public string CoreOffsetLabel => $"{(CoreOffset >= 0 ? "+" : string.Empty)}{CoreOffset:F0} MHz";

    public string MemOffsetLabel => $"{(MemOffset >= 0 ? "+" : string.Empty)}{MemOffset:F0} MHz";

    public string PowerLimitLabel => Capabilities.PowerLimitDefaultW > 0
        ? $"{PowerLimit:F0} W  ({PowerLimit / Capabilities.PowerLimitDefaultW * 100:F0}%)"
        : $"{PowerLimit:F0} W";

    public string VoltageBoostLabel => $"+{VoltageBoost:F0}%";

    public string LockClockLabel => $"{LockClock:F0} MHz";

    public bool CanTune => _services.DemoMode || (_services.IsElevated && _gpu is not null);

    public string TuneGateText => CanTune
        ? string.Empty
        : _gpu is null
            ? "No NVIDIA GPU detected — tuning unavailable."
            : "Running without administrator rights — monitoring works, tuning is locked. Restart Afterglow and accept the elevation prompt to tune.";

    public bool SupportsTempLimit => Capabilities.SupportsTempLimit;

    public string TempLimitNote => Capabilities.SupportsTempLimit
        ? string.Empty
        : "Temperature-limit control isn't exposed by this driver generation; use the power limit and fan curve instead.";

    // Demo-mode ranges mirror an RTX 5090 so the whole UI is explorable.
    private static readonly TuningCapabilities DemoCapabilities = new()
    {
        SupportsCoreOffset = true,
        CoreOffsetMinMHz = -1000,
        CoreOffsetMaxMHz = 1000,
        SupportsMemOffset = true,
        MemOffsetMinMHz = -2000,
        MemOffsetMaxMHz = 6000,
        SupportsPowerLimit = true,
        PowerLimitMinW = 400,
        PowerLimitMaxW = 575,
        PowerLimitDefaultW = 575,
        SupportsLockedCoreClock = true,
        MaxCoreClockMHz = 3090,
        SupportsFanControl = true,
        FanCount = 3,
        FanMinDutyPct = 30,
        SupportsVoltageBoost = true,
    };

    public TuningViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.SelectedGpu;
        Capabilities = _gpu?.Tuner.Capabilities ?? DemoCapabilities;
        WatchTuner();
        RefreshFromHardware();
    }

    /// <summary>The UI moved to another GPU: re-read its ranges and applied values.</summary>
    public void RebindGpu()
    {
        _gpu = _services.SelectedGpu;
        Capabilities = _gpu?.Tuner.Capabilities ?? DemoCapabilities;
        OnPropertyChanged(nameof(Capabilities));
        OnPropertyChanged(nameof(CanTune));
        OnPropertyChanged(nameof(TuneGateText));
        OnPropertyChanged(nameof(SupportsTempLimit));
        OnPropertyChanged(nameof(TempLimitNote));
        WatchTuner();
        RefreshFromHardware();
    }

    // --- Writes made elsewhere (F-75) ---------------------------------------
    // The V/F page, the stepper, the certifier, profile hotkeys and resets all
    // write through the same tuner; without this the sliders kept showing the
    // pre-change offset and lock, and "Save current" on the Profiles page then
    // persisted values the card was not running.

    private GpuTuner? _watchedTuner;

    /// <summary>The applied values the sliders were last loaded from.</summary>
    private (int Core, int Mem, double Power, uint? Boost, uint? Lock)? _lastRead;

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

    // Raised on the writing thread (possibly a stepper or probe thread), after
    // the tuner released its lock: hop to the UI thread without blocking it.
    private void OnTunerStateWritten(GpuTuner tuner) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_gpu?.Tuner, tuner) || _gpu is null)
            {
                return; // the selection moved to another card meanwhile
            }

            // Only resync when what this page shows actually changed: a per-point
            // edit leaves the offsets and lock alone, and must not throw away
            // slider values the user is still composing.
            if (_gpu.Tuner.ReadCurrent() == _lastRead)
            {
                return;
            }

            RefreshFromHardware();
            ApplyResultText = "Applied values changed elsewhere (another page, a profile, a reset or a " +
                              "stability run) — the sliders now show what the card is running.";
            LastApplyFailed = false;
        });

    /// <summary>Loads the currently applied values into the sliders.</summary>
    public void RefreshFromHardware()
    {
        if (_gpu is null)
        {
            PowerLimit = Capabilities.PowerLimitDefaultW;
            return;
        }

        var current = _gpu.Tuner.ReadCurrent();
        _lastRead = current;
        var (core, mem, power, boost, lockMHz) = current;
        CoreOffset = core;
        MemOffset = mem;
        PowerLimit = power > 0 ? power : Capabilities.PowerLimitDefaultW;
        VoltageBoost = boost ?? 0;
        LockEnabled = lockMHz is not null;
        if (lockMHz is uint lc)
        {
            LockClock = lc;
        }
    }

    /// <summary>The pending values as a profile (used by Apply and by "save as profile").</summary>
    public TuningProfile ToProfile(string name) => new()
    {
        Name = name,
        CoreOffsetMHz = (int)CoreOffset,
        MemOffsetMHz = (int)MemOffset,
        PowerLimitW = Capabilities.SupportsPowerLimit ? PowerLimit : null,
        VoltageBoostPct = Capabilities.SupportsVoltageBoost ? (uint)VoltageBoost : null,
        LockedCoreClockMHz = LockEnabled ? (uint)LockClock : null,
    };

    /// <summary>Loads a profile's values into the sliders (does not apply).</summary>
    public void LoadProfile(TuningProfile profile)
    {
        CoreOffset = profile.CoreOffsetMHz;
        MemOffset = profile.MemOffsetMHz;
        if (profile.PowerLimitW is double p)
        {
            PowerLimit = p;
        }

        VoltageBoost = profile.VoltageBoostPct ?? 0;
        LockEnabled = profile.LockedCoreClockMHz is not null;
        if (profile.LockedCoreClockMHz is uint lc)
        {
            LockClock = lc;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTune))]
    private void Apply()
    {
        if (_gpu is null)
        {
            ApplyResultText = "Applied (demo mode — no hardware was changed).";
            LastApplyFailed = false;
            return;
        }

        var result = _gpu.Tuner.Apply(ToProfile("Current"));
        ApplyResultText = result.Summary;
        LastApplyFailed = !result.AllSucceeded;
        RefreshFromHardware();
    }

    /// <summary>
    /// Undervolt presets via the lock+offset method: cap the boost clock while a
    /// positive offset shifts the V/F curve, so the target clock is reached at a
    /// lower voltage. Values load into the sliders; Apply commits them.
    /// </summary>
    [RelayCommand]
    private void UndervoltPreset(string level)
    {
        (uint lockClock, int offset) = level switch
        {
            "efficiency" => (2500u, 150),
            "balanced" => (2650u, 125),
            _ => (2800u, 100),
        };

        LockEnabled = true;
        LockClock = Math.Min(lockClock, Capabilities.MaxCoreClockMHz);
        CoreOffset = Math.Clamp(offset, Capabilities.CoreOffsetMinMHz, Capabilities.CoreOffsetMaxMHz);
        ApplyResultText =
            $"Undervolt preset loaded: cap {LockClock:F0} MHz with +{CoreOffset:F0} MHz offset. " +
            "Press Apply, then watch core voltage under load on the Dashboard — validate stability with the Stability page.";
        LastApplyFailed = false;
    }

    [RelayCommand]
    private void Reset()
    {
        if (_gpu is null)
        {
            CoreOffset = 0;
            MemOffset = 0;
            PowerLimit = Capabilities.PowerLimitDefaultW;
            VoltageBoost = 0;
            LockEnabled = false;
            ApplyResultText = "Reset (demo mode).";
            LastApplyFailed = false;
            return;
        }

        var result = _gpu.Tuner.ResetToDefaults();
        ApplyResultText = result.Summary;
        LastApplyFailed = !result.AllSucceeded;
        LockEnabled = false;
        RefreshFromHardware();
    }
}
