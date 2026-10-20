using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Afterglow.Core.Tuning;

namespace Afterglow.App.Controls;

/// <summary>
/// Voltage/frequency curve chart: the measured curve with hit-density shading
/// (how often the GPU actually sat at each point), the live operating point, and
/// a click-to-pick target used to compute an undervolt.
/// X axis = core voltage (mV), Y axis = core clock (MHz).
/// </summary>
public sealed class VfCurveChart : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(IReadOnlyList<VfBin>), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakSamplesProperty = DependencyProperty.Register(
        nameof(PeakSamples), typeof(long), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiveVoltageProperty = DependencyProperty.Register(
        nameof(LiveVoltage), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiveClockProperty = DependencyProperty.Register(
        nameof(LiveClock), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetVoltageProperty = DependencyProperty.Register(
        nameof(TargetVoltage), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetClockProperty = DependencyProperty.Register(
        nameof(TargetClock), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DriverCurveProperty = DependencyProperty.Register(
        nameof(DriverCurve), typeof(IReadOnlyList<VfBin>), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)));
    private static readonly Brush CurveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)));
    private static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3C)));
    private static readonly Brush TargetBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x46, 0xC3, 0x6B)));
    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
    private static readonly Typeface LabelFace = new("Segoe UI");
    private static readonly Brush PillBrush = Freeze(new SolidColorBrush(Color.FromArgb(210, 12, 15, 20)));
    private static readonly Brush PillTextBrush = Freeze(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));
    private static readonly Brush DriverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x3C)));

    // Drawing resources are immutable and shared: the chart redraws every
    // telemetry tick while its page is open (the live point moves), and used
    // to allocate a pen per gridline and a brush per bar each time.
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));
    private static readonly Pen DriverPen = Freeze(new Pen(DriverBrush, 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen CurvePen = Freeze(new Pen(CurveBrush, 2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen TargetDashPen = Freeze(new Pen(TargetBrush, 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen TargetRingPen = Freeze(new Pen(TargetBrush, 2.5));

    /// <summary>Hit-density bar fills, quantized to 32 alpha steps (indistinguishable on screen).</summary>
    private static readonly Brush[] DensityBrushes = BuildDensityBrushes();

    private readonly Dictionary<(string Text, double Size, Brush Brush), FormattedText> _labelCache = [];
    private double _labelCacheDpi;

    private double _minV = 700, _maxV = 1200, _minF = 1000, _maxF = 3000, _fStep = 500;
    private Point? _hover;

    public IReadOnlyList<VfBin>? Curve
    {
        get => (IReadOnlyList<VfBin>?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public long PeakSamples
    {
        get => (long)GetValue(PeakSamplesProperty);
        set => SetValue(PeakSamplesProperty, value);
    }

    public double LiveVoltage
    {
        get => (double)GetValue(LiveVoltageProperty);
        set => SetValue(LiveVoltageProperty, value);
    }

    public double LiveClock
    {
        get => (double)GetValue(LiveClockProperty);
        set => SetValue(LiveClockProperty, value);
    }

    public double TargetVoltage
    {
        get => (double)GetValue(TargetVoltageProperty);
        set => SetValue(TargetVoltageProperty, value);
    }

    public double TargetClock
    {
        get => (double)GetValue(TargetClockProperty);
        set => SetValue(TargetClockProperty, value);
    }

    /// <summary>
    /// The driver's stored per-point table with applied offsets (gold, dashed) —
    /// present only on GPUs whose driver exposes per-point curve control.
    /// </summary>
    public IReadOnlyList<VfBin>? DriverCurve
    {
        get => (IReadOnlyList<VfBin>?)GetValue(DriverCurveProperty);
        set => SetValue(DriverCurveProperty, value);
    }

    /// <summary>Raised when the user picks a target point (voltage mV, clock MHz).</summary>
    public event EventHandler<(double VoltageMv, double ClockMHz)>? TargetPicked;

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Brush[] BuildDensityBrushes()
    {
        var brushes = new Brush[32];
        for (int i = 0; i < brushes.Length; i++)
        {
            double intensity = (i + 1) / (double)brushes.Length;
            brushes[i] = Freeze(new SolidColorBrush(Color.FromArgb((byte)(20 + (intensity * 90)), 0x58, 0xA6, 0xFF)));
        }

        return brushes;
    }

    private static void ExpandRange(IReadOnlyList<VfBin> bins, ref double vMin, ref double vMax, ref double fMin, ref double fMax)
    {
        foreach (var bin in bins)
        {
            vMin = Math.Min(vMin, bin.VoltageMv);
            vMax = Math.Max(vMax, bin.VoltageMv);
            fMin = Math.Min(fMin, bin.MaxClockMHz);
            fMax = Math.Max(fMax, bin.MaxClockMHz);
        }
    }

    private Rect PlotRect => new(46, 10, Math.Max(10, ActualWidth - 58), Math.Max(10, ActualHeight - 36));

    private Point ToScreen(double voltageMv, double clockMHz)
    {
        var r = PlotRect;
        double x = r.X + ((voltageMv - _minV) / (_maxV - _minV) * r.Width);
        double y = r.Bottom - ((clockMHz - _minF) / (_maxF - _minF) * r.Height);
        return new Point(x, y);
    }

    private (double VoltageMv, double ClockMHz) ToData(Point p)
    {
        var r = PlotRect;
        double v = _minV + ((p.X - r.X) / r.Width * (_maxV - _minV));
        double f = _minF + ((r.Bottom - p.Y) / r.Height * (_maxF - _minF));
        return (Math.Clamp(v, _minV, _maxV), Math.Clamp(f, _minF, _maxF));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        var r = PlotRect;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (r.Width < 30 || r.Height < 30)
        {
            return;
        }

        var curve = Curve;
        var driver = DriverCurve;
        bool hasCurve = curve is { Count: > 1 };
        bool hasDriver = driver is { Count: > 1 };
        if (hasCurve || hasDriver)
        {
            // Auto-range with padding, snapped to readable steps.
            double vMin = double.MaxValue, vMax = double.MinValue, fMin = double.MaxValue, fMax = double.MinValue;
            if (hasCurve)
            {
                ExpandRange(curve!, ref vMin, ref vMax, ref fMin, ref fMax);
            }

            if (hasDriver)
            {
                ExpandRange(driver!, ref vMin, ref vMax, ref fMin, ref fMax);
            }

            _minV = Math.Floor((vMin - 25) / 25) * 25;
            _maxV = Math.Ceiling((vMax + 25) / 25) * 25;
            _minF = Math.Floor((fMin - 100) / 100) * 100;
            _maxF = Math.Ceiling((fMax + 100) / 100) * 100;
            if (_maxV - _minV < 50)
            {
                _maxV = _minV + 50;
            }

            if (_maxF - _minF < 200)
            {
                _maxF = _minF + 200;
            }

            // Gridlines on round clocks inside the range. The range itself stays tight to
            // the data: snapping it outward (as the time graphs do) left the curve using
            // half the plot height and halved the click-to-target precision.
            _fStep = AxisScale.Nice(_minF, _maxF, double.NaN, double.NaN).Step;
        }

        // X axis: the unit rides on the last tick's label instead of floating
        // separately (a standalone "mV" collides with the final tick).
        double tickStep = Math.Max(25, Math.Round((_maxV - _minV) / 8 / 25) * 25);
        var ticks = new List<double>();
        for (double v = _minV; v <= _maxV + 0.01; v += tickStep)
        {
            ticks.Add(v);
        }

        for (int i = 0; i < ticks.Count; i++)
        {
            var p = ToScreen(ticks[i], _minF);
            dc.DrawLine(GridPen, new Point(p.X, r.Y), new Point(p.X, r.Bottom));
            string text = i == ticks.Count - 1 ? $"{ticks[i]:F0} mV" : $"{ticks[i]:F0}";
            var formatted = Format(text, 10, LabelBrush);
            double x = Math.Min(p.X - (formatted.Width / 2), ActualWidth - formatted.Width - 2);
            dc.DrawText(formatted, new Point(Math.Max(2, x), r.Bottom + 5));
        }

        double fStep = double.IsFinite(_fStep) && _fStep > 0 ? _fStep : (_maxF - _minF) / 4;
        for (double f = Math.Ceiling(_minF / fStep) * fStep; f <= _maxF + 0.01 && fStep > 0; f += fStep)
        {
            var p = ToScreen(_minV, f);
            dc.DrawLine(GridPen, new Point(r.X, p.Y), new Point(r.Right, p.Y));
            DrawLabel(dc, $"{f:F0}", new Point(4, p.Y - 7), 10);
        }

        // Driver-stored per-point curve (with applied offsets): gold, dashed,
        // square markers — visually distinct from the measured blue curve.
        if (driver is { Count: > 1 })
        {
            var driverGeometry = new StreamGeometry();
            using (var ctx = driverGeometry.Open())
            {
                ctx.BeginFigure(ToScreen(driver[0].VoltageMv, driver[0].MaxClockMHz), false, false);
                for (int i = 1; i < driver.Count; i++)
                {
                    ctx.LineTo(ToScreen(driver[i].VoltageMv, driver[i].MaxClockMHz), true, false);
                }
            }

            driverGeometry.Freeze();
            dc.DrawGeometry(null, DriverPen, driverGeometry);
            foreach (var bin in driver)
            {
                var p = ToScreen(bin.VoltageMv, bin.MaxClockMHz);
                dc.DrawRectangle(DriverBrush, null, new Rect(p.X - 2, p.Y - 2, 4, 4));
            }
        }

        if (curve is not { Count: > 1 })
        {
            if (driver is not { Count: > 1 })
            {
                DrawLabel(dc,
                    "No curve data yet — run the GPU under load (a game, or the burn test) and the curve draws itself.",
                    new Point(r.X + 12, r.Y + (r.Height / 2)), 12, EmptyBrush);
            }

            return;
        }

        // Hit-density bars: how much time the GPU spent at each voltage. Bar
        // width follows the bins' own spacing on screen — not plot width / bin
        // count, which made bars ~3x too wide once the driver table widened
        // the voltage axis beyond the measured range (F-76).
        double barWidth = Math.Max(2, MinBinSpacing(curve) * 0.8);
        long peak = Math.Max(1, PeakSamples);
        foreach (var bin in curve)
        {
            double intensity = Math.Min(1.0, bin.Samples / (double)peak);
            if (intensity <= 0.01)
            {
                continue;
            }

            var top = ToScreen(bin.VoltageMv, bin.MaxClockMHz);
            var brush = DensityBrushes[Math.Clamp((int)Math.Ceiling(intensity * DensityBrushes.Length) - 1, 0, DensityBrushes.Length - 1)];
            dc.DrawRectangle(brush, null, new Rect(top.X - (barWidth / 2), top.Y, barWidth, r.Bottom - top.Y));
        }

        // Measured curve line.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ToScreen(curve[0].VoltageMv, curve[0].MaxClockMHz), false, false);
            for (int i = 1; i < curve.Count; i++)
            {
                ctx.LineTo(ToScreen(curve[i].VoltageMv, curve[i].MaxClockMHz), true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, CurvePen, geometry);

        foreach (var bin in curve)
        {
            dc.DrawEllipse(CurveBrush, null, ToScreen(bin.VoltageMv, bin.MaxClockMHz), 2.5, 2.5);
        }

        // Hover readout: the nearest measured bin under the mouse (suppressed
        // while dragging a target pick, whose own crosshair takes over).
        if (_hover is { } hover && !IsMouseCaptured)
        {
            VfBin? nearest = null;
            double bestDx = double.MaxValue;
            foreach (var bin in curve)
            {
                double dx = Math.Abs(ToScreen(bin.VoltageMv, bin.MaxClockMHz).X - hover.X);
                if (dx < bestDx)
                {
                    bestDx = dx;
                    nearest = bin;
                }
            }

            if (nearest is { } hit)
            {
                var p = ToScreen(hit.VoltageMv, hit.MaxClockMHz);
                dc.DrawLine(GridPen, new Point(p.X, r.Y), new Point(p.X, r.Bottom));
                dc.DrawEllipse(null, CurvePen, p, 5.5, 5.5);

                var hoverLabel = Format(
                    $"{hit.MaxClockMHz:F0} MHz @ {hit.VoltageMv:F0} mV · {hit.Samples:N0} samples",
                    11, PillTextBrush);
                double hxp = Math.Clamp(p.X + 10, r.X + 2, r.Right - hoverLabel.Width - 8);
                double hyp = Math.Clamp(p.Y - 26, r.Y + 2, r.Bottom - hoverLabel.Height - 4);
                var hoverPill = new Rect(hxp - 4, hyp - 2, hoverLabel.Width + 8, hoverLabel.Height + 4);
                dc.DrawRoundedRectangle(PillBrush, null, hoverPill, 4, 4);
                dc.DrawText(hoverLabel, new Point(hxp, hyp));
            }
        }

        // Target marker + crosshair.
        if (TargetVoltage > 0 && TargetClock > 0)
        {
            var t = ToScreen(TargetVoltage, TargetClock);
            dc.DrawLine(TargetDashPen, new Point(r.X, t.Y), new Point(r.Right, t.Y));
            dc.DrawLine(TargetDashPen, new Point(t.X, r.Y), new Point(t.X, r.Bottom));
            dc.DrawEllipse(null, TargetRingPen, t, 6, 6);

            var targetLabel = Format($"target {TargetClock:F0} MHz @ {TargetVoltage:F0} mV", 11, TargetBrush);
            double tx = Math.Clamp(t.X + 10, r.X + 2, r.Right - targetLabel.Width - 6);
            double ty = Math.Clamp(t.Y - 20, r.Y + 2, r.Bottom - targetLabel.Height - 2);
            var targetPill = new Rect(tx - 3, ty - 1, targetLabel.Width + 6, targetLabel.Height + 2);
            dc.DrawRoundedRectangle(PillBrush, null, targetPill, 3, 3);
            dc.DrawText(targetLabel, new Point(tx, ty));
        }

        // Live operating point, labeled on a backing pill so it stays readable
        // wherever it lands (on the curve, in shaded bars, near edges).
        if (LiveVoltage > 0 && LiveClock > 0)
        {
            var live = ToScreen(
                Math.Clamp(LiveVoltage, _minV, _maxV),
                Math.Clamp(LiveClock, _minF, _maxF));
            dc.DrawEllipse(LiveBrush, null, live, 4.5, 4.5);

            var label = Format("now", 10, LiveBrush);
            double lx = live.X + 9;
            double ly = live.Y - (label.Height / 2);
            if (lx + label.Width + 6 > r.Right)
            {
                lx = live.X - label.Width - 13;
            }

            ly = Math.Clamp(ly, r.Y + 2, r.Bottom - label.Height - 2);
            var pill = new Rect(lx - 3, ly - 1, label.Width + 6, label.Height + 2);
            dc.DrawRoundedRectangle(PillBrush, null, pill, 3, 3);
            dc.DrawText(label, new Point(lx, ly));
        }
    }

    /// <summary>Smallest horizontal distance between adjacent measured bins, in pixels.</summary>
    private double MinBinSpacing(IReadOnlyList<VfBin> bins)
    {
        double spacing = double.MaxValue;
        for (int i = 1; i < bins.Count; i++)
        {
            double dx = Math.Abs(ToScreen(bins[i].VoltageMv, 0).X - ToScreen(bins[i - 1].VoltageMv, 0).X);
            if (dx > 0.5)
            {
                spacing = Math.Min(spacing, dx);
            }
        }

        return spacing == double.MaxValue ? PlotRect.Width / Math.Max(1, bins.Count) : spacing;
    }

    /// <summary>
    /// Shaped text, cached: axis labels repeat every tick and only change with
    /// the range, so re-shaping them each redraw was wasted work. The cache is
    /// dropped on a DPI change and when it grows past a screenful of labels.
    /// </summary>
    private FormattedText Format(string text, double size, Brush brush)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _labelCacheDpi || _labelCache.Count > 128)
        {
            _labelCache.Clear();
            _labelCacheDpi = dpi;
        }

        if (!_labelCache.TryGetValue((text, size, brush), out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, size, brush, dpi);
            _labelCache[(text, size, brush)] = formatted;
        }

        return formatted;
    }

    private void DrawLabel(DrawingContext dc, string text, Point at, double size, Brush? brush = null)
    {
        dc.DrawText(Format(text, size, brush ?? LabelBrush), at);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Pick(e.GetPosition(this));
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
        {
            Pick(e.GetPosition(this));
            return;
        }

        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void Pick(Point position)
    {
        if (Curve is not { Count: > 1 })
        {
            return;
        }

        var (voltage, clock) = ToData(position);
        TargetPicked?.Invoke(this, (voltage, clock));
    }
}
