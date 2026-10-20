using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Afterglow.App.Shell;

/// <summary>
/// The DWM/HWND plumbing behind <see cref="WindowEffectsPlan"/>: dark frame, rounded
/// corners, border colour and the Mica system backdrop, plus the frame metrics a
/// maximized WindowChrome window needs to stay on screen.
/// </summary>
/// <remarks>
/// Every attribute must be set before the window is first shown (OnSourceInitialized),
/// otherwise the first frame paints with the defaults and visibly flips. DWM rejects
/// attributes it does not know with E_INVALIDARG, so each call reports success and the
/// caller falls back to the opaque look when the backdrop is refused.
/// <para>
/// The frame itself is not extended here: WPF's WindowChromeWorker owns
/// DwmExtendFrameIntoClientArea (driven by WindowChrome.GlassFrameThickness) and
/// re-applies it on every frame change, so a direct call would be overwritten.
/// </para>
/// </remarks>
internal static partial class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;

    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Applies the frame attributes of <paramref name="plan"/>. Returns true only when
    /// DWM accepted the backdrop, i.e. when the window may go translucent.
    /// </summary>
    public static bool Apply(nint hwnd, WindowEffectsPlan plan, Color border)
    {
        if (plan.DarkFrame)
        {
            // Must precede the backdrop: it selects the dark Mica tint and the dark
            // solid colour DWM shows while the window is inactive.
            _ = SetInt(hwnd, DwmwaUseImmersiveDarkMode, 1);
        }

        if (plan.RoundedCorners)
        {
            _ = SetInt(hwnd, DwmwaWindowCornerPreference, DwmwcpRound);
        }

        if (plan.BorderColor)
        {
            // COLORREF is 0x00BBGGRR.
            _ = SetInt(hwnd, DwmwaBorderColor, border.R | (border.G << 8) | (border.B << 16));
        }

        return plan.UsesBackdrop && SetInt(hwnd, DwmwaSystemBackdropType, (int)plan.Backdrop);
    }

    /// <summary>
    /// The user's "Transparency effects" switch. DWM already swaps Mica for a solid colour
    /// when it is off; reading it lets the window stay fully opaque (today's look) instead
    /// of showing translucent tints over that flat colour.
    /// </summary>
    public static bool TransparencyEffectsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// How far a maximized window overhangs its monitor, in DIPs. Windows positions a
    /// maximized resizable window so its sizing frame (SM_CXSIZEFRAME + SM_CXPADDEDBORDER
    /// at the window's own DPI) lies outside the work area; WindowChrome makes that frame
    /// client area, so without this inset the outer ring of content is cut off.
    /// </summary>
    public static Thickness MaximizedFrameThickness(nint hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            return default;
        }

        int padded = GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        double scale = 96.0 / dpi;
        double x = (GetSystemMetricsForDpi(SmCxSizeFrame, dpi) + padded) * scale;
        double y = (GetSystemMetricsForDpi(SmCySizeFrame, dpi) + padded) * scale;
        return new Thickness(x, y, x, y);
    }

    private static bool SetInt(nint hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) >= 0;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
