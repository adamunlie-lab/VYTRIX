using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

/// <summary>Chat with Claude about this PC. Suggestions arrive as buttons; nothing changes without a click.</summary>
public sealed class AssistantPage : PageBase
{
    public override string PageTitle => "Assistant";
    public override string PageSubtitle => "Ask Claude about your PC. It can suggest changes; you decide.";

    private static readonly string[] Starters =
    [
        "Why does my PC feel slow?",
        "What are the best settings for gaming on my PC?",
        "Which tweaks are safe for me to turn on?",
        "What has VYTRIX changed so far?",
    ];

    private readonly Grid _host = new();
    private readonly StackPanel _chat = new() { Margin = new Thickness(32, 4, 32, 12) };
    private readonly ScrollViewer _chatScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, MinHeight = 44, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Tag = "Ask about your PC, games, startup, network or any tweak   (Enter to send, Shift+Enter for a new line)", Padding = new Thickness(12, 11, 12, 11) };
    private readonly Button _send = new() { Content = "Send", Margin = new Thickness(10, 0, 0, 0), MinWidth = 86 };
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(36, 0, 32, 6), Visibility = Visibility.Collapsed };
    private CheckBox? _share;
    private AssistantSession? _session;
    private CancellationTokenSource? _cts;
    private bool _busy;

    protected override void Build()
    {
        // The chat needs its input pinned to the bottom, so swap the page's scroller for our own layout.
        if (Content is DockPanel root) { root.Children.Remove(Scroller); root.Children.Add(_host); }
        _send.Style = Ui.Style("Primary");
        _send.Click += async (_, _) => await SendOrStopAsync();
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await SendOrStopAsync(); }
        };
        _status.Foreground = Ui.Res("TextMutedBrush");
        Render();
    }

    public override void OnShown()
    {
        if (_session is not null && !_busy) _input.Focus();
    }

    private void Render()
    {
        HeaderActions.Children.Clear();
        _host.Children.Clear();
        _host.RowDefinitions.Clear();
        var key = AssistantKey.Load();
        if (key is null) { BuildSetup(); return; }
        _session ??= new AssistantSession(key);
        BuildChat();
    }

    // ───────────────────────── Setup (no key yet)

    private void BuildSetup()
    {
        var body = new StackPanel { Margin = new Thickness(32, 8, 32, 32), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

        var intro = new StackPanel();
        intro.Children.Add(Para("The Assistant uses **Claude**, made by Anthropic, to answer questions about your PC in plain language. It can look at your PC's readings and tweaks, explain what would help, and offer buttons for the changes it recommends."));
        intro.Children.Add(Para("It **can't change anything by itself**. Every suggestion is a button you choose to click, tweaks still ask you to confirm, and everything stays undoable in the Restore Center."));
        body.Children.Add(Kit.Card(intro, "Meet your VYTRIX Assistant", "Optional, and off until you add a key."));

        var steps = new StackPanel();
        steps.Children.Add(Step(1, "Create an Anthropic API key in the Anthropic Console, under API keys. Usage is billed to your Anthropic account, usually a few cents per conversation."));
        steps.Children.Add(Step(2, "Paste it below. VYTRIX stores it on this PC, encrypted for your Windows account, and only ever sends it to Anthropic."));
        var console = Ui.IconButton("IcoExport", "Open Anthropic Console", "Ghost");
        console.HorizontalAlignment = HorizontalAlignment.Left;
        console.Margin = new Thickness(0, 4, 0, 14);
        console.Click += (_, _) => SysCmd.Open("https://console.anthropic.com/settings/keys");
        steps.Children.Add(console);

        var pw = new PasswordBox { Height = 38, Padding = new Thickness(10, 8, 10, 8), FontSize = 13, ToolTip = "Your Anthropic API key (starts with sk-ant-)" };
        var save = new Button { Content = "Save key and start", Style = Ui.Style("Primary"), Margin = new Thickness(10, 0, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(pw);
        Grid.SetColumn(save, 1);
        row.Children.Add(save);
        steps.Children.Add(row);
        var err = new TextBlock { Foreground = Ui.Res("BadBrush"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        steps.Children.Add(err);
        save.Click += (_, _) =>
        {
            var k = pw.Password.Trim();
            if (!AssistantKey.LooksValid(k)) { err.Text = "That doesn't look like an Anthropic API key. It should start with sk-ant- and have no spaces."; err.Visibility = Visibility.Visible; return; }
            try { AssistantKey.Save(k); }
            catch (Exception ex) { err.Text = "Couldn't save the key: " + ex.Message; err.Visibility = Visibility.Visible; return; }
            pw.Clear();
            _session = null;
            Render();
            Shell.Toast("Assistant ready. Ask it anything about your PC.", ToastKind.Success);
        };
        var setupCard = Kit.Card(steps, "Set up in two steps");
        setupCard.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(setupCard);

        var priv = new StackPanel();
        priv.Children.Add(Para("Your messages go to Anthropic to generate answers. With **Share PC details** on, the Assistant can also read your hardware models, live readings, free disk space, Windows version, tweak states, the names and memory use of your top apps, and VYTRIX's change history."));
        priv.Children.Add(Para("It never sends your files, your computer name or your Windows user name. You can turn sharing off at any time, or remove your key. Details are in the Terms of Agreement, section 6."));
        var privCard = Kit.Card(priv, "What gets shared");
        privCard.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(privCard);

        _host.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false });
    }

    // ───────────────────────── Chat

    private void BuildChat()
    {
        _share = Kit.Switch(Settings.Current.AssistantSharePcDetails, "Let the Assistant read your PC's readings, tweak states, top apps and VYTRIX change history.");
        _share.Checked += (_, _) => SetShare(true);
        _share.Unchecked += (_, _) => SetShare(false);
        var shareLabel = new TextBlock { Text = "Share PC details", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0), Foreground = Ui.Res("TextDimBrush") };
        var newChat = Ui.IconButton("IcoRefresh", "New chat", "Ghost");
        newChat.Click += (_, _) => { if (_busy) return; _session?.Reset(); ShowWelcome(); };
        var keyBtn = Ui.IconButton("IcoSettings", "Key", "Ghost");
        keyBtn.Margin = new Thickness(8, 0, 0, 0);
        keyBtn.ToolTip = "Remove or replace your Anthropic API key";
        keyBtn.Click += async (_, _) =>
        {
            if (_busy) return;
            if (!await Shell.ConfirmAsync("Remove your API key?", "VYTRIX will delete the key stored on this PC and turn the Assistant off. The key itself keeps working in your Anthropic account until you revoke it there.", "Remove key", danger: true)) return;
            AssistantKey.Delete();
            _session = null;
            _chat.Children.Clear();
            Render();
        };
        HeaderActions.Children.Add(Kit.Row(_share, shareLabel, newChat, keyBtn));

        _host.RowDefinitions.Add(new RowDefinition());
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chatScroll.Content = _chat;
        _host.Children.Add(_chatScroll);
        Grid.SetRow(_status, 1);
        _host.Children.Add(_status);

        var bar = new Grid { Margin = new Thickness(32, 0, 32, 22) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_input);
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.Height = 44;
        Grid.SetColumn(_send, 1);
        bar.Children.Add(_send);
        Grid.SetRow(bar, 2);
        _host.Children.Add(bar);

        if (_chat.Children.Count == 0) ShowWelcome();
    }

    private void SetShare(bool on)
    {
        Settings.Current.AssistantSharePcDetails = on;
        Settings.Save();
        Shell.Toast(on ? "The Assistant can now read your PC's details." : "PC details won't be shared. Answers will be more general.", ToastKind.Info);
    }

    private void ShowWelcome()
    {
        _chat.Children.Clear();
        var hero = new StackPanel { Margin = new Thickness(0, 24, 0, 18), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 620 };
        var badge = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(16), Background = Ui.Res("AccentSoftBrush"), HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.IconPath("IcoSparkle", 26, "AccentHoverBrush") };
        hero.Children.Add(badge);
        hero.Children.Add(new TextBlock { Text = "How can I help your PC today?", Style = Ui.Style("H2"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 6) });
        hero.Children.Add(new TextBlock { Text = "I can check your PC, explain any setting, and suggest changes as buttons. Nothing changes unless you click.", Style = Ui.Style("Muted"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        _chat.Children.Add(hero);
        var grid = Kit.Grid(240, 2, 10);
        grid.MaxWidth = 640;
        foreach (var s in Starters)
        {
            var b = new Button { Style = Ui.Style("Ghost"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 11, 14, 11), Content = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 13 } };
            b.Click += async (_, _) => { _input.Text = s; await SendOrStopAsync(); };
            grid.Children.Add(b);
        }
        _chat.Children.Add(grid);
        _chat.Children.Add(new TextBlock { Text = "Claude can make mistakes. Check important changes before you confirm them.", Style = Ui.Style("Muted"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) });
    }

    private async Task SendOrStopAsync()
    {
        if (_busy) { _cts?.Cancel(); return; }
        var text = _input.Text.Trim();
        if (text.Length == 0 || _session is null) return;
        if (_session.TurnCount == 0) _chat.Children.Clear();
        _input.Clear();
        AddUser(text);
        SetBusy(true, "Thinking…");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(s => _status.Text = s);
            var reply = await Task.Run(() => _session.SendAsync(text, Settings.Current.AssistantSharePcDetails, progress, _cts.Token));
            AddAssistant(reply);
        }
        catch (Exception ex)
        {
            Log.Warn("assistant", $"Request failed: {ex.GetType().Name}");
            AddError(AssistantSession.Describe(ex));
            if (ex is not OperationCanceledException) _session.Reset();   // a failed turn can leave half a tool exchange behind
        }
        finally
        {
            SetBusy(false, "");
            _cts.Dispose(); _cts = null;
            _input.Focus();
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _send.Content = busy ? "Stop" : "Send";
        _input.IsEnabled = !busy;
        _status.Text = status;
        _status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddUser(string text)
    {
        var bubble = new Border
        {
            Background = Ui.Res("AccentSoftBrush"), CornerRadius = new CornerRadius(14, 14, 4, 14), Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 620, Margin = new Thickness(80, 8, 0, 8),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 },
        };
        _chat.Children.Add(bubble);
        Kit.FadeIn(bubble);
        ScrollToEnd();
    }

    private void AddAssistant(AssistantReply reply)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 60, 8), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 780 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var avatar = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Ui.Res("Surface3Brush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0), Child = Ui.IconPath("IcoSparkle", 15, "AccentHoverBrush") };
        row.Children.Add(avatar);
        var col = new StackPanel();
        foreach (var block in Markdownish(reply.Text)) col.Children.Add(block);
        if (reply.Actions.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var a in reply.Actions) wrap.Children.Add(ActionButton(a));
            col.Children.Add(wrap);
        }
        Grid.SetColumn(col, 1);
        row.Children.Add(col);
        _chat.Children.Add(row);
        Kit.FadeIn(row);
        ScrollToEnd();
    }

    private void AddError(string message)
    {
        var n = Kit.Notice(message, "warn");
        n.Margin = new Thickness(42, 6, 60, 6);
        _chat.Children.Add(n);
        ScrollToEnd();
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => _chatScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);

    private Button ActionButton(AssistantAction a)
    {
        var icon = a.Kind switch { "apply_tweak" => "IcoCheck", "turn_off_tweak" => "IcoUndo", _ => "IcoNext" };
        var b = Ui.IconButton(icon, a.Label, a.Kind == "open_page" ? "Ghost" : "Primary");
        b.Margin = new Thickness(0, 4, 8, 4);
        b.ToolTip = a.Reason;
        b.Click += async (_, _) => await RunActionAsync(a, b);
        return b;
    }

    private async Task RunActionAsync(AssistantAction a, Button b)
    {
        if (a.Kind == "open_page") { Shell.Navigate(a.Target); return; }
        var t = TweakCatalog.ById(a.Target);
        if (t is null) return;
        bool on = a.Kind == "apply_tweak";
        if (t.RequiresAdmin && !Elevation.IsAdmin)
        {
            Shell.Toast($"\"{t.Name}\" needs administrator mode. Use \"Relaunch as administrator\" in the sidebar.", ToastKind.Warning);
            return;
        }
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = on ? t.ExpectedEffect : "Your previous value is restored.", Style = Ui.Style("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (on && t.Risk != Risk.Low) body.Children.Add(Kit.Notice($"{t.RiskLabel}: {t.Explanation}", "warn"));
        if (on)
        {
            body.Children.Add(new TextBlock { Text = "VYTRIX will change:", Style = Ui.Style("Label"), Margin = new Thickness(0, 0, 0, 4) });
            body.Children.Add(new TextBlock { Text = t.ChangesSummary, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        }
        body.Children.Add(new TextBlock { Text = "Suggested by the Assistant. You can undo this any time in the Restore Center.", Style = Ui.Style("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        if (!await Shell.ConfirmAsync(on ? $"Turn on \"{t.Name}\"?" : $"Turn off \"{t.Name}\"?", body, on ? "Turn on" : "Turn off", danger: on && t.Risk != Risk.Low)) return;
        bool ok = await Kit.RunAsync(b, () => on ? TweakEngine.ApplyAsync(t, "Suggested by the VYTRIX Assistant") : TweakEngine.RevertAsync(t));
        if (ok) { b.IsEnabled = false; b.Content = on ? "Turned on" : "Turned off"; }
    }

    // ───────────────────────── Rendering helpers

    /// <summary>Paragraphs and "- " / "1. " lists, with **bold** spans. Claude is asked to keep formatting light.</summary>
    internal static IEnumerable<FrameworkElement> Markdownish(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            var lines = raw.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) continue;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var line in lines)
            {
                var l = line.Trim().TrimStart('#').Trim();
                var m = System.Text.RegularExpressions.Regex.Match(l, @"^(?:[-*•]|(\d+)[.)])\s+(.*)$");
                if (m.Success)
                {
                    var g = new Grid { Margin = new Thickness(0, 1, 0, 3) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.Children.Add(new TextBlock { Text = m.Groups[1].Success ? m.Groups[1].Value + "." : "•", Foreground = Ui.Res("AccentHoverBrush"), FontWeight = FontWeights.SemiBold, FontSize = 13.5, LineHeight = 21 });
                    var tb = Rich(m.Groups[2].Value);
                    Grid.SetColumn(tb, 1);
                    g.Children.Add(tb);
                    sp.Children.Add(g);
                }
                else sp.Children.Add(Rich(l));
            }
            yield return sp;
        }
    }

    private static TextBlock Rich(string text)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 21 };
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var piece = parts[i].Replace("`", "");
            tb.Inlines.Add(i % 2 == 1 ? new Run(piece) { FontWeight = FontWeights.SemiBold } : new Run(piece));
        }
        return tb;
    }

    private static TextBlock Para(string text)
    {
        var tb = Rich(text);
        tb.FontSize = 13;
        tb.Foreground = Ui.Res("TextDimBrush");
        tb.Margin = new Thickness(0, 0, 0, 10);
        return tb;
    }

    private static Grid Step(int n, string text)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.Res("AccentSoftBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock { Text = n.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("AccentHoverBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        Grid.SetColumn(tb, 1);
        g.Children.Add(tb);
        return g;
    }
}
