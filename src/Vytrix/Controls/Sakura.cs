using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vytrix.Controls;

/// <summary>
/// A procedurally generated cherry blossom tree whose limbs sway in the wind.
/// Each of the first three branch levels is its own layer rotating around its
/// base, nested inside its parent, so small branches ride on the larger ones.
/// Leaf-level layers are bitmap-cached, so a frame only re-composites cached
/// textures: the animation is cheap on CPU and GPU.
/// </summary>
public sealed class SakuraTree : Canvas
{
    public const double DesignWidth = 780, DesignHeight = 600;   // wide enough for the full crown
    private const int MaxDepth = 7, AnimatedDepth = 3;

    private sealed class Branch
    {
        public Point Start, Control, End;
        public double Angle, Length, Width;
        public int Depth;
        public List<Branch> Kids = [];
    }

    private readonly List<AnimationClock> _clocks = [];
    private readonly Random _rng;
    private bool _running;

    private static readonly Color[] Blossom =
    [
        Color.FromRgb(0xFF, 0xE4, 0xF1), Color.FromRgb(0xFB, 0xCF, 0xE8), Color.FromRgb(0xF9, 0xA8, 0xD4),
        Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xFF, 0xF5, 0xFA), Color.FromRgb(0xF0, 0x8C, 0xC4),
    ];

    public SakuraTree(int seed = 20261114)
    {
        _rng = new Random(seed);   // fixed seed: the same beautiful tree every launch
        Width = DesignWidth;
        Height = DesignHeight;
        Children.Add(new DrawingHost(BuildBackdrop()));

        var trunk = Grow(new Point(DesignWidth * 0.50, DesignHeight - 14), -0.06, 128, 40, 0);
        Children.Add(BuildLayer(trunk));
    }

    // ───────────────────────── generation

    private Branch Grow(Point start, double angle, double length, double width, int depth)
    {
        var end = new Point(start.X + Math.Sin(angle) * length, start.Y - Math.Cos(angle) * length);
        // Bend each branch slightly so limbs look organic rather than straight.
        var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        double bend = (_rng.NextDouble() - 0.5) * length * 0.28;
        var ctrl = new Point(mid.X + Math.Cos(angle) * bend, mid.Y + Math.Sin(angle) * bend);
        var b = new Branch { Start = start, Control = ctrl, End = end, Angle = angle, Length = length, Width = width, Depth = depth };
        if (depth >= MaxDepth) return b;
        // Natural irregularity: some outer twigs stop early.
        if (depth >= 4 && _rng.NextDouble() < 0.14) return b;

        int n = depth == 0 ? 3 : depth < 3 ? (_rng.NextDouble() < 0.55 ? 3 : 2) : (_rng.NextDouble() < 0.22 ? 3 : 2);
        double spread = depth == 0 ? 0.78 : depth < 3 ? 0.55 : 0.44;
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : i / (double)(n - 1) - 0.5;
            double a = angle + spread * 2 * t + (_rng.NextDouble() - 0.5) * 0.36;
            // Cherry crowns are broad and rounded: outer limbs lean out, inner ones keep rising.
            if (depth is >= 1 and <= 3) a *= 1.06;
            a = Math.Clamp(a, -1.45, 1.45);
            double len = length * (depth == 0 ? 0.80 : 0.66 + _rng.NextDouble() * 0.16);
            b.Kids.Add(Grow(end, a, len, Math.Max(1.1, width * (depth == 0 ? 0.55 : 0.62)), depth + 1));
        }
        return b;
    }

    // ───────────────────────── rendering

    private FrameworkElement BuildLayer(Branch b)
    {
        var layer = new Canvas { Width = DesignWidth, Height = DesignHeight };
        var drawing = new DrawingGroup();
        var animatedKids = new List<Branch>();
        using (var dc = drawing.Open())
        {
            DrawStatic(dc, b, animatedKids, isRoot: true);
        }
        drawing.Freeze();
        layer.Children.Add(new DrawingHost(drawing));
        foreach (var k in animatedKids) layer.Children.Add(BuildLayer(k));

        if (b.Depth >= 1)
        {
            // Sway around the branch base; thinner branches move further and faster.
            double amp = b.Depth switch { 1 => 0.9, 2 => 1.7, _ => 2.8 } * (0.8 + _rng.NextDouble() * 0.4);
            double seconds = b.Depth switch { 1 => 6.5, 2 => 5.0, _ => 3.8 } * (0.85 + _rng.NextDouble() * 0.3);
            var rot = new RotateTransform(0, b.Start.X, b.Start.Y);
            layer.RenderTransform = rot;
            var anim = new DoubleAnimation(-amp, amp, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                BeginTime = TimeSpan.FromSeconds(-_rng.NextDouble() * seconds * 2),   // random phase
            };
            Timeline.SetDesiredFrameRate(anim, 30);
            var clock = anim.CreateClock();
            clock.Controller!.Pause();
            rot.ApplyAnimationClock(RotateTransform.AngleProperty, clock);
            _clocks.Add(clock);
            if (b.Depth == AnimatedDepth) layer.CacheMode = new BitmapCache(1.0) { SnapsToDevicePixels = false };
        }
        return layer;
    }

    /// <summary>Draws a branch and every descendant that is not its own animated layer.</summary>
    private void DrawStatic(DrawingContext dc, Branch b, List<Branch> animatedKids, bool isRoot)
    {
        DrawBranch(dc, b);
        foreach (var k in b.Kids)
        {
            if (k.Depth <= AnimatedDepth) animatedKids.Add(k);
            else DrawStatic(dc, k, animatedKids, false);
        }
        if (b.Depth >= 4 || (b.Depth == 3 && b.Kids.Count == 0)) DrawBlossoms(dc, b);
    }

    private static Brush BarkBrush(int depth)
    {
        // Dark plum-brown trunk, lightening toward the twigs.
        double t = Math.Min(1, depth / 6.0);
        byte r = (byte)(0x2C + (0x6B - 0x2C) * t), g = (byte)(0x18 + (0x45 - 0x18) * t), bl = (byte)(0x24 + (0x58 - 0x24) * t);
        var br = new SolidColorBrush(Color.FromRgb(r, g, bl));
        br.Freeze();
        return br;
    }

    private static void DrawBranch(DrawingContext dc, Branch b)
    {
        if (b.Depth == 0) { DrawTrunk(dc, b); return; }
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(b.Start, false, false);
            c.QuadraticBezierTo(b.Control, b.End, true, true);
        }
        geo.Freeze();
        var pen = new Pen(BarkBrush(b.Depth), b.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
        // A thin highlight along thick limbs gives the bark some depth.
        if (b.Width > 7)
        {
            var hi = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xF9, 0xA8, 0xD4)), b.Width * 0.18) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            hi.Freeze();
            var offset = new TranslateTransform(-b.Width * 0.22, 0);
            dc.PushTransform(offset);
            dc.DrawGeometry(null, hi, geo);
            dc.Pop();
        }
    }

    /// <summary>A tapered trunk with a root flare and bark shading, instead of a uniform stroke.</summary>
    private static void DrawTrunk(DrawingContext dc, Branch b)
    {
        double w0 = b.Width, w1 = b.Width * 0.55;
        double nx = Math.Cos(b.Angle), ny = Math.Sin(b.Angle);   // perpendicular to the trunk direction
        Point L(Point p, double w) => new(p.X - nx * w / 2, p.Y - ny * w / 2);
        Point R(Point p, double w) => new(p.X + nx * w / 2, p.Y + ny * w / 2);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            // Root flare at the base, then taper up both sides along the curve.
            c.BeginFigure(new Point(b.Start.X - w0 * 1.15, b.Start.Y + 6), true, true);
            c.QuadraticBezierTo(L(b.Start, w0 * 0.95), L(new Point((b.Start.X + b.Control.X) / 2, (b.Start.Y + b.Control.Y) / 2), w0 * 0.85), true, true);
            c.QuadraticBezierTo(L(b.Control, (w0 + w1) / 2), L(b.End, w1), true, true);
            c.LineTo(R(b.End, w1), true, true);
            c.QuadraticBezierTo(R(b.Control, (w0 + w1) / 2), R(new Point((b.Start.X + b.Control.X) / 2, (b.Start.Y + b.Control.Y) / 2), w0 * 0.85), true, true);
            c.QuadraticBezierTo(R(b.Start, w0 * 0.95), new Point(b.Start.X + w0 * 1.2, b.Start.Y + 6), true, true);
        }
        geo.Freeze();
        var bark = new LinearGradientBrush(Color.FromRgb(0x2A, 0x16, 0x22), Color.FromRgb(0x4E, 0x30, 0x40), 0);
        bark.GradientStops.Add(new GradientStop(Color.FromRgb(0x24, 0x13, 0x1D), 1));
        bark.Freeze();
        dc.DrawGeometry(bark, null, geo);
        // Bark grain: a few faint vertical lines.
        var grain = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0x12, 0x08, 0x0E)), 1.4);
        grain.Freeze();
        for (int i = -1; i <= 1; i++)
        {
            var s = new Point(b.Start.X + i * w0 * 0.22, b.Start.Y - 4);
            var e = new Point(b.End.X + i * w1 * 0.25, b.End.Y + 6);
            dc.DrawLine(grain, s, e);
        }
    }

    private void DrawBlossoms(DrawingContext dc, Branch b)
    {
        // Clusters at the tip and along the outer part of each small branch.
        int clusters = b.Depth >= 6 ? 3 : 2;
        for (int c = 0; c < clusters; c++)
        {
            double t = c == 0 ? 1.0 : 0.45 + _rng.NextDouble() * 0.45;
            var p = PointOnBranch(b, t);
            int flowers = b.Depth >= 6 ? 5 : 4;
            double spreadR = 6 + b.Depth * 1.6;
            for (int i = 0; i < flowers; i++)
            {
                var at = new Point(p.X + (_rng.NextDouble() - 0.5) * spreadR * 2, p.Y + (_rng.NextDouble() - 0.5) * spreadR * 1.6);
                DrawFlower(dc, at, 2.6 + _rng.NextDouble() * 3.4);
            }
        }
    }

    private void DrawFlower(DrawingContext dc, Point at, double r)
    {
        var col = Blossom[_rng.Next(Blossom.Length)];
        byte alpha = (byte)(0xC8 + _rng.Next(0x37));
        var fill = new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B));
        fill.Freeze();
        if (r < 3.6)
        {
            dc.DrawEllipse(fill, null, at, r, r);   // distant blossoms: a soft dot
            return;
        }
        // Five-petal flower with a darker centre, rotated randomly.
        double rot = _rng.NextDouble() * Math.PI * 2;
        for (int k = 0; k < 5; k++)
        {
            double a = rot + k * Math.PI * 2 / 5;
            dc.DrawEllipse(fill, null, new Point(at.X + Math.Cos(a) * r * 0.62, at.Y + Math.Sin(a) * r * 0.62), r * 0.52, r * 0.52);
        }
        var centre = new SolidColorBrush(Color.FromArgb(0xE0, 0xBE, 0x18, 0x5D));
        centre.Freeze();
        dc.DrawEllipse(centre, null, at, r * 0.2, r * 0.2);
    }

    private static Point PointOnBranch(Branch b, double t)
    {
        double u = 1 - t;
        return new Point(u * u * b.Start.X + 2 * u * t * b.Control.X + t * t * b.End.X, u * u * b.Start.Y + 2 * u * t * b.Control.Y + t * t * b.End.Y);
    }

    private static Drawing BuildBackdrop()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // Soft pink glow behind the crown and a pale moon.
            var glow = new RadialGradientBrush(Color.FromArgb(0x38, 0xF4, 0x72, 0xB6), Color.FromArgb(0x00, 0xF4, 0x72, 0xB6));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(DesignWidth * 0.5, DesignHeight * 0.40), 340, 230);
            var moon = new RadialGradientBrush(Color.FromArgb(0x55, 0xFF, 0xF1, 0xF7), Color.FromArgb(0x00, 0xFF, 0xF1, 0xF7)) { GradientOrigin = new Point(0.5, 0.5) };
            moon.GradientStops.Insert(1, new GradientStop(Color.FromArgb(0x40, 0xFF, 0xF1, 0xF7), 0.35));
            moon.Freeze();
            dc.DrawEllipse(moon, null, new Point(DesignWidth * 0.22, DesignHeight * 0.20), 70, 70);
            // Ground shadow under the trunk.
            var ground = new RadialGradientBrush(Color.FromArgb(0x50, 0x0A, 0x05, 0x09), Color.FromArgb(0x00, 0x0A, 0x05, 0x09));
            ground.Freeze();
            dc.DrawEllipse(ground, null, new Point(DesignWidth * 0.6, DesignHeight - 12), 230, 26);
        }
        g.Freeze();
        return g;
    }

    // ───────────────────────── control

    /// <summary>Starts or pauses the wind. Paused clocks hold their current pose (no jump).</summary>
    public void SetRunning(bool run)
    {
        if (run == _running) return;
        _running = run;
        foreach (var c in _clocks) { if (run) c.Controller!.Resume(); else c.Controller!.Pause(); }
    }

    private sealed class DrawingHost : FrameworkElement
    {
        private readonly Drawing _d;
        public DrawingHost(Drawing d) { _d = d; IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(_d);
    }
}

/// <summary>Cherry blossom petals drifting down across the window. Hit-test invisible.</summary>
public sealed class PetalField : Canvas
{
    private static readonly Geometry PetalShape = CreatePetal();
    private readonly List<AnimationClock> _clocks = [];
    private readonly Random _rng = new(7);
    private bool _running;
    private Size _builtFor;

    private static readonly Color[] Colors =
    [
        Color.FromRgb(0xFB, 0xCF, 0xE8), Color.FromRgb(0xF9, 0xA8, 0xD4), Color.FromRgb(0xFF, 0xE4, 0xF1), Color.FromRgb(0xF4, 0x72, 0xB6),
    ];

    public PetalField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        SizeChanged += (_, e) =>
        {
            // Rebuild only after meaningful resizes, so dragging the window isn't expensive.
            if (Math.Abs(e.NewSize.Width - _builtFor.Width) > 120 || Math.Abs(e.NewSize.Height - _builtFor.Height) > 120) Rebuild();
        };
    }

    private static Geometry CreatePetal()
    {
        // A cherry petal: rounded with the characteristic notch at the tip.
        var g = Geometry.Parse("M0,9 C-5,6 -7,1 -4,-4 C-2.5,-6.5 -1,-6 0,-3.5 C1,-6 2.5,-6.5 4,-4 C7,1 5,6 0,9 Z");
        g.Freeze();
        return g;
    }

    private void Rebuild()
    {
        foreach (var c in _clocks) c.Controller!.Stop();
        _clocks.Clear();
        Children.Clear();
        _builtFor = new Size(ActualWidth, ActualHeight);
        if (ActualWidth < 50 || ActualHeight < 50) return;

        int count = (int)Math.Clamp(ActualWidth / 85, 8, 20);
        for (int i = 0; i < count; i++)
        {
            var col = Colors[_rng.Next(Colors.Length)];
            var fill = new SolidColorBrush(Color.FromArgb((byte)(0x99 + _rng.Next(0x50)), col.R, col.G, col.B));
            fill.Freeze();
            double scale = 0.7 + _rng.NextDouble() * 0.8;
            var flip = new ScaleTransform(1, 1);
            var rot = new RotateTransform(_rng.Next(360));
            var move = new TranslateTransform();
            var tg = new TransformGroup { Children = { new ScaleTransform(scale, scale), flip, rot, move } };
            Children.Add(new System.Windows.Shapes.Path { Data = PetalShape, Fill = fill, RenderTransform = tg });

            double fall = 11 + _rng.NextDouble() * 9;               // seconds to cross the window
            double startX = _rng.NextDouble() * (ActualWidth + 200) - 150;
            double drift = 80 + _rng.NextDouble() * 160;            // gentle wind from the left
            double phase = -_rng.NextDouble() * fall;

            Add(move, TranslateTransform.YProperty, new DoubleAnimation(-30, ActualHeight + 30, TimeSpan.FromSeconds(fall)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(phase) });
            Add(move, TranslateTransform.XProperty, new DoubleAnimation(startX, startX + drift, TimeSpan.FromSeconds(fall)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(phase) });
            Add(rot, RotateTransform.AngleProperty, new DoubleAnimation(rot.Angle, rot.Angle + (_rng.NextDouble() < 0.5 ? 360 : -360), TimeSpan.FromSeconds(6 + _rng.NextDouble() * 6)) { RepeatBehavior = RepeatBehavior.Forever });
            // Tumbling: squash horizontally as the petal turns edge-on.
            Add(flip, ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(1.2 + _rng.NextDouble() * 1.6))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
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
