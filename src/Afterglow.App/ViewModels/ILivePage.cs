namespace Afterglow.App.ViewModels;

/// <summary>
/// A page whose live readouts only matter while someone can see them. The
/// app spends most of its life hidden in the tray, where formatting a dozen
/// strings and redrawing graphs every telemetry tick buys nothing; a page that
/// is not live skips that work and catches up the moment it becomes live.
/// Functional per-tick work (fan curves, automation, alerts, recording) never
/// lives behind this switch.
/// </summary>
public interface ILivePage
{
    /// <summary>
    /// True while the window is visible AND this is the page on screen. Set on
    /// the UI thread; read from the telemetry thread, so implementations keep
    /// it in a volatile field. Turning it on must refresh immediately.
    /// </summary>
    bool IsLive { get; set; }
}
