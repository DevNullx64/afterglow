namespace Afterglow.App.Shell;

/// <summary>
/// DWM system backdrop materials (the DWM_SYSTEMBACKDROP_TYPE values passed with
/// DWMWA_SYSTEMBACKDROP_TYPE). Only the members Afterglow can choose are listed.
/// </summary>
internal enum SystemBackdrop
{
    /// <summary>DWMSBT_NONE: no backdrop; the window paints its own opaque background.</summary>
    None = 1,

    /// <summary>DWMSBT_MAINWINDOW: Mica, the material for a long-lived main window.</summary>
    Mica = 2,

    /// <summary>DWMSBT_TABBEDWINDOW: Mica Alt, darker and more wallpaper-tinted than Mica.</summary>
    MicaAlt = 4,
}

/// <summary>
/// Which Windows 11 window integrations to apply, decided once at startup from the OS
/// build and the user's display preferences. Deliberately free of WPF and Win32 so the
/// version gate can be unit tested on any machine (the test project compiles this one
/// file in directly): <c>WindowEffects</c> carries the plan out against a real HWND.
/// </summary>
/// <param name="DarkFrame">DWMWA_USE_IMMERSIVE_DARK_MODE (20) = TRUE. Also selects the dark Mica tint and the dark inactive fallback.</param>
/// <param name="RoundedCorners">DWMWA_WINDOW_CORNER_PREFERENCE (33) = DWMWCP_ROUND.</param>
/// <param name="BorderColor">DWMWA_BORDER_COLOR (34) = a palette stroke instead of the system grey.</param>
/// <param name="SnapLayouts">Answer HTMAXBUTTON over the custom maximize button so Windows shows the Snap Layouts flyout.</param>
/// <param name="Backdrop">DWMWA_SYSTEMBACKDROP_TYPE (38), or <see cref="SystemBackdrop.None"/> for the opaque look.</param>
internal sealed record WindowEffectsPlan(
    bool DarkFrame,
    bool RoundedCorners,
    bool BorderColor,
    bool SnapLayouts,
    SystemBackdrop Backdrop)
{
    /// <summary>Windows 11 21H2: attributes 20/33/34 are documented from here, and Snap Layouts shipped here.</summary>
    public const int Windows11Build = 22000;

    /// <summary>Windows 11 22H2: DWMWA_SYSTEMBACKDROP_TYPE (38) is documented from here.</summary>
    public const int SystemBackdropBuild = 22621;

    /// <summary>The material Afterglow uses when a backdrop is allowed.</summary>
    public const SystemBackdrop PreferredBackdrop = SystemBackdrop.Mica;

    /// <summary>Windows 10 and anything unrecognised: today's opaque window, no DWM calls at all.</summary>
    public static WindowEffectsPlan Classic { get; } =
        new(DarkFrame: false, RoundedCorners: false, BorderColor: false, SnapLayouts: false, SystemBackdrop.None);

    /// <summary>True when the window should go translucent and let the backdrop show.</summary>
    public bool UsesBackdrop => Backdrop != SystemBackdrop.None;

    /// <summary>
    /// The plan for a given OS build and the user's preferences.
    /// </summary>
    /// <param name="osBuild">Windows build number (Environment.OSVersion.Version.Build).</param>
    /// <param name="highContrast">High-contrast mode is on: never draw a material behind the content.</param>
    /// <param name="transparencyEffects">Settings &gt; Personalization &gt; Colors &gt; Transparency effects.</param>
    /// <param name="backdropOptOut">The opaque look is wanted (the default; Mica is opt-in with <c>--mica</c>).</param>
    public static WindowEffectsPlan For(int osBuild, bool highContrast, bool transparencyEffects, bool backdropOptOut)
    {
        if (osBuild < Windows11Build)
        {
            return Classic;
        }

        bool backdrop = osBuild >= SystemBackdropBuild && !highContrast && transparencyEffects && !backdropOptOut;
        return new WindowEffectsPlan(
            DarkFrame: true,
            RoundedCorners: true,
            BorderColor: true,
            SnapLayouts: true,
            backdrop ? PreferredBackdrop : SystemBackdrop.None);
    }

    /// <summary>One-line summary for the log.</summary>
    public string Describe() =>
        UsesBackdrop
            ? $"Windows 11 frame (dark, rounded, palette border, Snap Layouts) with {Backdrop} backdrop"
            : DarkFrame
                ? "Windows 11 frame (dark, rounded, palette border, Snap Layouts), opaque background"
                : "classic opaque frame";
}
