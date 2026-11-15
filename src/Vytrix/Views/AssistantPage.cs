using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

/// <summary>
/// Chat about this PC. Free by default: an open AI model running locally through Ollama.
/// Optionally Claude with the user's own Anthropic key. Suggestions are buttons; nothing changes without a click.
/// </summary>
public sealed class AssistantPage : PageBase
{
    public override string PageTitle => "Assistant";
    public override string PageSubtitle => "Ask about your PC in plain language. It can suggest changes; you decide.";

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
    private AssistantSession? _claude;
    private GeminiAssistant? _gemini;
    private LocalAssistant? _local;
    private bool _showSetup;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private int _renderGen;

    private static bool UseClaude => Settings.Current.AssistantMode == "claude";
    private static bool UseGemini => Settings.Current.AssistantMode == "gemini";
    private static bool UseLocal => !UseClaude && !UseGemini;
    private bool HasEngine => _claude is not null || _gemini is not null || _local is not null;
    private int ActiveTurns => UseClaude ? _claude?.TurnCount ?? 0 : UseGemini ? _gemini?.TurnCount ?? 0 : _local?.TurnCount ?? 0;
    private void ResetAll() { _claude?.Reset(); _gemini?.Reset(); _local?.Reset(); }
    private static LocalModel CurrentModel => Ollama.Models.FirstOrDefault(m => m.Id == Settings.Current.AssistantLocalModel) ?? Ollama.Models[0];

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
        _ = RenderAsync();
    }

    public override void OnShown()
    {
        if (!_busy && HasEngine && !_showSetup) _input.Focus();
    }

    private void Clear()
    {
        HeaderActions.Children.Clear();
        _host.Children.Clear();
        _host.RowDefinitions.Clear();
    }

    private async Task RenderAsync()
    {
        int gen = ++_renderGen;
        Clear();
        if (!_showSetup)
        {
            if (UseClaude && AssistantKey.Load() is { } key)
            {
                _claude ??= new AssistantSession(key);
                BuildChat();
                return;
            }
            if (UseGemini && GeminiKey.Load() is { } gkey)
            {
                _gemini ??= new GeminiAssistant(gkey);
                BuildChat();
                return;
            }
            if (UseLocal)
            {
                _host.Children.Add(new TextBlock { Text = "Starting the local AI…", Style = Ui.Style("Muted"), Margin = new Thickness(32, 12, 32, 0) });
                bool ready = await Ollama.EnsureRunningAsync() && await Ollama.HasModelAsync(CurrentModel.Id);
                if (gen != _renderGen) return;
                Clear();
                if (ready)
                {
                    if (_local?.Model != CurrentModel.Id) { _local = new LocalAssistant(CurrentModel.Id); _chat.Children.Clear(); }
                    BuildChat();
                    return;
                }
            }
        }
        BuildSetup();
    }

    // ───────────────────────── Setup

    private void BuildSetup()
    {
        var body = new StackPanel { Margin = new Thickness(32, 8, 32, 32), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };

        var intro = new StackPanel();
        intro.Children.Add(Para("Ask questions about your PC in plain language. The Assistant looks at your PC's readings and tweaks, explains what would help, and offers buttons for the changes it recommends."));
        intro.Children.Add(Para("It **can't change anything by itself**. Every suggestion is a button you choose to click, tweaks still ask you to confirm, and everything stays undoable in the Restore Center."));
        body.Children.Add(Kit.Card(intro, "Meet your VYTRIX Assistant", "Free, and it runs on your own PC."));

        var free = new StackPanel();
        BuildLocalSetup(free);
        var freeCard = Kit.Card(free, "Free AI on your PC", "Private and offline. Nothing you ask ever leaves this computer.", Ui.Badge("Most private", "GoodBrush", "GoodSoftBrush"));
        freeCard.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(freeCard);

        var gemini = new StackPanel();
        BuildGeminiSetup(gemini);
        var geminiCard = Kit.Card(gemini, "Free online: Gemini", "Google's Gemini with your own free API key. Nothing to install, and quicker answers than local AI.");
        geminiCard.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(geminiCard);

        var claude = new StackPanel();
        BuildClaudeSetup(claude);
        var claudeCard = Kit.Card(claude, "Optional: Claude", "Smarter answers from Anthropic's Claude. Needs your own Anthropic API key, which is paid per use.");
        claudeCard.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(claudeCard);

        if (HasEngine)
        {
            var back = new Button { Content = "Back to chat", Style = Ui.Style("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
            back.Click += async (_, _) => { _showSetup = false; await RenderAsync(); };
            body.Children.Add(back);
        }

        _host.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false });
    }

    private void BuildLocalSetup(StackPanel sp)
    {
        sp.Children.Add(Para("VYTRIX uses **Ollama**, a free app that runs open AI models on your PC. It needs a one-time download, then works without the internet. Answers are good for everyday questions, though not as sharp as Claude."));

        // Step 1: Ollama
        var ollamaState = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var install = new Button { Content = "Install Ollama (free)", Style = Ui.Style("Primary") };
        var site = Ui.IconButton("IcoExport", "Get it from ollama.com", "Ghost");
        site.Margin = new Thickness(8, 0, 0, 0);
        site.Click += (_, _) => SysCmd.Open(Ollama.DownloadPage);
        var recheck = new Button { Content = "Check again", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        var ollamaButtons = Kit.Row(install, site, recheck);
        ollamaButtons.Margin = new Thickness(36, 8, 0, 0);
        sp.Children.Add(StepRow(1, "Install Ollama", ollamaState));
        sp.Children.Add(ollamaButtons);

        // Step 2: model
        var models = new ComboBox { Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        var recommended = Ollama.RecommendedForThisPC();
        foreach (var m in Ollama.Models)
            models.Items.Add(new ComboBoxItem { Content = $"{m.Name}  ·  {m.Size}{(m.Id == recommended.Id ? "  ·  recommended for your PC" : "")}", Tag = m.Id, ToolTip = m.Note, Style = Ui.Style("ComboItem") });
        // Until a model is chosen and downloaded, preselect the one that suits this PC's graphics memory.
        bool chosen = Settings.Current.AssistantLocalModel != Ollama.Models[0].Id || _local is not null;
        var preselect = chosen ? CurrentModel : recommended;
        models.SelectedIndex = Math.Max(0, Array.FindIndex(Ollama.Models, m => m.Id == preselect.Id));
        var modelNote = new TextBlock { Style = Ui.Style("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var modelState = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var download = new Button { Content = "Download model", Style = Ui.Style("Primary"), Margin = new Thickness(10, 0, 0, 0) };
        var barTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Res("Surface3Brush"), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var barFill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0, 1) };
        barTrack.Child = barFill;
        barTrack.SizeChanged += (_, _) => barFill.Width = barTrack.ActualWidth;
        var progress = new TextBlock { Style = Ui.Style("Muted"), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var modelRow = Kit.Row(models, download);
        var modelBox = new StackPanel { Margin = new Thickness(36, 8, 0, 0) };
        modelBox.Children.Add(modelRow);
        modelBox.Children.Add(modelNote);
        modelBox.Children.Add(barTrack);
        modelBox.Children.Add(progress);
        sp.Children.Add(StepRow(2, "Download an AI model", modelState, new Thickness(0, 18, 0, 0)));
        sp.Children.Add(modelBox);

        var start = new Button { Content = "Start chatting", Style = Ui.Style("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0), IsEnabled = false };
        sp.Children.Add(start);

        string Selected() => (models.SelectedItem as ComboBoxItem)?.Tag as string ?? Ollama.Models[0].Id;
        CancellationTokenSource? pullCts = null;

        async Task Refresh()
        {
            var m = Ollama.Models.First(x => x.Id == Selected());
            var vram = SystemInfo.PrimaryGpu?.DedicatedVideoMemory ?? 0;
            modelNote.Text = m.Note + (vram > 0 ? $"  Your {SystemInfo.PrimaryGpu?.Name?.Trim()} has {vram / 1073741824.0:0} GB of video memory, so {recommended.Name} suits it best." : "");
            bool installed = Ollama.Installed;
            bool running = installed && await Ollama.EnsureRunningAsync();
            if (running)
            {
                ollamaState.Text = $"✓  Ollama {await Ollama.VersionAsync()} is installed and running.";
                ollamaState.Foreground = Ui.Res("GoodBrush");
                ollamaButtons.Visibility = Visibility.Collapsed;
            }
            else
            {
                ollamaState.Text = installed ? "Ollama is installed but didn't start. Open it from the Start menu, then click Check again." : "Not installed yet. It's free and takes about a minute.";
                ollamaState.Foreground = Ui.Res(installed ? "WarnBrush" : "TextDimBrush");
                ollamaButtons.Visibility = Visibility.Visible;
                install.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            }
            bool has = running && await Ollama.HasModelAsync(m.Id);
            if (pullCts is null)
            {
                modelState.Text = !running ? "Available once Ollama is running." : has ? $"✓  {m.Name} model is downloaded and ready." : $"Pick a model and download it once ({m.Size}).";
                modelState.Foreground = Ui.Res(has ? "GoodBrush" : "TextDimBrush");
                download.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
                download.IsEnabled = running;
            }
            models.IsEnabled = pullCts is null;
            start.IsEnabled = running && has && pullCts is null;
        }

        models.SelectionChanged += async (_, _) => await Refresh();
        recheck.Click += async (_, _) => await Refresh();
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            ollamaState.Text = "Installing Ollama… this can take a minute or two. Windows may ask for permission.";
            ollamaState.Foreground = Ui.Res("TextDimBrush");
            var (ok, msg) = await Ollama.InstallAsync();
            install.IsEnabled = true;
            Shell.Toast(msg, ok ? ToastKind.Success : ToastKind.Warning);
            await Refresh();
        };
        download.Click += async (_, _) =>
        {
            if (pullCts is not null) { pullCts.Cancel(); return; }
            var m = Ollama.Models.First(x => x.Id == Selected());
            pullCts = new CancellationTokenSource();
            download.Content = "Cancel";
            download.Style = Ui.Style("Ghost");
            models.IsEnabled = false;
            start.IsEnabled = false;
            barTrack.Visibility = progress.Visibility = Visibility.Visible;
            modelState.Text = $"Downloading the {m.Name} model ({m.Size})…";
            var prog = new Progress<(string Status, double Fraction)>(p =>
            {
                if (p.Fraction >= 0) ((ScaleTransform)barFill.RenderTransform).ScaleX = p.Fraction;
                progress.Text = p.Fraction >= 0 ? $"{p.Fraction:P0}" : Capitalize(p.Status);
            });
            var (ok, msg) = await Ollama.PullAsync(m.Id, prog, pullCts.Token);
            pullCts.Dispose(); pullCts = null;
            download.Content = "Download model";
            download.Style = Ui.Style("Primary");
            barTrack.Visibility = progress.Visibility = Visibility.Collapsed;
            Shell.Toast(msg, ok ? ToastKind.Success : ToastKind.Warning);
            await Refresh();
        };
        start.Click += async (_, _) =>
        {
            Settings.Current.AssistantMode = "local";
            Settings.Current.AssistantLocalModel = Selected();
            Settings.Save();
            _showSetup = false;
            await RenderAsync();
        };
        _ = Refresh();
    }

    private void BuildGeminiSetup(StackPanel sp)
    {
        sp.Children.Add(Para("Create a free API key in **Google AI Studio** and paste it here. The free tier has daily limits, and **Google may use what you send to improve its products**, including your PC details if Share PC details is on. The key is stored on this PC, encrypted for your Windows account, and only ever sent to Google."));
        if (GeminiKey.Exists)
        {
            var use = new Button { Content = "Use Gemini", Style = Ui.Style("Primary") };
            use.Click += async (_, _) => { Settings.Current.AssistantMode = "gemini"; Settings.Save(); _showSetup = false; await RenderAsync(); };
            var remove = new Button { Content = "Remove key", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += async (_, _) =>
            {
                if (!await Shell.ConfirmAsync("Remove your Gemini key?", "VYTRIX will delete the key stored on this PC. The key keeps working in your Google account until you delete it in Google AI Studio.", "Remove key", danger: true)) return;
                GeminiKey.Delete();
                _gemini = null;
                if (UseGemini) { Settings.Current.AssistantMode = "local"; Settings.Save(); }
                await RenderAsync();
            };
            sp.Children.Add(new TextBlock { Text = "✓  A Gemini key is saved on this PC.", Foreground = Ui.Res("GoodBrush"), FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });
            sp.Children.Add(Kit.Row(use, remove));
            return;
        }
        var studio = Ui.IconButton("IcoExport", "Get a free key in Google AI Studio", "Ghost");
        studio.HorizontalAlignment = HorizontalAlignment.Left;
        studio.Margin = new Thickness(0, 0, 0, 12);
        studio.Click += (_, _) => SysCmd.Open(GeminiAssistant.KeyPage);
        sp.Children.Add(studio);
        sp.Children.Add(KeyEntry("Your Gemini API key from Google AI Studio", "Save key and use Gemini", GeminiKey.LooksValid,
            "That doesn't look like a Google AI Studio key. Copy the whole key, with no spaces.",
            async k =>
            {
                GeminiKey.Save(k);
                _gemini = null;
                Settings.Current.AssistantMode = "gemini";
                Settings.Save();
                _showSetup = false;
                await RenderAsync();
                Shell.Toast("Gemini is ready. Ask it anything about your PC.", ToastKind.Success);
            }));
    }

    /// <summary>A password box with a save button and inline validation.</summary>
    private static StackPanel KeyEntry(string tooltip, string buttonText, Func<string, bool> valid, string invalidText, Func<string, Task> onSave)
    {
        var sp = new StackPanel();
        var pw = new PasswordBox { Height = 38, Padding = new Thickness(10, 8, 10, 8), FontSize = 13, ToolTip = tooltip };
        var save = new Button { Content = buttonText, Style = Ui.Style("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(pw);
        Grid.SetColumn(save, 1);
        row.Children.Add(save);
        sp.Children.Add(row);
        var err = new TextBlock { Foreground = Ui.Res("BadBrush"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(err);
        save.Click += async (_, _) =>
        {
            var k = pw.Password.Trim();
            if (!valid(k)) { err.Text = invalidText; err.Visibility = Visibility.Visible; return; }
            try { pw.Clear(); await onSave(k); }
            catch (Exception ex) { err.Text = "Couldn't save the key: " + ex.Message; err.Visibility = Visibility.Visible; }
        };
        return sp;
    }

    private void BuildClaudeSetup(StackPanel sp)
    {
        sp.Children.Add(Para("Create an API key in the Anthropic Console and paste it here. Usage is billed to your Anthropic account. The key is stored on this PC, encrypted for your Windows account, and only ever sent to Anthropic."));
        sp.Children.Add(Para("With **Share PC details** on, your hardware, live readings, tweak states, top apps and VYTRIX's change history are sent with your questions. Your files, computer name and user name never are."));
        bool hasKey = AssistantKey.Exists;
        if (hasKey)
        {
            var use = new Button { Content = "Use Claude", Style = Ui.Style("Primary") };
            use.Click += async (_, _) => { Settings.Current.AssistantMode = "claude"; Settings.Save(); _showSetup = false; await RenderAsync(); };
            var remove = new Button { Content = "Remove key", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += async (_, _) =>
            {
                if (!await Shell.ConfirmAsync("Remove your API key?", "VYTRIX will delete the key stored on this PC. The key keeps working in your Anthropic account until you revoke it there.", "Remove key", danger: true)) return;
                AssistantKey.Delete();
                _claude = null;
                if (UseClaude) { Settings.Current.AssistantMode = "local"; Settings.Save(); }
                await RenderAsync();
            };
            var state = new TextBlock { Text = "✓  A key is saved on this PC.", Foreground = Ui.Res("GoodBrush"), FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(state);
            sp.Children.Add(Kit.Row(use, remove));
            return;
        }
        var console = Ui.IconButton("IcoExport", "Open Anthropic Console", "Ghost");
        console.HorizontalAlignment = HorizontalAlignment.Left;
        console.Margin = new Thickness(0, 0, 0, 12);
        console.Click += (_, _) => SysCmd.Open("https://console.anthropic.com/settings/keys");
        sp.Children.Add(console);
        var pw = new PasswordBox { Height = 38, Padding = new Thickness(10, 8, 10, 8), FontSize = 13, ToolTip = "Your Anthropic API key (starts with sk-ant-)" };
        var save = new Button { Content = "Save key and use Claude", Style = Ui.Style("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(pw);
        Grid.SetColumn(save, 1);
        row.Children.Add(save);
        sp.Children.Add(row);
        var err = new TextBlock { Foreground = Ui.Res("BadBrush"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(err);
        save.Click += async (_, _) =>
        {
            var k = pw.Password.Trim();
            if (!AssistantKey.LooksValid(k)) { err.Text = "That doesn't look like an Anthropic API key. It should start with sk-ant- and have no spaces."; err.Visibility = Visibility.Visible; return; }
            try { AssistantKey.Save(k); }
            catch (Exception ex) { err.Text = "Couldn't save the key: " + ex.Message; err.Visibility = Visibility.Visible; return; }
            pw.Clear();
            _claude = null;
            Settings.Current.AssistantMode = "claude";
            Settings.Save();
            _showSetup = false;
            await RenderAsync();
            Shell.Toast("Claude is ready. Ask it anything about your PC.", ToastKind.Success);
        };
    }

    // ───────────────────────── Chat

    private void BuildChat()
    {
        var chip = UseClaude ? Ui.Badge("Claude  ·  your key", "AccentHoverBrush", "AccentSoftBrush")
            : UseGemini ? Ui.Badge("Gemini  ·  free tier", "AccentHoverBrush", "AccentSoftBrush")
            : Ui.Badge($"Free  ·  on this PC  ·  {CurrentModel.Name}", "GoodBrush", "GoodSoftBrush");
        chip.VerticalAlignment = VerticalAlignment.Center;
        chip.ToolTip = UseClaude ? "Answers come from Claude, billed to your Anthropic key."
            : UseGemini ? "Answers come from Google's Gemini on your free key. Google may use what you send to improve its products."
            : "Answers come from an AI model running on this PC. Nothing leaves your computer.";
        var items = new List<UIElement> { chip };
        if (!UseLocal)
        {
            var who = UseClaude ? "Claude" : "Gemini";
            var share = Kit.Switch(Settings.Current.AssistantSharePcDetails, $"Let {who} read your PC's readings, tweak states, top apps and VYTRIX change history.");
            share.Margin = new Thickness(16, 0, 0, 0);
            share.Checked += (_, _) => SetShare(true);
            share.Unchecked += (_, _) => SetShare(false);
            items.Add(share);
            items.Add(new TextBlock { Text = "Share PC details", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Ui.Res("TextDimBrush") });
        }
        var newChat = Ui.IconButton("IcoRefresh", "New chat", "Ghost");
        newChat.Margin = new Thickness(16, 0, 0, 0);
        newChat.Click += (_, _) => { if (_busy) return; ResetAll(); ShowWelcome(); };
        items.Add(newChat);
        var settings = Ui.IconButton("IcoSettings", "AI settings", "Ghost");
        settings.Margin = new Thickness(8, 0, 0, 0);
        settings.ToolTip = "Switch between free local AI, Gemini and Claude, or change the model";
        settings.Click += async (_, _) => { if (_busy) return; _showSetup = true; await RenderAsync(); };
        items.Add(settings);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) row.Children.Add(i);
        HeaderActions.Children.Add(row);

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
        if (_input.Parent is Panel old) old.Children.Remove(_input);
        if (_send.Parent is Panel old2) old2.Children.Remove(_send);
        bar.Children.Add(_input);
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.Height = 44;
        Grid.SetColumn(_send, 1);
        bar.Children.Add(_send);
        Grid.SetRow(bar, 2);
        _host.Children.Add(bar);

        if (_chat.Children.Count == 0 || ActiveTurns == 0) ShowWelcome();
    }

    private void SetShare(bool on)
    {
        Settings.Current.AssistantSharePcDetails = on;
        Settings.Save();
        var who = UseClaude ? "Claude" : "Gemini";
        Shell.Toast(on ? $"{who} can now read your PC's details." : $"PC details won't be shared with {who}. Answers will be more general.", ToastKind.Info);
    }

    private void ShowWelcome()
    {
        _chat.Children.Clear();
        var hero = new StackPanel { Margin = new Thickness(0, 24, 0, 18), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 620 };
        var badge = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(16), Background = Ui.Res("AccentSoftBrush"), HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.IconPath("IcoSparkle", 26, "AccentHoverBrush") };
        hero.Children.Add(badge);
        hero.Children.Add(new TextBlock { Text = "How can I help your PC today?", Style = Ui.Style("H2"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 6) });
        hero.Children.Add(new TextBlock
        {
            Text = !UseLocal
                ? "I can check your PC, explain any setting, and suggest changes as buttons. Nothing changes unless you click."
                : "I run right here on your PC, for free. I can check your PC, explain any setting, and suggest changes as buttons. Nothing changes unless you click.",
            Style = Ui.Style("Muted"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
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
        _chat.Children.Add(new TextBlock { Text = "AI can make mistakes. Check important changes before you confirm them.", Style = Ui.Style("Muted"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) });
    }

    private async Task SendOrStopAsync()
    {
        if (_busy) { _cts?.Cancel(); return; }
        var text = _input.Text.Trim();
        bool claude = UseClaude, gemini = UseGemini;
        if (text.Length == 0 || (claude ? _claude is null : gemini ? _gemini is null : _local is null)) return;
        if (ActiveTurns == 0) _chat.Children.Clear();
        _input.Clear();
        AddUser(text);
        SetBusy(true, claude || gemini ? "Thinking…" : "Thinking… (the first answer can take a little longer while the model loads)");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var progress = new Progress<string>(SetStatus);
            bool share = Settings.Current.AssistantSharePcDetails;
            var reply = claude ? await Task.Run(() => _claude!.SendAsync(text, share, progress, ct))
                : gemini ? await Task.Run(() => _gemini!.SendAsync(text, share, progress, ct))
                : await Task.Run(() => _local!.SendAsync(text, progress, ct));
            AddAssistant(reply);
        }
        catch (Exception ex)
        {
            Log.Warn("assistant", $"Request failed: {ex.GetType().Name}");
            AddError(claude ? AssistantSession.Describe(ex) : gemini ? GeminiAssistant.Describe(ex) : LocalAssistant.Describe(ex));
            if (ex is not OperationCanceledException) ResetAll();   // a failed turn can leave half a tool exchange behind
        }
        finally
        {
            SetBusy(false, "");
            _cts.Dispose(); _cts = null;
            _input.Focus();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _clock;
    private DateTime _busySince;
    private string _statusText = "";

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _send.Content = busy ? "Stop" : "Send";
        _input.IsEnabled = !busy;
        _statusText = status;
        _status.Text = status;
        _status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // A visible seconds counter so a slow answer never looks frozen.
        _clock?.Stop();
        if (!busy) return;
        _busySince = DateTime.Now;
        _clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => _status.Text = $"{_statusText}   {(int)(DateTime.Now - _busySince).TotalSeconds}s  ·  Stop cancels";
        _clock.Start();
    }

    private void SetStatus(string s) { _statusText = s; _status.Text = $"{s}   {(int)(DateTime.Now - _busySince).TotalSeconds}s  ·  Stop cancels"; }

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

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Paragraphs and "- " / "1. " lists, with **bold** spans. The AI is asked to keep formatting light.</summary>
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
            var piece = parts[i].Replace("`", "").Replace("*", "");   // single-asterisk italics aren't rendered; drop the markers
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

    private static Grid StepRow(int n, string title, TextBlock state, Thickness? margin = null)
    {
        var g = new Grid { Margin = margin ?? new Thickness(0, 4, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.Res("AccentSoftBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock { Text = n.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("AccentHoverBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var col = new StackPanel();
        col.Children.Add(new TextBlock { Text = title, Style = Ui.Style("H3") });
        state.Margin = new Thickness(0, 3, 0, 0);
        col.Children.Add(state);
        Grid.SetColumn(col, 1);
        g.Children.Add(col);
        return g;
    }
}
