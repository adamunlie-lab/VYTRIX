using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vytrix.Core;

namespace Vytrix.Controls;

/// <summary>Lays children out in equal columns; column count adapts to available width.</summary>
public sealed class ResponsiveGrid : Panel
{
    public double MinColumnWidth { get; set; } = 260;
    public int MaxColumns { get; set; } = 4;
    public double Gap { get; set; } = 14;

    private int Columns(double width) => Math.Clamp((int)((width + Gap) / (MinColumnWidth + Gap)), 1, MaxColumns);

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinColumnWidth * MaxColumns : available.Width;
        int cols = Columns(width);
        double cw = (width - Gap * (cols - 1)) / cols;
        double total = 0, rowH = 0;
        var kids = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        for (int i = 0; i < kids.Count; i++)
        {
            kids[i].Measure(new Size(cw, double.PositiveInfinity));
            rowH = Math.Max(rowH, kids[i].DesiredSize.Height);
            if (i % cols == cols - 1 || i == kids.Count - 1) { total += rowH + (i == kids.Count - 1 ? 0 : Gap); rowH = 0; }
        }
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double cw = (final.Width - Gap * (cols - 1)) / cols;
        var kids = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        for (int r = 0; r * cols < kids.Count; r++)
        {
            var row = kids.Skip(r * cols).Take(cols).ToList();
            double h = row.Max(k => k.DesiredSize.Height);
            for (int c = 0; c < row.Count; c++) row[c].Arrange(new Rect(c * (cw + Gap), y, cw, h));
            y += h + Gap;
        }
        return final;
    }
}

/// <summary>Base for every page: scrollable body, header, and lifecycle hooks.</summary>
public abstract class PageBase : UserControl
{
    public abstract string PageTitle { get; }
    public abstract string PageSubtitle { get; }
    protected readonly StackPanel Body = new() { Margin = new Thickness(32, 8, 32, 32) };
    protected readonly StackPanel HeaderActions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    protected readonly ScrollViewer Scroller;
    private bool _built;

    protected PageBase()
    {
        var root = new DockPanel { LastChildFill = true };
        var header = new Grid { Margin = new Thickness(32, 26, 32, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = PageTitle, Style = Ui.Style("H1") });
        titles.Children.Add(new TextBlock { Text = PageSubtitle, Style = Ui.Style("Dim"), Margin = new Thickness(0, 4, 0, 0) });
        header.Children.Add(titles);
        Grid.SetColumn(HeaderActions, 1);
        header.Children.Add(HeaderActions);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        Scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Body, Focusable = false };
        root.Children.Add(Scroller);
        Content = root;
    }

    /// <summary>Called the first time the page is shown.</summary>
    protected abstract void Build();
    /// <summary>Called every time the page becomes visible (refresh data here).</summary>
    public virtual void OnShown() { }
    public virtual void OnHidden() { }

    public void Show()
    {
        if (!_built) { Build(); _built = true; }
        OnShown();
    }

    protected MainWindow Shell => (MainWindow)Application.Current.MainWindow;
}

public static class Kit
{
    public static Border Card(UIElement content, string? title = null, string? subtitle = null, UIElement? action = null)
    {
        var sp = new StackPanel();
        if (title is not null)
        {
            var head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = title, Style = Ui.Style("H2") });
            if (subtitle is not null) t.Children.Add(new TextBlock { Text = subtitle, Style = Ui.Style("Muted"), Margin = new Thickness(0, 3, 0, 0) });
            head.Children.Add(t);
            if (action is not null) { System.Windows.Controls.Grid.SetColumn(action, 1); head.Children.Add(action); if (action is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Top; }
            sp.Children.Add(head);
        }
        sp.Children.Add(content);
        return new Border { Style = Ui.Style("Card"), Child = sp };
    }

    public static TextBlock Section(string text) => new() { Text = text.ToUpperInvariant(), Style = Ui.Style("Label"), Margin = new Thickness(2, 22, 0, 10) };

    public static ResponsiveGrid Grid(double minCol = 260, int maxCols = 4, double gap = 14) => new() { MinColumnWidth = minCol, MaxColumns = maxCols, Gap = gap };

    public static Grid KeyValue(string key, string value, string? tooltip = null)
    {
        var g = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = key, Style = Ui.Style("Dim"), FontSize = 12.5 });
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        if (tooltip is not null) v.ToolTip = tooltip;
        System.Windows.Controls.Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    public static Border EmptyState(string icon, string title, string message, UIElement? action = null)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 24) };
        var circle = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Ui.Res("Surface3Brush"), HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.IconPath(icon, 20) };
        sp.Children.Add(circle);
        sp.Children.Add(new TextBlock { Text = title, Style = Ui.Style("H3"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4) });
        sp.Children.Add(new TextBlock { Text = message, Style = Ui.Style("Muted"), TextAlignment = TextAlignment.Center, MaxWidth = 420 });
        if (action is not null) { if (action is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.Margin = new Thickness(0, 14, 0, 0); } sp.Children.Add(action); }
        return new Border { Child = sp };
    }

    public static Border Notice(string text, string kind = "info", UIElement? action = null)
    {
        var (icon, fg, bg) = kind switch
        {
            "warn" => ("IcoAlert", "WarnBrush", "WarnSoftBrush"),
            "error" => ("IcoAlert", "BadBrush", "BadSoftBrush"),
            "good" => ("IcoCheck", "GoodBrush", "GoodSoftBrush"),
            _ => ("IcoInfo", "AccentHoverBrush", "AccentSoftBrush"),
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ico = Ui.IconPath(icon, 16, fg);
        ico.VerticalAlignment = VerticalAlignment.Top;
        ico.Margin = new Thickness(0, 1, 10, 0);
        g.Children.Add(ico);
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetColumn(tb, 1);
        g.Children.Add(tb);
        if (action is not null) { System.Windows.Controls.Grid.SetColumn(action, 2); if (action is FrameworkElement fe) { fe.Margin = new Thickness(12, 0, 0, 0); fe.VerticalAlignment = VerticalAlignment.Center; } g.Children.Add(action); }
        return new Border { Background = Ui.Res(bg), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Child = g, Margin = new Thickness(0, 0, 0, 12) };
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && sp.Children.Count > 0) fe.Margin = new Thickness(8, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            sp.Children.Add(c);
        }
        return sp;
    }

    public static Border RiskBadge(Services.Risk r) => r switch
    {
        Services.Risk.Low => Ui.Badge("Low risk", "GoodBrush", "GoodSoftBrush"),
        Services.Risk.Moderate => Ui.Badge("Moderate", "WarnBrush", "WarnSoftBrush"),
        _ => Ui.Badge("Advanced", "BadBrush", "BadSoftBrush"),
    };

    public static Border StateBadge(Services.TweakState s) => s switch
    {
        Services.TweakState.Applied => Ui.Badge("Applied", "AccentHoverBrush", "AccentSoftBrush"),
        Services.TweakState.Partial => Ui.Badge("Partially applied", "WarnBrush", "WarnSoftBrush"),
        Services.TweakState.Unsupported => Ui.Badge("Not supported", "TextMutedBrush", "Surface3Brush"),
        // "Not applied" rather than "Windows default": the current value may be the user's own choice.
        _ => Ui.Badge("Not applied", "TextDimBrush", "Surface3Brush"),
    };

    /// <summary>
    /// Runs an operation with a busy state on the trigger button and reports the
    /// operation's own success flag. A failed operation is never shown as success.
    /// </summary>
    public static async Task<bool> RunAsync(Button? trigger, Func<Task<(bool Ok, string Message)>> op, bool toastOnSuccess = true)
    {
        object? original = trigger?.Content;
        if (trigger is not null)
        {
            trigger.IsEnabled = false;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Spinner { Width = 14, Height = 14, Stroke = trigger.Style == Ui.Style("Primary") ? Brushes.White : Ui.Res("AccentBrush") });
            sp.Children.Add(new TextBlock { Text = "Working…", Margin = new Thickness(8, 0, 0, 0), Foreground = trigger.Style == Ui.Style("Primary") ? Brushes.White : Ui.Res("TextBrush") });
            trigger.Content = sp;
        }
        try
        {
            var (ok, msg) = await op();
            var win = (MainWindow)Application.Current.MainWindow;
            if (!ok) win.Toast(msg, ToastKind.Error);
            else if (toastOnSuccess) win.Toast(msg, ToastKind.Success);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("ui", "Operation failed", ex);
            ((MainWindow)Application.Current.MainWindow).Toast($"Operation failed: {ex.Message}", ToastKind.Error);
            return false;
        }
        finally
        {
            if (trigger is not null) { trigger.Content = original; trigger.IsEnabled = true; }
        }
    }

    public static void FadeIn(UIElement e)
    {
        if (Settings.Current.ReduceMotion) return;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        if (e is FrameworkElement fe)
        {
            var tt = new TranslateTransform(0, 6);
            fe.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    public static CheckBox Switch(bool on, string? tooltip = null)
    {
        var cb = new CheckBox { Style = Ui.Style("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        if (tooltip is not null) cb.ToolTip = tooltip;
        return cb;
    }

    public static TextBox Search(string placeholder, Action<string> onChange, double width = 280)
    {
        var tb = new TextBox { Tag = placeholder, Width = width, VerticalAlignment = VerticalAlignment.Center };
        tb.TextChanged += (_, _) => onChange(tb.Text);
        return tb;
    }
}
