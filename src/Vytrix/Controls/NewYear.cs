using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vytrix.Controls;

/// <summary>
/// A New Year night: a city skyline with lit windows, the new year's number glowing in gold,
/// and fireworks bursting overhead. Every burst is one frozen drawing; only its scale, drop and
/// opacity animate, so the scene stays cheap to run.
/// </summary>
public sealed class FireworksSky : Canvas
{
    public const double DesignWidth = 640, DesignHeight = 560;
    private readonly Random _rng;
    private readonly List<AnimationClock> _clocks = [];
    private bool _running;

    private static readonly Color[] BurstColors =
    [
        Color.FromRgb(0xF6, 0xD0, 0x6F), Color.FromRgb(0xFF, 0x7A, 0x9C), Color.FromRgb(0x7D, 0xD3, 0xFC),
        Color.FromRgb(0xC4, 0xB5, 0xFD), Color.FromRgb(0x86, 0xEF, 0xAC), Color.FromRgb(0xFF, 0xF4, 0xD6),
    ];

    /// <summary>The year being celebrated: the coming year from October, otherwise this year.</summary>
    public static int CelebratedYear(DateTime now) => now.Month >= 10 ? now.Year + 1 : now.Year;

    private readonly List<(Image Img, ScaleTransform Scale)> _bursts = [];

    /// <summary>For still renders: shows every burst fully bloomed (animations never tick in a snapshot).</summary>
    internal void ShowStill()
    {
        foreach (var (img, scale) in _bursts)
        {
            img.ApplyAnimationClock(OpacityProperty, null);
            img.Opacity = 0.95;
            scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
        }
    }

    public FireworksSky(int seed = 2027) : this(seed, DateTime.Now) { }

    internal FireworksSky(int seed, DateTime now)
    {
        _rng = new Random(seed);
        Width = DesignWidth;
        Height = DesignHeight;
        Children.Add(Host(BuildGlow()));
        // Bursts sit behind the skyline so low ones disappear behind buildings.
        (double x, double y, double r, double period, double phase)[] bursts =
        [
            (170, 150, 95, 3.4, 0.0), (430, 110, 110, 3.9, 1.1), (300, 215, 75, 3.1, 2.0),
            (530, 230, 70, 3.6, 0.6), (95, 260, 60, 4.2, 2.6), (370, 300, 55, 3.3, 1.6),
        ];
        for (int i = 0; i < bursts.Length; i++)
        {
            var (x, y, r, period, phase) = bursts[i];
            AddBurst(x, y, r, BurstColors[i % BurstColors.Length], BurstColors[(i + 2) % BurstColors.Length], period, phase);
        }
        Children.Add(BuildYear(CelebratedYear(now)));
        Children.Add(Host(BuildSkyline()));
    }

    private static UIElement Host(Drawing d)
    {
        d.Freeze();
        return new Image { Source = new DrawingImage(d), Width = DesignWidth, Height = DesignHeight, Stretch = Stretch.None };
    }

    private static Drawing BuildGlow()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            var glow = new RadialGradientBrush(Color.FromArgb(0x38, 0x7C, 0x6B, 0xF2), Color.FromArgb(0x00, 0x7C, 0x6B, 0xF2));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(DesignWidth / 2, 230), 320, 260);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, DesignWidth, DesignHeight));
        }
        return g;
    }

    private Drawing BuildSkyline()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, DesignWidth, DesignHeight));
            var far = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x3A)); far.Freeze();
            var near = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x26)); near.Freeze();
            var lit = new SolidColorBrush(Color.FromArgb(0xD0, 0xF6, 0xD0, 0x6F)); lit.Freeze();
            var dim = new SolidColorBrush(Color.FromArgb(0x55, 0xF6, 0xD0, 0x6F)); dim.Freeze();
            const double ground = DesignHeight - 40;
            // Two rows of buildings for depth.
            foreach (var (brush, minH, maxH, windows) in new[] { (far, 90.0, 190.0, false), (near, 60.0, 150.0, true) })
            {
                double x = -10;
                while (x < DesignWidth + 10)
                {
                    double w = 34 + _rng.NextDouble() * 52, h = minH + _rng.NextDouble() * (maxH - minH);
                    dc.DrawRectangle(brush, null, new Rect(x, ground - h, w, h + 40));
                    if (_rng.NextDouble() < 0.25) dc.DrawRectangle(brush, null, new Rect(x + w / 2 - 2, ground - h - 22, 4, 22));   // antenna
                    if (windows)
                        for (double wy = ground - h + 10; wy < ground - 8; wy += 13)
                            for (double wx = x + 6; wx < x + w - 8; wx += 11)
                                if (_rng.NextDouble() < 0.42) dc.DrawRectangle(_rng.NextDouble() < 0.7 ? dim : lit, null, new Rect(wx, wy, 5, 6));
                    x += w + 2 + _rng.NextDouble() * 6;
                }
            }
            var reflection = new LinearGradientBrush(Color.FromArgb(0x22, 0xF6, 0xD0, 0x6F), Color.FromArgb(0x00, 0xF6, 0xD0, 0x6F), 90);
            reflection.Freeze();
            dc.DrawRectangle(reflection, null, new Rect(0, ground, DesignWidth, 40));
        }
        return g;
    }

    private UIElement BuildYear(int year)
    {
        var text = new FormattedText(year.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 76, Brushes.White, 1.0);
        // Above the tallest building (and its antenna), so the number is never hidden.
        var geo = text.BuildGeometry(new Point((DesignWidth - text.Width) / 2, DesignHeight - 40 - 318));
        var gold = new LinearGradientBrush(Color.FromRgb(0xFF, 0xF1, 0xC1), Color.FromRgb(0xE9, 0xB9, 0x49), 90);
        var path = new System.Windows.Shapes.Path
        {
            Data = geo, Fill = gold,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0xF6, 0xD0, 0x6F), BlurRadius = 26, ShadowDepth = 0, Opacity = 0.75 },
        };
        Animate(path, OpacityProperty, 0.8, 1.0, 2.2, 0);
        return path;
    }

    private void AddBurst(double cx, double cy, double radius, Color main, Color tip, double period, double phase)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            int sparks = 26 + _rng.Next(10);
            var trail = new SolidColorBrush(Color.FromArgb(0x70, main.R, main.G, main.B)); trail.Freeze();
            var head = new SolidColorBrush(main); head.Freeze();
            var tipB = new SolidColorBrush(tip); tipB.Freeze();
            for (int i = 0; i < sparks; i++)
            {
                double a = i * Math.PI * 2 / sparks + _rng.NextDouble() * 0.12;
                double len = radius * (0.75 + _rng.NextDouble() * 0.25);
                var end = new Point(radius + Math.Cos(a) * len, radius + Math.Sin(a) * len);
                var pen = new Pen(trail, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                dc.DrawLine(pen, new Point(radius + Math.Cos(a) * len * 0.35, radius + Math.Sin(a) * len * 0.35), end);
                dc.DrawEllipse(i % 3 == 0 ? tipB : head, null, end, 2.4, 2.4);
            }
            var core = new RadialGradientBrush(Color.FromArgb(0x90, main.R, main.G, main.B), Color.FromArgb(0x00, main.R, main.G, main.B)); core.Freeze();
            dc.DrawEllipse(core, null, new Point(radius, radius), radius * 0.22, radius * 0.22);
        }
        g.Freeze();
        var img = new Image { Source = new DrawingImage(g), Width = radius * 2, Height = radius * 2, Opacity = 0 };
        SetLeft(img, cx - radius);
        SetTop(img, cy - radius);
        var scale = new ScaleTransform(0.1, 0.1);
        var drop = new TranslateTransform();
        img.RenderTransformOrigin = new Point(0.5, 0.5);
        img.RenderTransform = new TransformGroup { Children = { scale, drop } };
        Children.Add(img);
        _bursts.Add((img, scale));

        // One cycle: a quick bloom, a slow fade with a little gravity, then darkness until the next burst.
        var p = TimeSpan.FromSeconds(period);
        Clock(scale, ScaleTransform.ScaleXProperty, Keys(p, (0, 0.1), (0.07, 0.85), (0.35, 1.0), (1, 1.0)), phase);
        Clock(scale, ScaleTransform.ScaleYProperty, Keys(p, (0, 0.1), (0.07, 0.85), (0.35, 1.0), (1, 1.0)), phase);
        Clock(drop, TranslateTransform.YProperty, Keys(p, (0, 0), (0.1, 0), (0.55, 16), (1, 16)), phase);
        Clock(img, OpacityProperty, Keys(p, (0, 0), (0.03, 1), (0.25, 0.95), (0.55, 0), (1, 0)), phase);
    }

    private static DoubleAnimationUsingKeyFrames Keys(TimeSpan period, params (double t, double v)[] frames)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (t, v) in frames)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromPercent(t)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        return a;
    }

    private void Clock(Animatable target, DependencyProperty prop, AnimationTimeline anim, double phase)
    {
        anim.BeginTime = TimeSpan.FromSeconds(-phase);
        Timeline.SetDesiredFrameRate(anim, 30);
        var c = anim.CreateClock();
        target.ApplyAnimationClock(prop, c);
        _clocks.Add(c);
        if (!_running) c.Controller!.Pause();
    }

    private void Clock(UIElement target, DependencyProperty prop, AnimationTimeline anim, double phase)
    {
        anim.BeginTime = TimeSpan.FromSeconds(-phase);
        Timeline.SetDesiredFrameRate(anim, 30);
        var c = anim.CreateClock();
        target.ApplyAnimationClock(prop, c);
        _clocks.Add(c);
        if (!_running) c.Controller!.Pause();
    }

    private void Animate(UIElement target, DependencyProperty prop, double from, double to, double seconds, double phase)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-phase), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Clock(target, prop, anim, 0);
    }

    public void SetRunning(bool run)
    {
        _running = run;
        foreach (var c in _clocks) { if (run) c.Controller!.Resume(); else c.Controller!.Pause(); }
    }
}
