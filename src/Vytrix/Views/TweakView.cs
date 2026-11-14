using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

/// <summary>
/// Reusable card for one tweak: state, risk, explanation, toggle and restore.
/// The toggle always reflects the real registry state after an operation.
/// </summary>
public sealed class TweakView : Border
{
    public Tweak Tweak { get; }
    private readonly CheckBox _toggle;
    private readonly ContentControl _stateBadge = new() { Focusable = false };
    private readonly StackPanel _details;
    private readonly TextBlock _expander;
    private bool _syncing;

    public TweakView(Tweak t, bool compact = false)
    {
        Tweak = t;
        Style = Ui.Style("Card");
        Padding = new Thickness(18, 16, 18, 16);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = t.Name, Style = Ui.Style("H3"), Margin = new Thickness(0, 0, 10, 4), VerticalAlignment = VerticalAlignment.Center });
        var risk = Kit.RiskBadge(t.Risk); risk.Margin = new Thickness(0, 0, 6, 4); title.Children.Add(risk);
        _stateBadge.Margin = new Thickness(0, 0, 6, 4); title.Children.Add(_stateBadge);
        if (t.RequiresAdmin) { var a = Ui.Badge("Admin", "TextDimBrush", "Surface3Brush"); a.Margin = new Thickness(0, 0, 6, 4); a.ToolTip = "Changes a machine-wide setting; requires administrator mode."; title.Children.Add(a); }
        if (t.AppliesAfter is not null) { var r = Ui.Badge(t.AppliesAfter, "TextDimBrush", "Surface3Brush"); r.Margin = new Thickness(0, 0, 6, 4); r.ToolTip = "When the change takes effect."; title.Children.Add(r); }
        left.Children.Add(title);
        left.Children.Add(new TextBlock { Text = t.Description, Style = Ui.Style("Dim"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) });
        if (!compact)
        {
            var eff = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };
            eff.Inlines.Add(new System.Windows.Documents.Run("Effect  ") { Foreground = Ui.Res("TextMutedBrush"), FontWeight = FontWeights.SemiBold });
            eff.Inlines.Add(new System.Windows.Documents.Run(t.ExpectedEffect));
            left.Children.Add(eff);
        }

        _expander = new TextBlock { Text = "Show details", Foreground = Ui.Res("AccentHoverBrush"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
        _expander.MouseLeftButtonUp += (_, _) => ToggleDetails();
        left.Children.Add(_expander);

        _details = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        _details.Children.Add(new Border
        {
            Style = Ui.Style("Inset"),
            Child = BuildDetails(t),
        });
        left.Children.Add(_details);
        g.Children.Add(left);

        _toggle = Kit.Switch(false);
        _toggle.Margin = new Thickness(18, 2, 0, 0);
        _toggle.VerticalAlignment = VerticalAlignment.Top;
        _toggle.Checked += async (_, _) => await OnToggle(true);
        _toggle.Unchecked += async (_, _) => await OnToggle(false);
        Grid.SetColumn(_toggle, 1);
        g.Children.Add(_toggle);
        Child = g;
        Sync();
        TweakEngine.TweakChanged += x => { if (x.Id == t.Id) Dispatcher.BeginInvoke(Sync); };
    }

    private static StackPanel BuildDetails(Tweak t)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = t.Explanation, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19 });
        sp.Children.Add(Spacer());
        sp.Children.Add(Kit.KeyValue("Component", t.ComponentLabel));
        sp.Children.Add(Kit.KeyValue("Category", t.CategoryLabel));
        sp.Children.Add(Kit.KeyValue("Risk", t.RiskLabel));
        sp.Children.Add(Kit.KeyValue("Compatibility", t.Compatibility));
        sp.Children.Add(Kit.KeyValue("Takes effect", t.AppliesAfter ?? "Immediately"));
        sp.Children.Add(Kit.KeyValue("Requires admin", t.RequiresAdmin ? "Yes" : "No"));
        sp.Children.Add(Kit.KeyValue("Restore", "The exact previous value is recorded before any change and restored when you turn this off. Registry keys are also exported to a .reg backup."));
        sp.Children.Add(Spacer());
        sp.Children.Add(new TextBlock { Text = "CHANGES", Style = Ui.Style("Label"), Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(new TextBox { Text = t.ChangesSummary, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Background = Ui.Res("SurfaceBrush") });
        return sp;
    }

    private static FrameworkElement Spacer() => new Border { Height = 10 };

    private void ToggleDetails()
    {
        bool show = _details.Visibility != Visibility.Visible;
        _details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _expander.Text = show ? "Hide details" : "Show details";
        if (show) Kit.FadeIn(_details);
    }

    public void ExpandDetails() { if (_details.Visibility != Visibility.Visible) ToggleDetails(); }

    public void Sync()
    {
        TweakState s;
        try { s = Tweak.GetState(); } catch { s = TweakState.NotApplied; }
        _syncing = true;
        _toggle.IsChecked = s == TweakState.Applied;
        _toggle.IsEnabled = s != TweakState.Unsupported;
        _toggle.ToolTip = s == TweakState.Unsupported ? Tweak.UnsupportedReason : (Tweak.RequiresAdmin && !Elevation.IsAdmin ? "Requires administrator mode" : null);
        _syncing = false;
        _stateBadge.Content = Kit.StateBadge(s);
        if (s == TweakState.Unsupported) _stateBadge.ToolTip = Tweak.UnsupportedReason;
    }

    private async Task OnToggle(bool on)
    {
        if (_syncing) return;
        var win = (MainWindow)Application.Current.MainWindow;
        if (Tweak.RequiresAdmin && !Elevation.IsAdmin)
        {
            win.Toast($"\"{Tweak.Name}\" needs administrator mode. Use \"Relaunch as administrator\" in the sidebar.", ToastKind.Warning);
            Sync();
            return;
        }
        if (on && (Settings.Current.ConfirmBeforeApplying || Tweak.Risk != Risk.Low))
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = Tweak.ExpectedEffect, Style = Ui.Style("Dim"), Margin = new Thickness(0, 0, 0, 10) });
            if (Tweak.Risk != Risk.Low) body.Children.Add(Kit.Notice($"{Tweak.RiskLabel}: {Tweak.Explanation}", "warn"));
            body.Children.Add(new TextBlock { Text = "VYTRIX will change:", Style = Ui.Style("Label"), Margin = new Thickness(0, 0, 0, 4) });
            body.Children.Add(new TextBlock { Text = Tweak.ChangesSummary, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = "The previous value is saved so you can turn this off at any time.", Style = Ui.Style("Muted"), Margin = new Thickness(0, 10, 0, 0) });
            if (!await win.ConfirmAsync($"Apply \"{Tweak.Name}\"?", body, "Apply", danger: Tweak.Risk != Risk.Low)) { Sync(); return; }
        }
        _toggle.IsEnabled = false;
        await Kit.RunAsync(null, () => on ? TweakEngine.ApplyAsync(Tweak) : TweakEngine.RevertAsync(Tweak));
        Sync();
    }
}
