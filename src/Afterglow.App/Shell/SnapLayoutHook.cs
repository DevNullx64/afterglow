using System.Windows;
using System.Windows.Controls;

namespace Afterglow.App.Shell;

/// <summary>
/// Hover and pressed state for a caption button whose mouse input Windows handles in
/// the non-client area. WPF's IsMouseOver/IsPressed stay false there, so the caption
/// button template triggers on these as well.
/// </summary>
internal static class NonClientButton
{
    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered", typeof(bool), typeof(NonClientButton), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(NonClientButton), new FrameworkPropertyMetadata(false));

    public static bool GetIsHovered(DependencyObject element) => (bool)element.GetValue(IsHoveredProperty);

    public static void SetIsHovered(DependencyObject element, bool value) => element.SetValue(IsHoveredProperty, value);

    public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);

    public static void SetIsPressed(DependencyObject element, bool value) => element.SetValue(IsPressedProperty, value);
}

/// <summary>
/// Windows 11 Snap Layouts for a custom-drawn maximize button.
/// </summary>
/// <remarks>
/// Windows only shows the Snap Layouts flyout when WM_NCHITTEST answers HTMAXBUTTON over
/// the maximize button. WindowChrome answers HTCLIENT there (the button sets
/// IsHitTestVisibleInChrome), so this hook runs first and claims the button's screen rect.
/// <para>
/// The catch (dotnet/wpf#4825): once a point is non-client, WPF receives WM_NC* instead of
/// client mouse messages, so the Button never sees hover, press or click there. This hook
/// drives those itself, following the technique used by WPF-UI (TitleBarButton) and
/// ControlzEx (NonClientControlManager):
/// WM_NCHITTEST over the button sets hover and returns HTMAXBUTTON; WM_NCMOUSELEAVE clears
/// it; WM_NCLBUTTONDOWN is swallowed (DefWindowProc would otherwise run its modal caption
/// button loop and flash a classic button) and only marks the button pressed;
/// WM_NCLBUTTONUP over the button invokes maximize/restore.
/// </para>
/// <para>
/// Must be added to the HwndSource after WindowChrome's own hook: HwndSource calls hooks
/// newest-first, and an unhandled message falls through to WindowChrome unchanged.
/// </para>
/// </remarks>
internal sealed class SnapLayoutHook
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    private readonly Button _button;
    private readonly Action _toggleMaximize;

    public SnapLayoutHook(Button button, Action toggleMaximize)
    {
        _button = button;
        _toggleMaximize = toggleMaximize;
    }

    public nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (IsOverButton(lParam))
                {
                    SetHovered(true);
                    handled = true;
                    return HtMaxButton;
                }

                // Anywhere else in the window: not ours. Leave the message to WindowChrome.
                SetHovered(false);
                SetPressed(false);
                break;

            case WmNcMouseLeave:
                SetHovered(false);
                SetPressed(false);
                break;

            case WmNcLButtonDown or WmNcLButtonDblClk when wParam == HtMaxButton:
                SetPressed(true);
                handled = true;
                break;

            case WmNcLButtonUp when wParam == HtMaxButton:
                bool click = NonClientButton.GetIsPressed(_button);
                SetPressed(false);
                handled = true;
                if (click)
                {
                    _toggleMaximize();
                }

                break;

            case WmNcLButtonUp:
                SetPressed(false);
                break;
        }

        return 0;
    }

    /// <summary>
    /// True when the screen point packed in <paramref name="lParam"/> lies on the button.
    /// The coordinates are physical pixels (signed: monitors left of or above the primary
    /// one are negative); PointFromScreen converts them with the window's own DPI.
    /// </summary>
    private bool IsOverButton(nint lParam)
    {
        if (!_button.IsVisible || !_button.IsEnabled || PresentationSource.FromVisual(_button) is null)
        {
            return false;
        }

        long packed = lParam;
        var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        Point local = _button.PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X < _button.ActualWidth && local.Y < _button.ActualHeight;
    }

    private void SetHovered(bool value)
    {
        if (NonClientButton.GetIsHovered(_button) != value)
        {
            NonClientButton.SetIsHovered(_button, value);
        }
    }

    private void SetPressed(bool value)
    {
        if (NonClientButton.GetIsPressed(_button) != value)
        {
            NonClientButton.SetIsPressed(_button, value);
        }
    }
}
