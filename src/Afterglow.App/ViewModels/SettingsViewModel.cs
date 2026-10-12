using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core;
using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.App.ViewModels;

public partial class SettingsViewModel : ObservableObject, ILivePage
{
    private readonly AppServices _services;
    private bool _loading;
    private volatile bool _isLive;

    /// <summary>
    /// The Settings page carries no per-tick telemetry, but it is in
    /// MainViewModel's live-page set, so this is the one hook that fires when
    /// the user navigates onto the page. The startup-profile pickers are
    /// rebuilt from disk on that transition, so a profile saved or deleted
    /// elsewhere in the session actually appears / disappears (F-74).
    /// </summary>
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
            if (value)
            {
                RefreshStartupProfileOptions();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PollingLabel))]
    public partial double PollingIntervalMs { get; set; }

    [ObservableProperty] public partial bool CsvLoggingEnabled { get; set; }
    [ObservableProperty] public partial string CsvStatusText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool StartMinimizedToTray { get; set; }
    [ObservableProperty] public partial bool ResetOnDriverCrash { get; set; }
    [ObservableProperty] public partial bool UpdateCheckEnabled { get; set; }
    [ObservableProperty] public partial string UpdateStatusText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial string StartWithWindowsNote { get; set; } = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<AutomationRule> AutomationRules { get; } = [];

    [ObservableProperty] public partial int NewAutoMetricIndex { get; set; }
    [ObservableProperty] public partial string NewAutoThreshold { get; set; } = "94";
    [ObservableProperty] public partial string NewAutoSeconds { get; set; } = "30";
    [ObservableProperty] public partial int NewAutoActionIndex { get; set; }
    [ObservableProperty] public partial string NewAutoFanPct { get; set; } = "85";
    [ObservableProperty] public partial string NewAutoProfile { get; set; } = string.Empty;

    /// <summary>The fan-% box only applies to the fan action; the profile picker only to the profile action.</summary>
    public bool NewAutoActionIsFans => NewAutoActionIndex == 0;

    public bool NewAutoActionIsProfile => NewAutoActionIndex == 1;

    partial void OnNewAutoActionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(NewAutoActionIsFans));
        OnPropertyChanged(nameof(NewAutoActionIsProfile));
    }

    [RelayCommand]
    private void AddAutomationRule()
    {
        if (!double.TryParse(NewAutoThreshold, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double threshold) ||
            !int.TryParse(NewAutoSeconds, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int seconds))
        {
            return;
        }

        string action = NewAutoActionIndex switch { 1 => "profile", 2 => "reset", _ => "fans" };
        if (action == "profile" && (NewAutoProfile.Length == 0 || NewAutoProfile == "(none)"))
        {
            return;
        }

        uint fanPct = uint.TryParse(NewAutoFanPct, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out uint pct)
            ? Math.Clamp(pct, 30u, 100u)
            : 85u;

        AutomationRules.Add(new AutomationRule
        {
            Metric = NewAutoMetricIndex switch { 1 => "memjunction", 2 => "power", _ => "gpu" },
            Threshold = threshold,
            ForSeconds = Math.Clamp(seconds, 5, 3600),
            Action = action,
            ActionProfile = action == "profile" ? NewAutoProfile : null,
            ActionFanPct = fanPct,
        });
        PersistAutomationRules();
    }

    [RelayCommand]
    private void RemoveAutomationRule(AutomationRule? rule)
    {
        if (rule is not null && AutomationRules.Remove(rule))
        {
            PersistAutomationRules();
        }
    }

    private void PersistAutomationRules()
    {
        var rules = AutomationRules.ToArray();
        _services.UpdateSettings(s => s with { AutomationRules = rules });
    }

    [ObservableProperty] public partial double AlertGpuTemp { get; set; }
    [ObservableProperty] public partial double AlertMemTemp { get; set; }

    /// <summary>"(none)" + saved profile names for the startup-apply picker.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> StartupProfileOptions { get; } = [];

    [ObservableProperty] public partial string SelectedStartupProfile { get; set; } = "(none)";

    [ObservableProperty] public partial string StartupProfileNote { get; set; } = string.Empty;

    // Overlay
    [ObservableProperty] public partial int OverlayCornerIndex { get; set; }
    [ObservableProperty] public partial double OverlayOpacity { get; set; }
    [ObservableProperty] public partial bool OverlayShowFps { get; set; }
    [ObservableProperty] public partial bool OverlayShowGraph { get; set; }
    [ObservableProperty] public partial bool OverlayShowTemp { get; set; }
    [ObservableProperty] public partial bool OverlayShowPower { get; set; }
    [ObservableProperty] public partial bool OverlayShowClock { get; set; }
    [ObservableProperty] public partial bool OverlayShowVram { get; set; }
    [ObservableProperty] public partial bool OverlayShowFan { get; set; }

    public string PollingLabel => $"{PollingIntervalMs:F0} ms";

    public string AlertGpuLabel => AlertGpuTemp < 50 ? "off" : $"{AlertGpuTemp:F0} °C";

    public string AlertMemLabel => AlertMemTemp < 50 ? "off" : $"{AlertMemTemp:F0} °C";

    public string HotkeysText { get; } =
        "Global hotkeys:  Ctrl+Alt+O toggle overlay · Ctrl+Alt+R panic reset to defaults · " +
        "Ctrl+Alt+1…5 apply saved profile 1…5 (alphabetical)";

    // The full informational version so a beta identifies itself as such — the
    // numeric AssemblyVersion drops the "-beta.N" suffix and reported a beta as
    // plain "1.2.0" (F-46).
    public string AboutText { get; } =
        $"Afterglow {Core.Services.UpdateChecker.DisplayVersion} — " +
        "open-source tuning and monitoring for NVIDIA RTX GPUs. MIT licensed. " +
        "No kernel drivers, no telemetry, no accounts.";

    public string DataPathText { get; } = $"Profiles, settings, and logs: {AppPaths.Root}";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        RefreshStartupProfileOptions();
        LoadFrom(services.Settings);
    }

    /// <summary>
    /// Repopulates the startup-profile picker (and, since it shares the same
    /// collection, the automation-rule profile picker) from disk. Runs at
    /// construction and on every navigation onto the page (see <see cref="IsLive"/>).
    /// The current selection is preserved when its profile still exists; if the
    /// chosen profile was deleted, the picker falls back to "(none)" and the
    /// persisted startup setting is cleared so startup no longer points at a
    /// profile that is gone.
    /// </summary>
    public void RefreshStartupProfileOptions()
    {
        string previous = SelectedStartupProfile;

        _loading = true;
        StartupProfileOptions.Clear();
        StartupProfileOptions.Add("(none)");
        foreach (var profile in _services.Profiles.LoadAll())
        {
            StartupProfileOptions.Add(profile.Name);
        }

        bool stillExists = StartupProfileOptions.Contains(previous);
        SelectedStartupProfile = stillExists ? previous : "(none)";
        _loading = false;

        if (!stillExists && previous != "(none)")
        {
            // The selected profile was deleted this session: drop the dead
            // reference so RestoreStartupState doesn't tray-alert about a
            // "no longer exists" profile at the next launch.
            StartupProfileNote = string.Empty;
            _services.UpdateSettings(s => s with { ApplyProfileOnStart = null });
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        // The user answered before the background query did; theirs wins.
        _startupTaskQueryPending = false;

        if (!_services.IsElevated)
        {
            StartWithWindowsNote = "Changing autostart needs the elevated app — restart Afterglow and accept the UAC prompt first.";
            RevertStartWithWindows(!value);
            return;
        }

        string? failure;
        if (value)
        {
            // Enable() refuses outright when the exe sits somewhere a non-admin
            // can change it, and explains why — pass that through verbatim so
            // the note tells the user what to do about it.
            failure = Services.StartupTaskService.Enable().Error;
        }
        else
        {
            failure = Services.StartupTaskService.Disable() ? null : "Couldn't remove the startup task.";
        }

        if (failure is not null)
        {
            StartWithWindowsNote = failure;
            RevertStartWithWindows(!value);
            return;
        }

        StartWithWindowsNote = string.Empty;
    }

    private void RevertStartWithWindows(bool actual)
    {
        _loading = true;
        StartWithWindows = actual;
        _loading = false;
    }

    partial void OnSelectedStartupProfileChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        string? chosen = value == "(none)" ? null : value;
        if (chosen is not null)
        {
            var profile = _services.Profiles.Load(chosen);
            StartupProfileNote = profile is { MarkedStable: false }
                ? "This profile isn't marked stable yet — it won't actually apply at startup until you validate it (Stability page) and press 'Mark stable' in Profiles."
                : string.Empty;
        }
        else
        {
            StartupProfileNote = string.Empty;
        }

        _services.UpdateSettings(s => s with { ApplyProfileOnStart = chosen });
    }

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        PollingIntervalMs = s.PollingIntervalMs;
        _ = LoadStartupTaskStateAsync();
        AutomationRules.Clear();
        foreach (var rule in s.AutomationRules)
        {
            AutomationRules.Add(rule);
        }

        CloseToTray = s.CloseToTray;
        StartMinimizedToTray = s.StartMinimizedToTray;
        ResetOnDriverCrash = s.ResetOnDriverCrash;
        UpdateCheckEnabled = s.UpdateCheckEnabled;
        AlertGpuTemp = s.AlertGpuTempC == 0 ? 49 : s.AlertGpuTempC;
        AlertMemTemp = s.AlertMemJunctionTempC == 0 ? 49 : s.AlertMemJunctionTempC;
        OverlayCornerIndex = (int)s.Overlay.Corner;
        OverlayOpacity = s.Overlay.Opacity;
        OverlayShowFps = s.Overlay.ShowFps;
        OverlayShowGraph = s.Overlay.ShowFrametimeGraph;
        OverlayShowTemp = s.Overlay.ShowGpuTemp;
        OverlayShowPower = s.Overlay.ShowPower;
        OverlayShowClock = s.Overlay.ShowClock;
        OverlayShowVram = s.Overlay.ShowVram;
        OverlayShowFan = s.Overlay.ShowFan;
        SelectedStartupProfile = s.ApplyProfileOnStart is { } startup && StartupProfileOptions.Contains(startup)
            ? startup
            : "(none)";
        _loading = false;
    }

    /// <summary>
    /// Whether the logon task exists takes a schtasks.exe round trip, and the
    /// location check walks the ACL of every folder above the exe — together a
    /// few hundred milliseconds that used to sit on the UI thread at every
    /// launch, for a page that is rarely opened. Both now run in the
    /// background; the toggle shows "off" until the answer arrives, and an
    /// answer that lands after the user already flipped the toggle is dropped.
    /// </summary>
    private async Task LoadStartupTaskStateAsync()
    {
        _startupTaskQueryPending = true;
        var (enabled, note) = await Task.Run(() =>
        {
            bool exists = Services.StartupTaskService.IsEnabled();

            // A task registered before the location check existed (or from a copy
            // that has since moved) keeps running elevated at every logon. Say so:
            // the toggle only reports that a task exists, not that it is safe.
            // Judge the exe the TASK launches — the running one may be a dev
            // build or a second copy, which says nothing about the logon binary.
            string? taskExe = exists ? Services.StartupTaskService.RegisteredExecutable() : null;
            string? warning = taskExe is not null &&
                              Core.Security.TrustedInstallLocation.Check(taskExe) is { IsTrusted: false } untrusted
                ? $"This startup task runs Afterglow elevated with no prompt from a location that is not " +
                  $"administrator-only: {untrusted.Reason}. Anything able to replace the exe would inherit those " +
                  "rights at logon. Turn this off, or reinstall into Program Files with the setup .exe."
                : null;
            return (exists, warning);
        }).ConfigureAwait(true);

        if (!_startupTaskQueryPending)
        {
            return;
        }

        _startupTaskQueryPending = false;
        RevertStartWithWindows(enabled);
        if (note is not null)
        {
            StartWithWindowsNote = note;
        }
    }

    private bool _startupTaskQueryPending;

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        OnPropertyChanged(nameof(AlertGpuLabel));
        OnPropertyChanged(nameof(AlertMemLabel));
        _services.UpdateSettings(s => s with
        {
            PollingIntervalMs = (int)PollingIntervalMs,
            CloseToTray = CloseToTray,
            StartMinimizedToTray = StartMinimizedToTray,
            ResetOnDriverCrash = ResetOnDriverCrash,
            UpdateCheckEnabled = UpdateCheckEnabled,
            AlertGpuTempC = AlertGpuTemp < 50 ? 0 : (int)AlertGpuTemp,
            AlertMemJunctionTempC = AlertMemTemp < 50 ? 0 : (int)AlertMemTemp,
            Overlay = s.Overlay with
            {
                Corner = (OverlayCorner)OverlayCornerIndex,
                Opacity = OverlayOpacity,
                ShowFps = OverlayShowFps,
                ShowFrametimeGraph = OverlayShowGraph,
                ShowGpuTemp = OverlayShowTemp,
                ShowPower = OverlayShowPower,
                ShowClock = OverlayShowClock,
                ShowVram = OverlayShowVram,
                ShowFan = OverlayShowFan,
            },
        });
    }

    partial void OnPollingIntervalMsChanged(double value) => Persist();

    partial void OnCloseToTrayChanged(bool value) => Persist();

    partial void OnStartMinimizedToTrayChanged(bool value) => Persist();

    partial void OnResetOnDriverCrashChanged(bool value) => Persist();

    partial void OnUpdateCheckEnabledChanged(bool value) => Persist();

    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        UpdateStatusText = "Checking…";
        var current = Core.Services.UpdateChecker.CurrentVersion ?? new Version(0, 0, 0);
        var result = await Core.Services.UpdateChecker.CheckAsync(current);
        // Display the real running version (prerelease and all), so a beta
        // tester is never told "up to date (v1.2.0)" while on v1.2.0-beta.3 (F-46/F-65).
        string running = Core.Services.UpdateChecker.DisplayVersion;
        UpdateStatusText = result is null
            ? "Check failed — no connection, or GitHub rate-limited the request. Nothing was retried."
            : result.UpdateAvailable
                ? $"{result.LatestTag} is available (you're on v{running}) — use the Releases page button."
                : $"Up to date (v{running}).";
    }

    [RelayCommand]
    private void OpenReleasesPage()
    {
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Core.Services.UpdateChecker.ReleasesPageUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            UpdateStatusText = $"Could not open the browser: {ex.Message}";
        }
    }

    partial void OnAlertGpuTempChanged(double value) => Persist();

    partial void OnAlertMemTempChanged(double value) => Persist();

    partial void OnOverlayCornerIndexChanged(int value) => Persist();

    partial void OnOverlayOpacityChanged(double value) => Persist();

    partial void OnOverlayShowFpsChanged(bool value) => Persist();

    partial void OnOverlayShowGraphChanged(bool value) => Persist();

    partial void OnOverlayShowTempChanged(bool value) => Persist();

    partial void OnOverlayShowPowerChanged(bool value) => Persist();

    partial void OnOverlayShowClockChanged(bool value) => Persist();

    partial void OnOverlayShowVramChanged(bool value) => Persist();

    partial void OnOverlayShowFanChanged(bool value) => Persist();

    partial void OnCsvLoggingEnabledChanged(bool value)
    {
        if (value)
        {
            var logger = new CsvLogger();
            logger.Start();
            _services.ActiveCsvLogger = logger;
            _services.SetExtendedMetrics(true);
            _services.Telemetry.SnapshotTaken += logger.Log;
            CsvStatusText = $"Logging to {logger.CurrentFile}";
        }
        else if (_services.ActiveCsvLogger is { } active)
        {
            _services.Telemetry.SnapshotTaken -= active.Log;
            _services.SetExtendedMetrics(false);
            active.Dispose();
            _services.ActiveCsvLogger = null;
            CsvStatusText = "Logging stopped.";
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            // Full path: this process is usually elevated, and a bare name is
            // searched for in the app's own folder before System32.
            _ = System.Diagnostics.Process.Start(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Core.CommandLine.Join([AppPaths.Root]));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            CsvStatusText = $"Could not open folder: {ex.Message}";
        }
    }
}
