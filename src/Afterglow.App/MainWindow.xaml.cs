using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Afterglow.App.Shell;

namespace Afterglow.App;

public partial class MainWindow : Window
{
    /// <summary>Command-line switch that keeps the opaque window on Windows 11 (no Mica).</summary>
    public const string NoMicaSwitch = "--no-mica";

    private nint _hwnd;

    // Held for the window's lifetime alongside the HwndSource hook that points at it.
    private SnapLayoutHook? _snapLayoutHook;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizedInset();
        DpiChanged += (_, _) => UpdateMaximizedInset();
    }

    /// <summary>The window integration chosen at startup (for diagnostics and tests).</summary>
    internal WindowEffectsPlan EffectsPlan { get; private set; } = WindowEffectsPlan.Classic;

    /// <summary>True when DWM accepted the Mica backdrop and the window went translucent.</summary>
    internal bool BackdropActive { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // base first: it raises SourceInitialized, where WindowChrome attaches its own
        // WndProc hook. HwndSource calls hooks newest-first, so the Snap Layouts hook
        // added below sees WM_NCHITTEST before WindowChrome does.
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);

        // --screenshot renders the visual tree with RenderTargetBitmap, which never sees the
        // DWM backdrop: translucent surfaces would come out as see-through PNG pixels.
        bool optOut = Environment.GetCommandLineArgs().Any(a =>
            a.Equals(NoMicaSwitch, StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        EffectsPlan = WindowEffectsPlan.For(
            Environment.OSVersion.Version.Build,
            SystemParameters.HighContrast,
            WindowEffects.TransparencyEffectsEnabled(),
            optOut);

        // Still before the first show, so the first frame already has the final look.
        BackdropActive = WindowEffects.Apply(_hwnd, EffectsPlan, (Color)FindResource("StrokeColor"));
        if (BackdropActive)
        {
            UseBackdropSurfaces();
        }

        if (EffectsPlan.SnapLayouts && source is not null)
        {
            _snapLayoutHook = new SnapLayoutHook(MaximizeButton, ToggleMaximize);
            source.AddHook(_snapLayoutHook.WndProc);
        }

        Core.Diagnostics.Log.Info(
            $"Window: {EffectsPlan.Describe()} (build {Environment.OSVersion.Version.Build}" +
            (EffectsPlan.UsesBackdrop && !BackdropActive ? "; DWM refused the backdrop, kept opaque)" : ")"));

        UpdateMaximizedInset();
    }

    /// <summary>
    /// The Mica recipe for a WindowChrome window (as WPF's own backdrop support does it):
    /// keep AllowsTransparency off, make the whole client area DWM frame with
    /// GlassFrameThickness -1 (WindowChrome then clears the composition background and
    /// owns DwmExtendFrameIntoClientArea), drop the window fill, and paint each region
    /// with a translucent palette tint so the material only shows through as depth.
    /// </summary>
    private void UseBackdropSurfaces()
    {
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness;
        }

        Background = Brushes.Transparent;
        Resources["ChromeSurface"] = FindResource("ChromeSurfaceMica");
        Resources["ContentSurface"] = FindResource("ContentSurfaceMica");
    }

    /// <summary>
    /// A maximized window overhangs the monitor by its sizing frame on every side, and
    /// with WindowChrome that frame is client area: pad it back in so nothing is clipped.
    /// </summary>
    private void UpdateMaximizedInset()
    {
        RootGrid.Margin = WindowState == WindowState.Maximized && _hwnd != 0
            ? WindowEffects.MaximizedFrameThickness(_hwnd)
            : default;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
