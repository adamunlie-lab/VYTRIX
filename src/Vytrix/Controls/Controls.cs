using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vytrix.Controls;

/// <summary>Lightweight real-time line chart. Draws directly in OnRender (no element tree per point).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(nameof(AutoScale), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    private double[] _values = [];
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = new DashStyle([2, 4], 0) });

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    public void SetValues(double[] values)
    {
        lock (values) _values = (double[])values.Clone();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (ShowGrid)
            for (int i = 1; i < 4; i++) dc.DrawLine(GridPen, new Point(0, Math.Round(h * i / 4) + 0.5), new Point(w, Math.Round(h * i / 4) + 0.5));

        var all = _values;
        int first = Array.FindIndex(all, v => v >= 0);   // skip slots with no sample yet
        if (first < 0 || all.Length - first < 2) return;
        var vals = all[first..];
        double offset = (double)first / (all.Length - 1) * w;

        double max = AutoScale ? Math.Max(1, vals.Max() * 1.15) : Max;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        double step = (w - offset) / (vals.Length - 1);
        Point P(int i) => new(offset + i * step, h - Math.Clamp(Math.Max(0, vals[i]) / max, 0, 1) * (h - 2) - 1);
        using (var c = line.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (int i = 1; i < vals.Length; i++) c.LineTo(P(i), true, true);
        }
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(offset, h), true, true);
            for (int i = 0; i < vals.Length; i++) c.LineTo(P(i), false, false);
            c.LineTo(new Point(w, h), false, false);
        }
        line.Freeze(); area.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.MediumPurple;
        var fill = new LinearGradientBrush(Color.FromArgb(0x40, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Thin horizontal usage bar.</summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Meter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Meter),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(Meter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>Track brush; defaults to the current theme's Surface3.</summary>
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public Meter() { Height = 6; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2;
        dc.DrawRoundedRectangle(Track ?? Ui.Res("Surface3Brush"), null, new Rect(0, 0, w, h), r, r);
        if (Value <= 0) return;
        double fw = Math.Max(h, w * Math.Clamp(Value, 0, 100) / 100);
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fw, h), r, r);
    }
}

/// <summary>Circular gauge (0–100). Shows "N/A" when Value is negative.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x24, 0x24, 0x2C));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        double t = Thickness, rad = (size - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, t), c, rad, rad);
        if (Value <= 0) return;
        double v = Math.Clamp(Value, 0, 99.999) / 100;
        double a0 = -Math.PI / 2, a1 = a0 + v * 2 * Math.PI;
        var start = new Point(c.X + rad * Math.Cos(a0), c.Y + rad * Math.Sin(a0));
        var end = new Point(c.X + rad * Math.Cos(a1), c.Y + rad * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(rad, rad), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}

/// <summary>Indeterminate loading indicator.</summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private readonly RotateTransform _rot = new();

    public Spinner()
    {
        Width = Height = 18;
        RenderTransform = _rot;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else _rot.BeginAnimation(RotateTransform.AngleProperty, null);   // stop animating when hidden: no idle CPU
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight), t = Math.Max(2, s / 9), r = (s - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), t), c, r, r);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}

/// <summary>Accent colour customisation. Swaps the dynamic accent brushes app-wide.</summary>
public static class ThemeManager
{
    public static readonly (string Name, Color Main, Color Hover)[] Accents =
    [
        ("Violet", Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xA7, 0x8B, 0xFA)),
        ("Cyan", Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x22, 0xD3, 0xEE)),
        ("Emerald", Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99)),
        ("Amber", Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xFB, 0xBF, 0x24)),
        ("Rose", Color.FromRgb(0xF4, 0x3F, 0x5E), Color.FromRgb(0xFB, 0x71, 0x85)),
        ("Sakura", Color.FromRgb(0xDB, 0x27, 0x77), Color.FromRgb(0xF4, 0x72, 0xB6)),
        ("Crimson", Color.FromRgb(0xC8, 0x10, 0x2E), Color.FromRgb(0xE6, 0x39, 0x46)),
        ("Gold", Color.FromRgb(0xD9, 0xA4, 0x2B), Color.FromRgb(0xF6, 0xD0, 0x6F)),
    ];

    public const string Christmas = "Christmas";
    public const string NewYear = "New Year";
    public const string CherryBlossom = "Cherry Blossom";
    public const string Graphite = "Graphite";
    public static readonly string[] Themes = [Christmas, NewYear, CherryBlossom, Graphite];

    /// <summary>The signature accent each theme switches to when selected.</summary>
    public static string DefaultAccent(string theme) => theme switch { Christmas => "Crimson", NewYear => "Gold", CherryBlossom => "Sakura", _ => "Violet" };

    /// <summary>
    /// Seasonal switching: Christmas becomes New Year from 27 December to 6 January, and an
    /// automatically chosen New Year theme steps back to Graphite afterwards. Returns null for no change.
    /// </summary>
    public static string? SeasonalSwitch(string current, DateTime today, bool newYearWasAutomatic)
    {
        bool newYearSeason = (today.Month == 12 && today.Day >= 27) || (today.Month == 1 && today.Day <= 6);
        if (newYearSeason && current == Christmas) return NewYear;
        if (!newYearSeason && current == NewYear && newYearWasAutomatic) return Graphite;
        return null;
    }

    // Midnight-navy palette with champagne gold: the colours of a New Year's Eve sky.
    private static readonly Dictionary<string, Color> NewYearPalette = new()
    {
        ["BgBrush"] = Color.FromRgb(0x09, 0x0C, 0x1C),
        ["SidebarBrush"] = Color.FromRgb(0x0C, 0x10, 0x24),
        ["SurfaceBrush"] = Color.FromArgb(0xEE, 0x11, 0x16, 0x2E),
        ["Surface2Brush"] = Color.FromArgb(0xF2, 0x16, 0x1C, 0x38),
        ["Surface3Brush"] = Color.FromRgb(0x1F, 0x27, 0x4A),
        ["BorderBrush"] = Color.FromRgb(0x26, 0x2F, 0x55),
        ["BorderStrongBrush"] = Color.FromRgb(0x36, 0x40, 0x6C),
        ["TextBrush"] = Color.FromRgb(0xF7, 0xF3, 0xE8),
        ["TextDimBrush"] = Color.FromRgb(0xC8, 0xCB, 0xDC),
        ["TextMutedBrush"] = Color.FromRgb(0x85, 0x8C, 0xAD),
        ["GoodBrush"] = Color.FromRgb(0x86, 0xEF, 0xAC),
        ["WarnBrush"] = Color.FromRgb(0xF6, 0xD0, 0x6F),
        ["BadBrush"] = Color.FromRgb(0xFF, 0x7A, 0x9C),
        ["GoodSoftBrush"] = Color.FromArgb(0x1F, 0x86, 0xEF, 0xAC),
        ["WarnSoftBrush"] = Color.FromArgb(0x1F, 0xF6, 0xD0, 0x6F),
        ["BadSoftBrush"] = Color.FromArgb(0x1F, 0xFF, 0x7A, 0x9C),
        ["Accent2Brush"] = Color.FromRgb(0x7D, 0xD3, 0xFC),
    };
    private static readonly Color[] NewYearLogo = [Color.FromRgb(0xFF, 0xF1, 0xC1), Color.FromRgb(0xF6, 0xD0, 0x6F), Color.FromRgb(0xC4, 0xB5, 0xFD)];

    // Midnight-pine palette: deep evergreen surfaces, warm snow-white text, crimson and gold accents.
    private static readonly Dictionary<string, Color> ChristmasPalette = new()
    {
        ["BgBrush"] = Color.FromRgb(0x09, 0x12, 0x10),
        ["SidebarBrush"] = Color.FromRgb(0x0B, 0x16, 0x13),
        ["SurfaceBrush"] = Color.FromArgb(0xEE, 0x0F, 0x1D, 0x19),
        ["Surface2Brush"] = Color.FromArgb(0xF2, 0x13, 0x25, 0x20),
        ["Surface3Brush"] = Color.FromRgb(0x1A, 0x30, 0x29),
        ["BorderBrush"] = Color.FromRgb(0x22, 0x39, 0x30),
        ["BorderStrongBrush"] = Color.FromRgb(0x2F, 0x4B, 0x3F),
        ["TextBrush"] = Color.FromRgb(0xF6, 0xF2, 0xE9),
        ["TextDimBrush"] = Color.FromRgb(0xC4, 0xCF, 0xC7),
        ["TextMutedBrush"] = Color.FromRgb(0x7E, 0x93, 0x88),
        ["GoodBrush"] = Color.FromRgb(0x6E, 0xE7, 0xB7),
        ["WarnBrush"] = Color.FromRgb(0xF4, 0xC9, 0x5D),
        ["BadBrush"] = Color.FromRgb(0xFF, 0x6B, 0x6B),
        ["GoodSoftBrush"] = Color.FromArgb(0x1F, 0x6E, 0xE7, 0xB7),
        ["WarnSoftBrush"] = Color.FromArgb(0x1F, 0xF4, 0xC9, 0x5D),
        ["BadSoftBrush"] = Color.FromArgb(0x1F, 0xFF, 0x6B, 0x6B),
        ["Accent2Brush"] = Color.FromRgb(0xF4, 0xC9, 0x5D),
    };
    private static readonly Color[] ChristmasLogo = [Color.FromRgb(0xFF, 0xF1, 0xC1), Color.FromRgb(0xE9, 0xC4, 0x6A), Color.FromRgb(0xC8, 0x10, 0x2E)];
    public static string Current { get; private set; } = Graphite;
    public static event Action? Changed;

    private static readonly string[] PaletteKeys =
    [
        "BgBrush", "SidebarBrush", "SurfaceBrush", "Surface2Brush", "Surface3Brush", "BorderBrush", "BorderStrongBrush",
        "TextBrush", "TextDimBrush", "TextMutedBrush", "GoodBrush", "WarnBrush", "BadBrush", "GoodSoftBrush", "WarnSoftBrush", "BadSoftBrush", "Accent2Brush",
    ];

    // Night-sakura palette: deep plum surfaces so pink blossoms glow and text keeps strong contrast.
    // Surfaces are very slightly translucent so the tree behind the pages shows through softly.
    private static readonly Dictionary<string, Color> SakuraPalette = new()
    {
        ["BgBrush"] = Color.FromRgb(0x14, 0x0D, 0x13),
        ["SidebarBrush"] = Color.FromRgb(0x19, 0x10, 0x18),
        ["SurfaceBrush"] = Color.FromArgb(0xEE, 0x20, 0x15, 0x1E),
        ["Surface2Brush"] = Color.FromArgb(0xF2, 0x29, 0x1B, 0x26),
        ["Surface3Brush"] = Color.FromRgb(0x33, 0x22, 0x30),
        ["BorderBrush"] = Color.FromRgb(0x3D, 0x29, 0x38),
        ["BorderStrongBrush"] = Color.FromRgb(0x4F, 0x35, 0x49),
        ["TextBrush"] = Color.FromRgb(0xFC, 0xF1, 0xF6),
        ["TextDimBrush"] = Color.FromRgb(0xD3, 0xB8, 0xC8),
        ["TextMutedBrush"] = Color.FromRgb(0x98, 0x7A, 0x8F),
        ["GoodBrush"] = Color.FromRgb(0x6E, 0xE7, 0xB7),
        ["WarnBrush"] = Color.FromRgb(0xFC, 0xD3, 0x4D),
        ["BadBrush"] = Color.FromRgb(0xFB, 0x71, 0x85),
        ["GoodSoftBrush"] = Color.FromArgb(0x1F, 0x6E, 0xE7, 0xB7),
        ["WarnSoftBrush"] = Color.FromArgb(0x1F, 0xFC, 0xD3, 0x4D),
        ["BadSoftBrush"] = Color.FromArgb(0x1F, 0xFB, 0x71, 0x85),
        ["Accent2Brush"] = Color.FromRgb(0xC4, 0xB5, 0xFD),
    };
    private static readonly Color[] SakuraLogo = [Color.FromRgb(0xFF, 0xE4, 0xF1), Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xC0, 0x84, 0xFC)];
    private static Dictionary<string, Color>? _graphite;
    private static Color[]? _graphiteLogo;

    /// <summary>Applies a palette by recolouring the shared brushes, so every element (XAML or code) updates live.</summary>
    public static void ApplyTheme(string name)
    {
        var res = Application.Current.Resources;
        _graphite ??= PaletteKeys.ToDictionary(k => k, k => ((SolidColorBrush)res[k]).Color);
        var logo = (LinearGradientBrush)res["LogoBrush"];
        _graphiteLogo ??= logo.GradientStops.Select(s => s.Color).ToArray();

        if (!Themes.Contains(name)) name = Christmas;
        var map = name switch { CherryBlossom => SakuraPalette, Christmas => ChristmasPalette, NewYear => NewYearPalette, _ => _graphite };
        foreach (var (key, color) in map) SetColor(res, key, color);
        // Gradient stops from XAML are read-only, so the logo brush is replaced (consumers use DynamicResource).
        var stops = name switch { CherryBlossom => SakuraLogo, Christmas => ChristmasLogo, NewYear => NewYearLogo, _ => _graphiteLogo };
        var newLogo = new LinearGradientBrush { StartPoint = logo.StartPoint, EndPoint = logo.EndPoint };
        for (int i = 0; i < stops.Length; i++) newLogo.GradientStops.Add(new GradientStop(stops[i], stops.Length == 1 ? 0 : i / (double)(stops.Length - 1)));
        res["LogoBrush"] = newLogo;
        Current = name;
        Changed?.Invoke();
    }

    public static void Apply(string name)
    {
        var a = Accents.FirstOrDefault(x => x.Name == name);
        if (a.Name is null) a = Accents[0];
        var res = Application.Current.Resources;
        SetColor(res, "AccentBrush", a.Main);
        SetColor(res, "AccentHoverBrush", a.Hover);
        SetColor(res, "AccentSoftBrush", Color.FromArgb(0x22, a.Main.R, a.Main.G, a.Main.B));
    }

    private static void SetColor(ResourceDictionary res, string key, Color c)
    {
        if (res[key] is SolidColorBrush b && !b.IsFrozen) b.Color = c;
        else res[key] = new SolidColorBrush(c);   // frozen: replace (DynamicResource users still update)
    }
}

/// <summary>Small helpers for building consistent UI from code.</summary>
public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static Geometry Icon(string key) => (Geometry)Application.Current.Resources[key];

    public static System.Windows.Shapes.Path IconPath(string key, double size = 16, string brush = "TextDimBrush") => new()
    {
        Data = Icon(key), Style = Style("Icon"), Width = size, Height = size, Stroke = Res(brush),
    };

    public static Border Badge(string text, string fg, string bg) => new()
    {
        Style = Style("Pill"), Background = Res(bg),
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res(fg) },
    };

    public static Button IconButton(string iconKey, string text, string? style = null, string iconBrush = "TextBrush")
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(IconPath(iconKey, 14, style == "Primary" ? "TextBrush" : iconBrush));
        if (style == "Primary") ((System.Windows.Shapes.Path)sp.Children[0]).Stroke = Brushes.White;
        var label = new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        label.Foreground = style switch { "Primary" => Brushes.White, "Danger" => Res("BadBrush"), "Link" => Res("AccentHoverBrush"), _ => Res("TextBrush") };
        sp.Children.Add(label);
        var b = new Button { Content = sp };
        if (style is not null) b.Style = Style(style);
        return b;
    }
}
