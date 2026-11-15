using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Vytrix.Controls;

/// <summary>
/// A string of Christmas lights around the edge of the window (Christmas theme). The wire
/// follows the window's outline in shallow loops, with bulbs pointing inwards, and is redrawn
/// whenever the window changes size.
/// <para>
/// Cost: the lights are four thin strips (one per edge), so a twinkle only repaints those
/// strips rather than the whole window. The twinkle itself is a few shared brush animations,
/// stopped whenever the window isn't active or Reduce motion is on (the bulbs then glow steadily).
/// </para>
/// </summary>
public sealed class ChristmasLights : Grid
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x3B, 0x3B),   // red
        Color.FromRgb(0x3D, 0xD9, 0x6B),   // green
        Color.FromRgb(0xFF, 0xC8, 0x3A),   // gold
        Color.FromRgb(0x4A, 0x9B, 0xFF),   // blue
        Color.FromRgb(0xFF, 0x8A, 0xD8),   // pink
    ];
    private const int Phases = 2;
    private const double Beat = 0.25;     // seconds per twinkle step
    /// <summary>One twinkle: brightness per beat, a slow dim and brighten with a pause at full.</summary>
    private static readonly double[] Twinkle = [1, 1, 0.85, 0.65, 0.45, 0.35, 0.45, 0.65, 0.85, 1, 1, 1];
    private const double Spacing = 36;      // between bulbs along the wire
    private const double Loop = 108;        // between the points where the wire is pinned
    private const double Edge = 3;          // wire distance from the window edge at the pins
    private const double StripDepth = 32;   // how far the wire, bulbs and glow reach in from the edge

    // [colour, phase] → the bulb body brush and its glow; their opacity is what twinkles.
    private readonly SolidColorBrush[,] _bulb = new SolidColorBrush[Palette.Length, Phases];
    private readonly RadialGradientBrush[,] _glow = new RadialGradientBrush[Palette.Length, Phases];
    private readonly List<AnimationClock> _clocks = [];
    private readonly Strip[] _strips;
    private static readonly Pen Wire = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x3B, 0x26)), 1.6) { LineJoin = PenLineJoin.Round });
    private static readonly Brush Cap = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x33)));
    private static readonly Brush Highlight = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private bool _running = true;

    public ChristmasLights()
    {
        IsHitTestVisible = false;
        for (int c = 0; c < Palette.Length; c++)
            for (int p = 0; p < Phases; p++)
            {
                var col = Palette[c];
                _bulb[c, p] = new SolidColorBrush(col);
                _glow[c, p] = new RadialGradientBrush(Color.FromArgb(0x90, col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B));
                // Each colour/phase pair fades a little out of step with the others, so the string shimmers.
                // One clock drives both the bulb and its glow. The brightness moves in small steps on a shared
                // quarter-second beat, so every group changes at the same moments and the strips repaint only
                // about four times a second, however many groups there are.
                var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(Beat * Twinkle.Length), RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-Beat * (c * 3 + p * 5)) };
                for (int s = 0; s < Twinkle.Length; s++) anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(Twinkle[s], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Beat * s))));
                Timeline.SetDesiredFrameRate(anim, 8);
                var clock = anim.CreateClock();
                _bulb[c, p].ApplyAnimationClock(Brush.OpacityProperty, clock);
                _glow[c, p].ApplyAnimationClock(Brush.OpacityProperty, clock);
                _clocks.Add(clock);
            }
        SetRunning(false);

        _strips = [new(this, 0), new(this, 1), new(this, 2), new(this, 3)];
        foreach (var s in _strips) Children.Add(s);
        SizeChanged += (_, _) => { foreach (var s in _strips) s.InvalidateVisual(); };
    }

    /// <summary>Twinkles while running; when stopped the bulbs glow steadily at full brightness.</summary>
    public void SetRunning(bool run)
    {
        if (run == _running) return;
        _running = run;
        foreach (var c in _clocks) { if (run) c.Controller!.Begin(); else c.Controller!.Stop(); }
    }

    /// <summary>One edge of the window: 0 top, 1 right, 2 bottom, 3 left.</summary>
    private sealed class Strip : FrameworkElement
    {
        private readonly ChristmasLights _owner;
        private readonly int _edge;

        public Strip(ChristmasLights owner, int edge)
        {
            _owner = owner;
            _edge = edge;
            IsHitTestVisible = false;
            bool horizontal = edge is 0 or 2;
            if (horizontal) { Height = StripDepth; VerticalAlignment = edge == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom; }
            else { Width = StripDepth; HorizontalAlignment = edge == 1 ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = _owner.ActualWidth, h = _owner.ActualHeight;
            if (w < 200 || h < 200) return;

            // This edge in the window's coordinates, with its inward direction.
            var (a, b, inward, sag) = _edge switch
            {
                0 => (new Point(Edge, Edge), new Point(w - Edge, Edge), new Vector(0, 1), 7.0),   // shallower: it passes over the title bar
                1 => (new Point(w - Edge, Edge), new Point(w - Edge, h - Edge), new Vector(-1, 0), 9.0),
                2 => (new Point(w - Edge, h - Edge), new Point(Edge, h - Edge), new Vector(0, -1), 9.0),
                _ => (new Point(Edge, h - Edge), new Point(Edge, Edge), new Vector(1, 0), 9.0),
            };
            // Draw in window coordinates, shifted to where this strip sits.
            var origin = _edge switch { 1 => new Vector(w - StripDepth, 0), 2 => new Vector(0, h - StripDepth), _ => new Vector(0, 0) };
            dc.PushTransform(new TranslateTransform(-origin.X, -origin.Y));

            var along = b - a;
            double len = along.Length;
            var dir = along / len;
            int loops = Math.Max(1, (int)Math.Round(len / Loop));
            double loopLen = len / loops;

            var wire = new StreamGeometry();
            using (var g = wire.Open())
            {
                g.BeginFigure(a, false, false);
                for (int i = 0; i < loops; i++)
                {
                    var p1 = a + dir * ((i + 1) * loopLen);
                    var control = a + dir * ((i + 0.5) * loopLen) + inward * sag * 2;   // quadratic control point → the wire sags by `sag` mid-loop
                    g.QuadraticBezierTo(control, p1, true, true);
                }
            }
            wire.Freeze();
            dc.DrawGeometry(null, Wire, wire);

            // Bulbs hang from the wire, spaced evenly, keeping clear of the corners.
            int count = (int)((len - 24) / Spacing);
            double start = (len - (count - 1) * Spacing) / 2;
            double angle = Math.Atan2(inward.Y, inward.X) * 180 / Math.PI - 90;
            for (int k = 0; k < count; k++)
            {
                double d = start + k * Spacing;
                double t = d % loopLen / loopLen;                 // where in its loop this bulb is
                var at = a + dir * d + inward * (sag * 4 * t * (1 - t));   // on the curve
                int i = k + _edge * 7;                            // shift the colour pattern a little per edge
                int c = i % Palette.Length, p = (i / Palette.Length) % Phases;
                dc.DrawEllipse(_owner._glow[c, p], null, at + inward * 9, 11, 11);
                dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
                dc.DrawRoundedRectangle(Cap, null, new Rect(at.X - 2.2, at.Y, 4.4, 3.4), 1, 1);   // socket
                dc.DrawEllipse(_owner._bulb[c, p], null, new Point(at.X, at.Y + 7.4), 3.1, 4.6);
                dc.DrawEllipse(Highlight, null, new Point(at.X - 1, at.Y + 6), 0.9, 1.6);
                dc.Pop();
            }
            dc.Pop();
        }
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
