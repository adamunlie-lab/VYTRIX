using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vytrix.Controls;

/// <summary>
/// A decorated Christmas tree drawn in code: layered boughs with snow, a gold bead garland,
/// ornaments, three banks of fairy lights that twinkle out of phase, a glowing star and
/// presents. Static artwork is frozen into drawings; only light opacity and the star's glow
/// animate, so it costs very little to run.
/// </summary>
public sealed class ChristmasTree : Canvas
{
    public const double DesignWidth = 560, DesignHeight = 620;
    private const double Cx = DesignWidth / 2, TopY = 92, BaseY = 520;

    private readonly Random _rng;
    private readonly List<AnimationClock> _clocks = [];
    private bool _running;

    private static readonly Color[] LightColors =
    [
        Color.FromRgb(0xFF, 0xE3, 0x9B), Color.FromRgb(0xFF, 0xD1, 0x66), Color.FromRgb(0xFF, 0x6B, 0x6B),
        Color.FromRgb(0x9B, 0xE8, 0xFF), Color.FromRgb(0xFF, 0xF4, 0xD6), Color.FromRgb(0x8C, 0xF5, 0xB0),
    ];

    public ChristmasTree(int seed = 1225)
    {
        _rng = new Random(seed);   // deterministic: the same tree every launch
        Width = DesignWidth;
        Height = DesignHeight;
        Children.Add(Host(BuildBackdrop()));
        Children.Add(Host(BuildTree()));
        // Three banks of lights twinkle independently.
        var lights = BuildLights();
        for (int i = 0; i < lights.Length; i++)
        {
            var layer = Host(lights[i]);
            Children.Add(layer);
            Animate(layer, OpacityProperty, 0.35, 1.0, 1.1 + i * 0.37, i * 0.5);
        }
        Children.Add(BuildStar());
        Children.Add(Host(BuildPresents()));
    }

    // ───────────────────────── artwork

    private static double HalfWidthAt(double y) => 18 + (y - TopY) / (BaseY - TopY) * 200;   // cone silhouette

    private static Drawing BuildBackdrop()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            var glow = new RadialGradientBrush(Color.FromArgb(0x40, 0xE9, 0xC4, 0x6A), Color.FromArgb(0x00, 0xE9, 0xC4, 0x6A));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(Cx, 300), 270, 290);
            // Snowy ground.
            var snow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x70, 0xF4, 0xF7, 0xFA), 0),
                    new GradientStop(Color.FromArgb(0x30, 0xE6, 0xEE, 0xF4), 0.55),
                    new GradientStop(Color.FromArgb(0x00, 0xE6, 0xEE, 0xF4), 1),
                },
            };
            snow.Freeze();
            dc.DrawEllipse(snow, null, new Point(Cx, BaseY + 46), 270, 46);
            var shadow = new RadialGradientBrush(Color.FromArgb(0x55, 0x04, 0x0A, 0x08), Color.FromArgb(0x00, 0x04, 0x0A, 0x08));
            shadow.Freeze();
            dc.DrawEllipse(shadow, null, new Point(Cx, BaseY + 40), 200, 18);
        }
        g.Freeze();
        return g;
    }

    private Drawing BuildTree()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // Trunk.
            var bark = new LinearGradientBrush(Color.FromRgb(0x3B, 0x24, 0x16), Color.FromRgb(0x5C, 0x3A, 0x22), 0);
            bark.Freeze();
            dc.DrawRoundedRectangle(bark, null, new Rect(Cx - 20, BaseY - 10, 40, 56), 4, 4);

            // Five overlapping tiers, back to front, each a curved cone with a scalloped hem.
            double[] tierTop = [92, 150, 220, 300, 380];
            double[] tierBottom = [210, 285, 365, 450, 528];
            for (int i = 0; i < tierTop.Length; i++)
            {
                double top = tierTop[i], bottom = tierBottom[i];
                double hw = HalfWidthAt(bottom) + 10;
                int scallops = 5 + i;
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(new Point(Cx, top), true, true);
                    c.QuadraticBezierTo(new Point(Cx + hw * 0.35, top + (bottom - top) * 0.55), new Point(Cx + hw, bottom), true, true);
                    // Scalloped hem: soft dips between boughs.
                    for (int s = scallops - 1; s >= 0; s--)
                    {
                        double x0 = Cx - hw + 2 * hw * s / scallops;
                        double x1 = Cx - hw + 2 * hw * (s + 0.5) / scallops;
                        c.QuadraticBezierTo(new Point(x1, bottom + 14), new Point(x0, bottom), true, true);
                    }
                    c.QuadraticBezierTo(new Point(Cx - hw * 0.35, top + (bottom - top) * 0.55), new Point(Cx, top), true, true);
                }
                geo.Freeze();
                var pine = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(0x2A, 0x7A, 0x52), 0),
                        new GradientStop(Color.FromRgb(0x16, 0x55, 0x3A), 0.45),
                        new GradientStop(Color.FromRgb(0x0B, 0x33, 0x24), 1),
                    },
                };
                pine.Freeze();
                dc.DrawGeometry(pine, null, geo);

                // Needle texture: short light and dark strokes across the tier.
                var light = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x6F, 0xD0, 0x9A)), 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var dark = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x05, 0x1C, 0x13)), 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                light.Freeze(); dark.Freeze();
                for (int n = 0; n < 70 + i * 25; n++)
                {
                    double y = top + 18 + _rng.NextDouble() * (bottom - top - 18);
                    double w = HalfWidthAt(y) * (0.4 + 0.55 * (y - top) / (bottom - top));
                    double x = Cx + (_rng.NextDouble() * 2 - 1) * w;
                    double len = 5 + _rng.NextDouble() * 6, dir = x < Cx ? -1 : 1;
                    dc.DrawLine(_rng.NextDouble() < 0.6 ? light : dark, new Point(x, y), new Point(x + dir * len, y + len * 0.55));
                }

                // Snow resting on the bough tips.
                var snowBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xF5, 0xF9, 0xFC));
                snowBrush.Freeze();
                for (int s = 0; s < scallops; s++)
                {
                    double x = Cx - hw + 2 * hw * (s + 0.5) / scallops;
                    if (_rng.NextDouble() < 0.7) dc.DrawEllipse(snowBrush, null, new Point(x, bottom + 9), 9 + _rng.NextDouble() * 6, 3.2);
                }
            }

            // Gold bead garland: three swags across the tree.
            var bead = new RadialGradientBrush(Color.FromRgb(0xFF, 0xF1, 0xC1), Color.FromRgb(0xC9, 0x97, 0x2E)) { GradientOrigin = new Point(0.35, 0.3) };
            bead.Freeze();
            foreach (var (y0, y1, sag) in new[] { (190.0, 225.0, 28.0), (295.0, 340.0, 34.0), (410.0, 460.0, 40.0) })
            {
                var a = new Point(Cx - HalfWidthAt(y0) * 0.85, y0);
                var b = new Point(Cx + HalfWidthAt(y1) * 0.85, y1);
                var ctrl = new Point(Cx, (y0 + y1) / 2 + sag);
                for (double t = 0; t <= 1.0; t += 0.022)
                {
                    double u = 1 - t;
                    var p = new Point(u * u * a.X + 2 * u * t * ctrl.X + t * t * b.X, u * u * a.Y + 2 * u * t * ctrl.Y + t * t * b.Y);
                    dc.DrawEllipse(bead, null, p, 2.6, 2.6);
                }
            }

            // Ornaments with a highlight and a small gold cap.
            Color[] baubles = [Color.FromRgb(0xC8, 0x10, 0x2E), Color.FromRgb(0xE9, 0xC4, 0x6A), Color.FromRgb(0xC7, 0xD6, 0xE6), Color.FromRgb(0x9E, 0x1B, 0x32)];
            var cap = new SolidColorBrush(Color.FromRgb(0xD9, 0xB2, 0x4C));
            cap.Freeze();
            for (int n = 0; n < 24; n++)
            {
                double y = 170 + _rng.NextDouble() * 330;
                double x = Cx + (_rng.NextDouble() * 2 - 1) * HalfWidthAt(y) * 0.78;
                double r = 7 + _rng.NextDouble() * 5;
                var col = baubles[_rng.Next(baubles.Length)];
                var ball = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.32, 0.28), Center = new Point(0.45, 0.42), RadiusX = 0.7, RadiusY = 0.7,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0),
                        new GradientStop(col, 0.28),
                        new GradientStop(Color.FromRgb((byte)(col.R * 0.45), (byte)(col.G * 0.45), (byte)(col.B * 0.45)), 1),
                    },
                };
                ball.Freeze();
                dc.DrawRectangle(cap, null, new Rect(x - 2.5, y - r - 3, 5, 4));
                dc.DrawEllipse(ball, null, new Point(x, y), r, r);
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>Three banks of lights; each bank is a separate layer so they can twinkle out of phase.</summary>
    private Drawing[] BuildLights()
    {
        var banks = new DrawingGroup[3];
        var ctx = new DrawingContext[3];
        for (int i = 0; i < 3; i++) { banks[i] = new DrawingGroup(); ctx[i] = banks[i].Open(); }
        // Lights follow gentle spirals around the cone.
        int count = 0;
        for (double y = 120; y < 505; y += 6.5)
        {
            double phase = y / 34.0;
            double x = Cx + Math.Sin(phase) * HalfWidthAt(y) * 0.86;
            if (Math.Cos(phase) < -0.35) continue;   // skip the back of the tree
            var col = LightColors[_rng.Next(LightColors.Length)];
            var glow = new RadialGradientBrush(Color.FromArgb(0xB0, col.R, col.G, col.B), Color.FromArgb(0x00, col.R, col.G, col.B));
            glow.Freeze();
            var core = new SolidColorBrush(Color.FromRgb((byte)Math.Min(255, col.R + 30), (byte)Math.Min(255, col.G + 30), (byte)Math.Min(255, col.B + 30)));
            core.Freeze();
            var dc = ctx[count++ % 3];
            dc.DrawEllipse(glow, null, new Point(x, y), 9, 9);
            dc.DrawEllipse(core, null, new Point(x, y), 2.4, 2.4);
        }
        for (int i = 0; i < 3; i++) { ctx[i].Close(); banks[i].Freeze(); }
        return banks;
    }

    private FrameworkElement BuildStar()
    {
        var star = new Canvas { Width = DesignWidth, Height = DesignHeight };
        // Soft halo that breathes.
        var halo = new System.Windows.Shapes.Ellipse
        {
            Width = 110, Height = 110,
            Fill = new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xE3, 0x9B), Color.FromArgb(0x00, 0xFF, 0xE3, 0x9B)),
        };
        SetLeft(halo, Cx - 55); SetTop(halo, TopY - 70);
        star.Children.Add(halo);
        Animate(halo, OpacityProperty, 0.55, 1.0, 1.8, 0);
        // Slowly turning sparkle rays.
        var rays = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,-46 L0,46 M-46,0 L46,0 M-30,-30 L30,30 M-30,30 L30,-30"),
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xF1, 0xC1)), StrokeThickness = 1.2,
            RenderTransform = new RotateTransform(0),
        };
        SetLeft(rays, Cx); SetTop(rays, TopY - 16);
        star.Children.Add(rays);
        Animate((RotateTransform)rays.RenderTransform, RotateTransform.AngleProperty, 0, 360, 24, 0, reverse: false);
        // Five-point gold star.
        var pts = new PointCollection();
        for (int k = 0; k < 10; k++)
        {
            double a = -Math.PI / 2 + k * Math.PI / 5, r = k % 2 == 0 ? 26 : 11;
            pts.Add(new Point(Cx + Math.Cos(a) * r, TopY - 16 + Math.Sin(a) * r));
        }
        var gold = new LinearGradientBrush(Color.FromRgb(0xFF, 0xF4, 0xC8), Color.FromRgb(0xE0, 0xA8, 0x2E), 60);
        star.Children.Add(new System.Windows.Shapes.Polygon { Points = pts, Fill = gold, Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xA8)), StrokeThickness = 1 });
        return star;
    }

    private static Drawing BuildPresents()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            void Gift(double x, double w, double h, Color box, Color ribbon)
            {
                double y = BaseY + 44 - h;
                var b = new LinearGradientBrush(box, Color.FromRgb((byte)(box.R * 0.6), (byte)(box.G * 0.6), (byte)(box.B * 0.6)), 90);
                var rb = new SolidColorBrush(ribbon);
                b.Freeze(); rb.Freeze();
                dc.DrawRoundedRectangle(b, null, new Rect(x, y, w, h), 3, 3);
                dc.DrawRectangle(rb, null, new Rect(x + w / 2 - 3, y, 6, h));
                dc.DrawRectangle(rb, null, new Rect(x, y + h * 0.38, w, 5));
                var bow = new Pen(rb, 3);
                bow.Freeze();
                dc.DrawEllipse(null, bow, new Point(x + w / 2 - 7, y - 4), 7, 4);
                dc.DrawEllipse(null, bow, new Point(x + w / 2 + 7, y - 4), 7, 4);
            }
            Gift(Cx - 150, 64, 46, Color.FromRgb(0xC8, 0x10, 0x2E), Color.FromRgb(0xF4, 0xD6, 0x7A));
            Gift(Cx + 70, 56, 38, Color.FromRgb(0x1D, 0x6B, 0x4A), Color.FromRgb(0xF2, 0xF2, 0xF2));
            Gift(Cx + 128, 42, 54, Color.FromRgb(0xD9, 0xB2, 0x4C), Color.FromRgb(0xC8, 0x10, 0x2E));
            Gift(Cx - 84, 44, 32, Color.FromRgb(0xE8, 0xEE, 0xF4), Color.FromRgb(0xC8, 0x10, 0x2E));
        }
        g.Freeze();
        return g;
    }

    // ───────────────────────── animation

    private void Animate(Animatable target, DependencyProperty prop, double from, double to, double seconds, double phase, bool reverse = true)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = reverse, RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(-phase),
            EasingFunction = reverse ? new SineEase { EasingMode = EasingMode.EaseInOut } : null,
        };
        Timeline.SetDesiredFrameRate(a, 30);
        var c = a.CreateClock();
        c.Controller!.Pause();
        target.ApplyAnimationClock(prop, c);
        _clocks.Add(c);
    }

    private void Animate(UIElement target, DependencyProperty prop, double from, double to, double seconds, double phase)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-phase),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(a, 30);
        var c = a.CreateClock();
        c.Controller!.Pause();
        target.ApplyAnimationClock(prop, c);
        _clocks.Add(c);
    }

    public void SetRunning(bool run)
    {
        if (run == _running) return;
        _running = run;
        foreach (var c in _clocks) { if (run) c.Controller!.Resume(); else c.Controller!.Pause(); }
    }

    private static FrameworkElement Host(Drawing d) => new DrawingHost(d) { Width = DesignWidth, Height = DesignHeight };

    private sealed class DrawingHost : FrameworkElement
    {
        private readonly Drawing _d;
        public DrawingHost(Drawing d) { _d = d; IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(_d);
    }
}

/// <summary>Gentle snowfall across the window. Hit-test invisible; pauses with the scene.</summary>
public sealed class SnowField : Canvas
{
    private readonly List<AnimationClock> _clocks = [];
    private readonly Random _rng = new(24);
    private bool _running;
    private Size _builtFor;

    public SnowField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _builtFor.Width) > 120 || Math.Abs(e.NewSize.Height - _builtFor.Height) > 120) Rebuild();
        };
    }

    private void Rebuild()
    {
        foreach (var c in _clocks) c.Controller!.Stop();
        _clocks.Clear();
        Children.Clear();
        _builtFor = new Size(ActualWidth, ActualHeight);
        if (ActualWidth < 50 || ActualHeight < 50) return;

        int count = (int)Math.Clamp(ActualWidth / 42, 14, 36);
        for (int i = 0; i < count; i++)
        {
            double size = 2 + Math.Pow(_rng.NextDouble(), 2) * 5;   // mostly small, a few large (near) flakes
            var flake = new System.Windows.Shapes.Ellipse
            {
                Width = size, Height = size,
                Fill = new RadialGradientBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
                Opacity = 0.45 + _rng.NextDouble() * 0.5,
            };
            var move = new TranslateTransform();
            flake.RenderTransform = move;
            Children.Add(flake);
            double fall = 10 + (7 - size) * 1.8 + _rng.NextDouble() * 5;   // bigger flakes fall faster
            double x = _rng.NextDouble() * ActualWidth;
            double phase = -_rng.NextDouble() * fall;
            Add(move, TranslateTransform.YProperty, new DoubleAnimation(-10, ActualHeight + 10, TimeSpan.FromSeconds(fall)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(phase) });
            Add(move, TranslateTransform.XProperty, new DoubleAnimation(x - 18, x + 18, TimeSpan.FromSeconds(2.5 + _rng.NextDouble() * 3))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, BeginTime = TimeSpan.FromSeconds(-_rng.NextDouble() * 3) });
        }
        if (!_running) foreach (var c in _clocks) c.Controller!.Pause();
    }

    private void Add(Animatable target, DependencyProperty prop, AnimationTimeline anim)
    {
        Timeline.SetDesiredFrameRate(anim, 30);
        var clock = anim.CreateClock();
        target.ApplyAnimationClock(prop, clock);
        _clocks.Add(clock);
    }

    public void SetRunning(bool run)
    {
        _running = run;
        if (run && Children.Count == 0) Rebuild();
        foreach (var c in _clocks) { if (run) c.Controller!.Resume(); else c.Controller!.Pause(); }
    }
}
