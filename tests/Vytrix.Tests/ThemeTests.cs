using System.Windows;
using System.Windows.Media;
using Vytrix.Controls;
using Xunit;

namespace Vytrix.Tests;

public class ThemeTests
{
    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
        return result;
    }

    [Fact]
    public void Cherry_tree_fits_inside_its_canvas()
    {
        // Regression: the crown used to extend past the canvas and was cut off with a flat edge.
        var bounds = OnSta(() =>
        {
            var tree = new SakuraTree();
            tree.Measure(new Size(SakuraTree.DesignWidth, SakuraTree.DesignHeight));
            tree.Arrange(new Rect(0, 0, SakuraTree.DesignWidth, SakuraTree.DesignHeight));
            return VisualTreeHelper.GetDescendantBounds(tree);
        });
        Assert.True(bounds.Left >= -4 && bounds.Right <= SakuraTree.DesignWidth + 4, $"Horizontal bounds {bounds.Left:0}..{bounds.Right:0}");
        Assert.True(bounds.Top >= -4, $"Top {bounds.Top:0}");
    }

    [Fact]
    public void Cherry_tree_is_deterministic()
    {
        // Same seed, same tree on every launch (no random reshuffling between runs).
        var (a, b) = OnSta(() =>
        {
            Rect Bounds()
            {
                var t = new SakuraTree();
                t.Measure(new Size(SakuraTree.DesignWidth, SakuraTree.DesignHeight));
                t.Arrange(new Rect(0, 0, SakuraTree.DesignWidth, SakuraTree.DesignHeight));
                return VisualTreeHelper.GetDescendantBounds(t);
            }
            return (Bounds(), Bounds());
        });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Theme_names_are_stable()
    {
        Assert.Equal(["Christmas", "Cherry Blossom", "Graphite"], ThemeManager.Themes);
        Assert.Contains(ThemeManager.Accents, x => x.Name == "Sakura");
        Assert.Contains(ThemeManager.Accents, x => x.Name == "Crimson");
        Assert.Equal("Christmas", new Core.AppSettings().Theme);
        foreach (var t in ThemeManager.Themes) Assert.Contains(ThemeManager.Accents, a => a.Name == ThemeManager.DefaultAccent(t));
    }

    [Fact]
    public void Christmas_tree_fits_inside_its_canvas()
    {
        var bounds = OnSta(() =>
        {
            var tree = new ChristmasTree();
            tree.Measure(new Size(ChristmasTree.DesignWidth, ChristmasTree.DesignHeight));
            tree.Arrange(new Rect(0, 0, ChristmasTree.DesignWidth, ChristmasTree.DesignHeight));
            return VisualTreeHelper.GetDescendantBounds(tree);
        });
        Assert.True(bounds.Left >= -4 && bounds.Right <= ChristmasTree.DesignWidth + 4, $"Horizontal bounds {bounds.Left:0}..{bounds.Right:0}");
        Assert.True(bounds.Top >= -4 && bounds.Bottom <= ChristmasTree.DesignHeight + 4, $"Vertical bounds {bounds.Top:0}..{bounds.Bottom:0}");
    }
}
