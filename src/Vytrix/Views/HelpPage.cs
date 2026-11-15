using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Vytrix.Controls;
using Vytrix.Services;

namespace Vytrix.Views;

/// <summary>The built-in user guide: quick start, topics with jump buttons, and a filter.</summary>
public sealed class HelpPage : PageBase
{
    public override string PageTitle => "Help";
    public override string PageSubtitle => "How to get the most out of VYTRIX, and how to undo anything.";

    private readonly Dictionary<string, Border> _cards = [];
    private readonly StackPanel _topics = new();
    private readonly TextBlock _noMatch = new() { Text = "No help topics match. Try another word, or press Ctrl+K to search the whole app.", Style = Ui.Style("Muted"), Visibility = Visibility.Collapsed, Margin = new Thickness(2, 8, 0, 0) };
    private FrameworkElement? _quickStart;
    private TextBlock? _quickLabel;

    protected override void Build()
    {
        HeaderActions.Children.Add(Kit.Search("Search help", Filter, 260));

        _quickLabel = Kit.Section("Quick start");
        Body.Children.Add(_quickLabel);
        var quick = Kit.Grid(240, 3);
        int n = 1;
        foreach (var (title, text, key, label) in HelpContent.QuickStart) quick.Children.Add(StepCard(n++, title, text, key, label));
        _quickStart = quick;
        Body.Children.Add(quick);

        Body.Children.Add(Kit.Section("Topics"));
        foreach (var t in HelpContent.Topics)
        {
            var card = TopicCard(t);
            _cards[t.Id] = card;
            _topics.Children.Add(card);
        }
        Body.Children.Add(_topics);
        Body.Children.Add(_noMatch);

        var terms = Ui.IconButton("IcoShield", "Read the Terms of Agreement", "Ghost");
        terms.Click += async (_, _) => await Shell.ShowTermsAsync(requireAcceptance: false);
        var footer = new StackPanel { Margin = new Thickness(2, 18, 0, 0) };
        footer.Children.Add(new TextBlock { Text = $"VYTRIX {Updater.Current.ToString(3)}", Style = Ui.Style("Muted"), Margin = new Thickness(0, 0, 0, 8) });
        footer.Children.Add(Kit.Row(terms));
        Body.Children.Add(footer);
    }

    /// <summary>Scrolls to a topic and briefly highlights it (used by global search).</summary>
    public void Focus(string topicId)
    {
        if (!_cards.TryGetValue(topicId, out var card)) return;
        if (card.Visibility != Visibility.Visible) Filter("");
        Dispatcher.BeginInvoke(() =>
        {
            card.BringIntoView(new Rect(0, 0, 1, 1));
            var prev = card.BorderBrush;
            card.BorderBrush = Ui.Res("AccentBrush");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            t.Tick += (_, _) => { t.Stop(); card.ClearValue(Border.BorderBrushProperty); };
            t.Start();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Filter(string q)
    {
        bool any = false;
        foreach (var t in HelpContent.Topics)
        {
            bool m = t.Matches(q);
            _cards[t.Id].Visibility = m ? Visibility.Visible : Visibility.Collapsed;
            any |= m;
        }
        bool searching = !string.IsNullOrWhiteSpace(q);
        if (_quickStart is not null) _quickStart.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        if (_quickLabel is not null) _quickLabel.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        _noMatch.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private Border StepCard(int number, string title, string text, string pageKey, string pageLabel)
    {
        var sp = new StackPanel();
        var badge = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Ui.Res("AccentSoftBrush"),
            Child = new TextBlock { Text = number.ToString(), FontFamily = (FontFamily)Application.Current.Resources["NumberFont"], FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("AccentHoverBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        sp.Children.Add(badge);
        sp.Children.Add(new TextBlock { Text = title, Style = Ui.Style("H3"), Margin = new Thickness(0, 12, 0, 4) });
        sp.Children.Add(new TextBlock { Text = text, Style = Ui.Style("Dim"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19 });
        var go = new Button { Content = pageLabel, Style = Ui.Style("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        go.Click += (_, _) => Shell.Navigate(pageKey);
        sp.Children.Add(go);
        return new Border { Style = Ui.Style("Card"), Child = sp };
    }

    private Border TopicCard(HelpTopic t)
    {
        var content = new StackPanel();
        foreach (var b in t.Blocks) content.Children.Add(Render(b));
        Button? open = null;
        if (t.PageKey is { } key)
        {
            open = new Button { Content = t.PageLabel ?? "Open", Style = Ui.Style("Ghost") };
            open.Click += (_, _) => Shell.Navigate(key);
        }

        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = Ui.Res("Surface3Brush"), Child = Ui.IconPath(t.Icon, 17, "AccentHoverBrush"), VerticalAlignment = VerticalAlignment.Top };
        head.Children.Add(icon);
        var titles = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = t.Title, Style = Ui.Style("H2") });
        titles.Children.Add(new TextBlock { Text = t.Summary, Style = Ui.Style("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        if (open is not null) { open.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(open, 2); head.Children.Add(open); }

        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(content);
        return new Border { Style = Ui.Style("Card"), Child = sp, Margin = new Thickness(0, 0, 0, 14) };
    }

    private static UIElement Render(HelpBlock b) => b switch
    {
        HelpPara p => Rich(p.Text, new Thickness(0, 0, 0, 10)),
        HelpSteps s => List(s.Items, numbered: true),
        HelpBullets l => List(l.Items, numbered: false),
        HelpTable t => Table(t),
        _ => new Border(),
    };

    /// <summary>A wrapped paragraph; text between ** markers is bold.</summary>
    private static TextBlock Rich(string text, Thickness margin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = margin };
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            tb.Inlines.Add(i % 2 == 1 ? new Run(parts[i]) { FontWeight = FontWeights.SemiBold } : new Run(parts[i]) { Foreground = Ui.Res("TextDimBrush") });
        }
        return tb;
    }

    private static StackPanel List(string[] items, bool numbered)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        for (int i = 0; i < items.Length; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock
            {
                Text = numbered ? $"{i + 1}." : "•", FontSize = 13, LineHeight = 20,
                Foreground = Ui.Res("AccentHoverBrush"), FontWeight = FontWeights.SemiBold,
            });
            var tb = Rich(items[i], new Thickness(0));
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            sp.Children.Add(row);
        }
        return sp;
    }

    private static Border Table(HelpTable t)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150, MaxWidth = 240 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        void Add(int r, string k, string v, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bg = new Border
            {
                Background = header ? Ui.Res("Surface3Brush") : (r % 2 == 0 ? Brushes.Transparent : Ui.Res("SurfaceBrush")),
                CornerRadius = header ? new CornerRadius(6) : new CornerRadius(0),
            };
            Grid.SetRow(bg, r); Grid.SetColumnSpan(bg, 2);
            grid.Children.Add(bg);
            var kt = header ? new TextBlock { Text = k.ToUpperInvariant(), Style = Ui.Style("Label") } : new TextBlock { Text = k, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            kt.Margin = new Thickness(12, 8, 12, 8);
            Grid.SetRow(kt, r);
            grid.Children.Add(kt);
            var vt = header ? new TextBlock { Text = v.ToUpperInvariant(), Style = Ui.Style("Label") } : Rich(v, new Thickness(0));
            vt.Margin = new Thickness(0, 8, 12, 8);
            if (!header) vt.FontSize = 12.5;
            Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
            grid.Children.Add(vt);
        }
        Add(0, t.KeyHeader, t.ValueHeader, header: true);
        for (int i = 0; i < t.Rows.Length; i++) Add(i + 1, t.Rows[i].Key, t.Rows[i].Value, header: false);
        return new Border { Child = grid, Margin = new Thickness(0, 0, 0, 10) };
    }
}
