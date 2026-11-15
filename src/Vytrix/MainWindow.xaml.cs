using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;
using Vytrix.Views;

namespace Vytrix;

public enum ToastKind { Info, Success, Warning, Error }

public partial class MainWindow : Window
{
    private sealed record NavEntry(string Key, string Label, string Icon, Func<PageBase> Factory, string Keywords);
    private sealed record SearchHit(string Title, string Subtitle, string Icon, Action Go);

    private readonly List<NavEntry> _nav;
    private readonly Dictionary<string, PageBase> _pages = [];
    private readonly Dictionary<string, RadioButton> _navButtons = [];
    private PageBase? _current;
    private TaskCompletionSource<bool>? _dialog;

    public MainWindow()
    {
        InitializeComponent();
        _nav =
        [
            new("overview", "Overview", "IcoOverview", () => new OverviewPage(), "dashboard cpu gpu ram temperature uptime health"),
            new("assistant", "Assistant", "IcoSparkle", () => new AssistantPage(), "ai assistant claude chat ask question advice anthropic"),
            new("optimization", "Optimization", "IcoOptimize", () => new OptimizationPage(), "profiles recommendations balanced gaming competitive creator custom background apps"),
            new("gaming", "Gaming", "IcoGaming", () => new GamingPage(), "game mode hags gpu scheduling power plan launch fullscreen"),
            new("network", "Network", "IcoNetwork", () => new NetworkPage(), "dns ping latency packet loss adapter wifi ethernet winsock"),
            new("memory", "Memory", "IcoMemory", () => new MemoryPage(), "ram commit processes usage"),
            new("storage", "Storage", "IcoStorage", () => new StoragePage(), "temp cleanup disk folders recycle bin"),
            new("tweaks", "Tweaks", "IcoTweaks", () => new TweaksPage(), "tweak library privacy visual effects registry"),
            new("startup", "Startup", "IcoStartup", () => new StartupPage(), "startup apps boot"),
            new("restore", "Restore Center", "IcoRestore", () => new RestorePage(), "rollback undo restore point backup log journal"),
            new("hardware", "Hardware", "IcoHardware", () => new HardwarePage(), "cpu gpu motherboard bios ram disk specs"),
            new("apps", "Installed Apps", "IcoApps", () => new AppsPage(), "uninstall programs software"),
            new("services", "Services", "IcoServices", () => new ServicesPage(), "windows services startup type"),
            new("benchmark", "Benchmark", "IcoBenchmark", () => new BenchmarkPage(), "winsat score disk memory cpu speed test"),
            new("settings", "Settings", "IcoSettings", () => new SettingsPage(), "theme accent updates report export"),
            new("help", "Help", "IcoHelp", () => new HelpPage(), "help guide how to faq questions manual tutorial"),
        ];
        BuildNav();
        UpdateAdminState();
        BuildScene();
        BuildSplash();
        StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { GlobalSearch.Focus(); GlobalSearch.SelectAll(); e.Handled = true; }
            if (e.Key == Key.Escape && DialogLayer.Visibility == Visibility.Visible) { CloseDialog(false); e.Handled = true; }
        };
        Loaded += async (_, _) =>
        {
            // --page <key> opens a specific page (used for QA and shortcuts).
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--page");
            Navigate(i >= 0 && i + 1 < args.Length ? args[i + 1] : "overview");
            // The Terms of Agreement must be accepted before VYTRIX can be used (again after a new version).
            if (!Terms.Accepted)
            {
                if (await ShowTermsAsync(requireAcceptance: true)) Terms.Accept();
                else { Log.Info("terms", "Terms declined; exiting."); Application.Current.Shutdown(); return; }
            }
            // Automatic updates: check now, then every 6 hours while VYTRIX is open.
            if (Settings.Current.CheckUpdatesOnStart)
            {
                await CheckForUpdatesAsync(userInitiated: false);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
                timer.Tick += async (_, _) => { if (Settings.Current.CheckUpdatesOnStart) await CheckForUpdatesAsync(userInitiated: false); };
                timer.Start();
            }
        };
    }

    // ───────────────────────── Auto-update

    private bool _updateBusy;
    private string? _declinedVersion;

    /// <summary>
    /// Checks GitHub for a newer release. Automatic checks stay quiet unless an update is
    /// found; the installer is downloaded and verified in the background (if enabled),
    /// then the user chooses Install now or Later. Returns a status message.
    /// </summary>
    public async Task<(bool Ok, string Message)> CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateBusy) return (true, "An update check is already running.");
        _updateBusy = true;
        try
        {
            var (m, msg) = await Updater.CheckAsync();
            if (m is null) return (!msg.StartsWith("Update check failed") && !msg.Contains("invalid") && !msg.Contains("ignoring"), msg);
            if (!userInitiated && _declinedVersion == m.Version) return (true, msg);   // don't nag twice per session

            string? installer = Updater.ReadyInstaller(m);
            if (installer is null && (Settings.Current.AutoDownloadUpdates || userInitiated))
            {
                if (userInitiated) Toast($"Downloading VYTRIX {m.Version}…");
                var (ok, result) = await Updater.DownloadAndVerifyAsync(m);
                if (!ok) { if (userInitiated) return (false, result); Log.Warn("update", result); return (false, result); }
                installer = result;
            }

            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = $"You have v{Updater.Current.ToString(3)}. VYTRIX {m.Version} is available.", Style = Ui.Style("Dim"), Margin = new Thickness(0, 0, 0, 10) });
            if (!string.IsNullOrWhiteSpace(m.Notes))
                body.Children.Add(new Border
                {
                    Style = Ui.Style("Inset"),
                    Child = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = m.Notes, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 } },
                });
            body.Children.Add(new TextBlock
            {
                Text = installer is not null
                    ? "The update is downloaded and its SHA-256 checksum verified. Windows will ask for administrator permission, VYTRIX closes, installs the update and reopens. Your settings, change history and backups are kept."
                    : "The installer will be downloaded from GitHub and verified before anything runs.",
                Style = Ui.Style("Muted"), Margin = new Thickness(0, 10, 0, 0),
            });
            if (!await ConfirmAsync($"Update to VYTRIX {m.Version}?", body, "Install now", cancelText: "Later"))
            {
                _declinedVersion = m.Version;
                return (true, $"VYTRIX {m.Version} will be offered again next time.");
            }
            if (installer is null)
            {
                var (ok, result) = await Updater.DownloadAndVerifyAsync(m);
                if (!ok) return (false, result);
                installer = result;
            }
            var launch = Updater.LaunchInstaller(installer);
            if (launch.Ok) Application.Current.Shutdown();
            return launch;
        }
        finally { _updateBusy = false; }
    }

    // ───────────────────────── Cherry Blossom scene

    private bool _dialogOpen;
    private SakuraTree? _sakura;
    private PetalField? _petals;
    private ChristmasTree? _xmas;
    private SnowField? _snow;
    private FireworksSky? _fireworks;
    private SnowField? _glitter;
    private Viewbox? _sakuraBox, _xmasBox, _nyBox;

    // ───────────────────────── Splash

    private void BuildSplash()
    {
        // Letter-spaced wordmark (WPF has no letter-spacing, so each letter is its own element).
        foreach (var ch in "VYTRIX")
            SplashWordmark.Children.Add(new TextBlock { Text = ch.ToString(), FontFamily = (FontFamily)Application.Current.Resources["NumberFont"], FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 7, 0) });
        var started = DateTime.Now;
        if (!Settings.Current.ReduceMotion)
        {
            SplashBarMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-60, 180, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            var pulse = new ScaleTransform(0.92, 0.92);
            SplashMark.RenderTransform = pulse;
            SplashMark.RenderTransformOrigin = new Point(0.5, 0.5);
            var grow = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            pulse.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            pulse.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        // Leave once the first live reading is in (so the dashboard never shows placeholders), 0.9–2.5 s.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - started;
            if (elapsed.TotalMilliseconds < 900 || (!Telemetry.Instance.HasSample && elapsed.TotalMilliseconds < 2500)) return;
            timer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Settings.Current.ReduceMotion ? 1 : 380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => { Splash.Visibility = Visibility.Collapsed; SplashBarMove.BeginAnimation(TranslateTransform.XProperty, null); };
            Splash.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    // ───────────────────────── Title bar tools (theme switcher, carols)

    private System.Windows.Controls.Primitives.Popup? _themePopup;

    private Button PillButton(string icon, string? text, string tip)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Ui.IconPath(icon, 13, "TextDimBrush"));
        if (text is not null) sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(7, 0, 0, 0), Foreground = Ui.Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center });
        return new Button { Content = sp, Style = Ui.Style("Ghost"), Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip, Focusable = false };
    }

    private void BuildTitleTools()
    {
        TitleTools.Children.Clear();
        if (ThemeManager.Current == ThemeManager.Christmas)
        {
            var p = CarolPlayer.Instance;
            var music = PillButton(p.IsPlaying ? "IcoPause" : "IcoMusic", p.IsPreparing ? "Preparing…" : p.IsPlaying ? p.CurrentTitle : "Carols",
                p.IsPlaying ? $"{p.CurrentTitle}\n{p.CurrentCredit}\nClick to pause" : "Play music-box carols (public domain, synthesized by VYTRIX)");
            music.Click += async (_, _) => await p.ToggleAsync();
            TitleTools.Children.Add(music);
            if (p.IsPlaying)
            {
                var next = PillButton("IcoNext", null, "Next carol");
                next.Click += (_, _) => p.Next();
                TitleTools.Children.Add(next);
            }
        }
        var theme = PillButton("IcoPalette", ThemeManager.Current, "Change theme");
        theme.Click += (_, _) => ShowThemeMenu(theme);
        TitleTools.Children.Add(theme);
    }

    private void ShowThemeMenu(FrameworkElement anchor)
    {
        var list = new StackPanel { Margin = new Thickness(4) };
        foreach (var t in ThemeManager.Themes)
        {
            var (swatch1, swatch2, blurb) = t switch
            {
                ThemeManager.Christmas => ("#C8102E", "#1D6B4A", "Midnight pine, twinkling tree, snowfall and carols"),
                ThemeManager.NewYear => ("#D9A42B", "#1F274A", "Midnight skyline, fireworks and gold glitter"),
                ThemeManager.CherryBlossom => ("#DB2777", "#C4B5FD", "Night sakura with a swaying cherry tree"),
                _ => ("#8B5CF6", "#22D3EE", "The original graphite and violet"),
            };
            var row = new Grid { Margin = new Thickness(8, 7, 14, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var sw = new Grid { Width = 26, Height = 26 };
            sw.Children.Add(new Border { CornerRadius = new CornerRadius(13), Background = (Brush)new BrushConverter().ConvertFrom(swatch2)! });
            sw.Children.Add(new Border { CornerRadius = new CornerRadius(13, 0, 0, 13), Width = 13, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)new BrushConverter().ConvertFrom(swatch1)! });
            row.Children.Add(sw);
            var txt = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            txt.Children.Add(new TextBlock { Text = t + (t == ThemeManager.Current ? "   ✓" : ""), FontSize = 13 });
            txt.Children.Add(new TextBlock { Text = blurb, Style = Ui.Style("Muted"), FontSize = 11 });
            Grid.SetColumn(txt, 1);
            row.Children.Add(txt);
            var b = new Button { Content = row, Style = Ui.Style("Ghost"), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
            var name = t;
            b.Click += (_, _) => { _themePopup!.IsOpen = false; SwitchTheme(name); };
            list.Children.Add(b);
        }
        _themePopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, HorizontalOffset = -180,
            Child = new Border
            {
                Background = Ui.Res("Surface2Brush"), BorderBrush = Ui.Res("BorderStrongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 8, 12, 12), Width = 330, Child = list,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black },
            },
        };
        _themePopup.IsOpen = true;
    }

    /// <summary>Switches theme live, with its signature accent, and remembers the choice.</summary>
    public void SwitchTheme(string name)
    {
        Settings.Current.Theme = name;
        Settings.Current.NewYearThemeAutomatic = false;   // picked by hand: the seasonal switch leaves it alone
        Settings.Current.Accent = ThemeManager.DefaultAccent(name);
        ThemeManager.ApplyTheme(name);
        ThemeManager.Apply(Settings.Current.Accent);
        if (name != ThemeManager.Christmas && CarolPlayer.Instance.IsPlaying) CarolPlayer.Instance.Pause();
        Settings.Save();
        Toast($"{name} theme applied.", ToastKind.Success);
    }

    private void BuildScene()
    {
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(BuildTitleTools);
        CarolPlayer.Instance.StateChanged += () => Dispatcher.BeginInvoke(BuildTitleTools);
        StateChanged += (_, _) => CarolPlayer.Instance.SetWindowVisible(WindowState != WindowState.Minimized);
        Loaded += (_, _) => BuildTitleTools();
        ThemeManager.Changed += UpdateScene;
        Settings.Changed += UpdateScene;
        StateChanged += (_, _) => UpdateScene();
        Activated += (_, _) => UpdateScene();
        Deactivated += (_, _) => UpdateScene();
        Loaded += (_, _) => UpdateScene();
    }

    // Scenes are created the first time their theme is shown (no cost for unused themes).
    private void EnsureSceneFor(string theme)
    {
        if (theme == ThemeManager.CherryBlossom && _sakura is null)
        {
            _sakura = new SakuraTree();
            _sakuraBox = new Viewbox
            {
                Child = _sakura, Stretch = Stretch.Uniform, Width = 700, Height = 700 * SakuraTree.DesignHeight / SakuraTree.DesignWidth,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -30, -8),
            };
            SceneHost.Children.Add(_sakuraBox);
            _petals = new PetalField();
            PetalHost.Children.Add(_petals);
        }
        if (theme == ThemeManager.Christmas && _xmas is null)
        {
            _xmas = new ChristmasTree();
            _xmasBox = new Viewbox
            {
                Child = _xmas, Stretch = Stretch.Uniform, Height = 600, Width = 600 * ChristmasTree.DesignWidth / ChristmasTree.DesignHeight,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, -14),
            };
            SceneHost.Children.Add(_xmasBox);
            _snow = new SnowField();
            PetalHost.Children.Add(_snow);
        }
        if (theme == ThemeManager.NewYear && _fireworks is null)
        {
            _fireworks = new FireworksSky();
            _nyBox = new Viewbox
            {
                Child = _fireworks, Stretch = Stretch.Uniform, Height = 560, Width = 560 * FireworksSky.DesignWidth / FireworksSky.DesignHeight,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -6),
            };
            SceneHost.Children.Add(_nyBox);
            _glitter = new SnowField(Color.FromRgb(0xF6, 0xD0, 0x6F));
            PetalHost.Children.Add(_glitter);
        }
    }

    /// <summary>Raised with whether theme scenery may animate right now (Overview's hero listens).</summary>
    public static event Action<bool>? SceneAnimationChanged;
    public static bool SceneAnimating { get; private set; }

    /// <summary>
    /// Shows the active theme's scene and animates it only while VYTRIX is the active, visible
    /// window and Reduce motion is off, so it costs nothing in the background.
    /// </summary>
    private void UpdateScene()
    {
        var theme = ThemeManager.Current;
        bool sakura = theme == ThemeManager.CherryBlossom, xmas = theme == ThemeManager.Christmas, ny = theme == ThemeManager.NewYear;
        bool scenic = sakura || xmas || ny;
        EnsureSceneFor(theme);
        // The Overview has its own hero, so the background scene steps aside there.
        bool overview = _current is OverviewPage;
        bool particles = Settings.Current.ShowFallingPetals;
        bool animate = scenic && IsActive && WindowState != WindowState.Minimized && !Settings.Current.ReduceMotion && !_dialogOpen;
        if (_sakuraBox is not null) _sakuraBox.Visibility = sakura && !overview ? Visibility.Visible : Visibility.Collapsed;
        if (_xmasBox is not null) _xmasBox.Visibility = xmas && !overview ? Visibility.Visible : Visibility.Collapsed;
        if (_nyBox is not null) _nyBox.Visibility = ny && !overview ? Visibility.Visible : Visibility.Collapsed;
        if (_petals is not null) { _petals.Visibility = sakura && particles ? Visibility.Visible : Visibility.Collapsed; _petals.SetRunning(animate && sakura && particles); }
        if (_snow is not null) { _snow.Visibility = xmas && particles ? Visibility.Visible : Visibility.Collapsed; _snow.SetRunning(animate && xmas && particles); }
        if (_glitter is not null) { _glitter.Visibility = ny && particles ? Visibility.Visible : Visibility.Collapsed; _glitter.SetRunning(animate && ny && particles); }
        _sakura?.SetRunning(animate && sakura && !overview);
        _xmas?.SetRunning(animate && xmas && !overview);
        _fireworks?.SetRunning(animate && ny && !overview);
        SceneHost.Visibility = scenic && !overview ? Visibility.Visible : Visibility.Collapsed;
        PetalHost.Visibility = scenic && particles ? Visibility.Visible : Visibility.Collapsed;
        SceneAnimating = animate;
        SceneAnimationChanged?.Invoke(animate);
    }

    private void BuildNav()
    {
        void Group(string label)
        {
            NavPanel.Children.Add(new TextBlock { Text = label, Style = Ui.Style("Label"), Margin = new Thickness(14, NavPanel.Children.Count == 0 ? 0 : 14, 0, 5) });
        }
        Group("MAIN");
        foreach (var n in _nav)
        {
            if (n.Key == "hardware") Group("SYSTEM");
            if (n.Key == "settings") Group("APP");
            var rb = new RadioButton { Content = n.Label, Tag = Ui.Icon(n.Icon), Style = Ui.Style("NavItem"), GroupName = "nav" };
            rb.Checked += (_, _) => Navigate(n.Key);
            _navButtons[n.Key] = rb;
            NavPanel.Children.Add(rb);
        }
    }

    public void Navigate(string key, Action<PageBase>? after = null)
    {
        var entry = _nav.FirstOrDefault(n => n.Key == key);
        if (entry is null) return;
        if (_navButtons.TryGetValue(key, out var rb) && rb.IsChecked != true) { rb.IsChecked = true; }   // re-enters via Checked
        if (!_pages.TryGetValue(key, out var page))
        {
            try { page = entry.Factory(); _pages[key] = page; }
            catch (Exception ex) { Log.Error("nav", $"Page {key} failed to build", ex); Toast($"Could not open {entry.Label}: {ex.Message}", ToastKind.Error); return; }
        }
        if (_current != page)
        {
            _current?.OnHidden();
            _current = page;
            PageHost.Content = page;
            try { page.Show(); }
            catch (Exception ex) { Log.Error("nav", $"Page {key} failed", ex); Toast($"{entry.Label} hit an error: {ex.Message}", ToastKind.Error); }
            Kit.FadeIn(page);
            UpdateScene();
        }
        after?.Invoke(page);
    }

    // ───────────────────────── Admin / elevation

    private void UpdateAdminState()
    {
        if (Elevation.IsAdmin)
        {
            AdminDot.Fill = Ui.Res("GoodBrush");
            AdminText.Text = "Administrator mode";
            AdminHint.Text = "All modules available. Changes are journaled and reversible.";
            ElevateButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Relaunch as administrator?",
                "Windows will ask for permission (UAC). VYTRIX will restart with administrator rights so it can change machine-wide settings, services, DNS and restore points.",
                "Relaunch")) return;
        if (Elevation.RelaunchElevated("--elevated")) Application.Current.Shutdown();
        else Toast("Administrator rights were not granted. VYTRIX keeps running in standard mode.", ToastKind.Warning);
    }

    // ───────────────────────── Window chrome

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────── Global search

    private List<SearchHit> Search(string q)
    {
        q = q.Trim();
        if (q.Length < 2) return [];
        bool M(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var hits = new List<SearchHit>();
        foreach (var n in _nav.Where(n => M(n.Label) || M(n.Keywords)))
            hits.Add(new SearchHit(n.Label, "Page", n.Icon, () => Navigate(n.Key)));
        foreach (var t in TweakCatalog.All.Where(t => M(t.Name) || M(t.Description) || M(t.CategoryLabel) || M(t.ComponentLabel)))
            hits.Add(new SearchHit(t.Name, $"Tweak · {t.ComponentLabel}", "IcoTweaks", () => Navigate("tweaks", p => ((TweaksPage)p).Focus(t.Id))));
        foreach (var p in Profiles.All.Where(p => M(p.Name) || M(p.Description)))
            hits.Add(new SearchHit($"{p.Name} profile", "Optimization profile", "IcoOptimize", () => Navigate("optimization")));
        foreach (var t in HelpContent.Topics.Where(t => M(t.Title) || M(t.Keywords) || M(t.Summary)))
            hits.Add(new SearchHit(t.Title, "Help topic", "IcoHelp", () => Navigate("help", p => ((HelpPage)p).Focus(t.Id))));
        return hits.Take(12).ToList();
    }

    private void GlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hits = Search(GlobalSearch.Text);
        SearchResults.Items.Clear();
        foreach (var h in hits)
        {
            var g = new Grid { Margin = new Thickness(8, 7, 8, 7), Tag = h };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Ui.IconPath(h.Icon, 15));
            var sp = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            sp.Children.Add(new TextBlock { Text = h.Title });
            sp.Children.Add(new TextBlock { Text = h.Subtitle, Style = Ui.Style("Muted"), FontSize = 11 });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            SearchResults.Items.Add(g);
        }
        if (hits.Count == 0 && GlobalSearch.Text.Trim().Length >= 2)
            SearchResults.Items.Add(new TextBlock { Text = "No matches", Style = Ui.Style("Muted"), Margin = new Thickness(10, 8, 10, 8) });
        SearchPopup.IsOpen = GlobalSearch.Text.Trim().Length >= 2;
        if (hits.Count > 0) SearchResults.SelectedIndex = 0;
    }

    private void GlobalSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (!SearchPopup.IsOpen) return;
        if (e.Key == Key.Down) { SearchResults.SelectedIndex = Math.Min(SearchResults.Items.Count - 1, SearchResults.SelectedIndex + 1); e.Handled = true; }
        else if (e.Key == Key.Up) { SearchResults.SelectedIndex = Math.Max(0, SearchResults.SelectedIndex - 1); e.Handled = true; }
        else if (e.Key == Key.Enter) { ActivateSearch(SearchResults.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchPopup.IsOpen = false; e.Handled = true; }
    }

    private void SearchResults_Click(object sender, MouseButtonEventArgs e) => ActivateSearch(SearchResults.SelectedItem);

    private void ActivateSearch(object? item)
    {
        if (item is Grid { Tag: SearchHit h })
        {
            SearchPopup.IsOpen = false;
            GlobalSearch.Text = "";
            h.Go();
        }
    }

    private void GlobalSearch_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => { if (!SearchResults.IsKeyboardFocusWithin && !SearchResults.IsMouseOver) SearchPopup.IsOpen = false; }, DispatcherPriority.Input);
    }

    // ───────────────────────── Dialogs

    /// <summary>Shows a modal confirmation. Returns true only if the user explicitly confirms.</summary>
    public Task<bool> ConfirmAsync(string title, object body, string confirmText = "Confirm", bool danger = false, string cancelText = "Cancel", bool showCancel = true)
    {
        _dialog?.TrySetResult(false);
        _dialog = new TaskCompletionSource<bool>();
        DialogTitle.Text = title;
        DialogBody.Content = body is string s ? new TextBlock { Text = s, Style = Ui.Style("Dim"), FontSize = 13, LineHeight = 20 } : body;
        DialogOk.Content = confirmText;
        DialogOk.Style = Ui.Style(danger ? "Danger" : "Primary");
        DialogCancel.Content = cancelText;
        DialogCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DialogIcon.Data = Ui.Icon(danger ? "IcoAlert" : "IcoInfo");
        DialogIcon.Stroke = Ui.Res(danger ? "WarnBrush" : "AccentHoverBrush");
        DialogLayer.Visibility = Visibility.Visible;
        // Deep scrim behind the dialog (a BlurEffect produced edge artifacts around clipped
        // artwork in testing); pause the scenery while the dialog is open.
        _dialogOpen = true;
        UpdateScene();
        if (!Settings.Current.ReduceMotion)
        {
            DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var st = new ScaleTransform(0.97, 0.97);
            DialogCard.RenderTransform = st;
            DialogCard.RenderTransformOrigin = new Point(0.5, 0.5);
            var anim = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        (showCancel ? DialogCancel : DialogOk).Focus();   // default focus on the safe choice
        return _dialog.Task;
    }

    public Task AlertAsync(string title, object body) => ConfirmAsync(title, body, "OK", showCancel: false);

    /// <summary>
    /// Shows the Terms of Agreement. With requireAcceptance, Accept stays disabled until the
    /// checkbox is ticked and the result says whether the user accepted; otherwise it is read-only.
    /// </summary>
    public async Task<bool> ShowTermsAsync(bool requireAcceptance)
    {
        var body = new StackPanel();
        if (requireAcceptance)
            body.Children.Add(new TextBlock { Text = "Please read and accept the Terms of Agreement to use VYTRIX.", Style = Ui.Style("Dim"), Margin = new Thickness(0, 0, 0, 10) });
        var text = new TextBlock
        {
            Text = Terms.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextDimBrush"),
        };
        body.Children.Add(new Border
        {
            Background = Ui.Res("SurfaceBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { Content = text, Height = 380, Padding = new Thickness(14, 10, 10, 10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
        CheckBox? agree = null;
        if (requireAcceptance)
        {
            agree = new CheckBox { Content = "I have read and agree to the VYTRIX Terms of Agreement", Margin = new Thickness(0, 14, 0, 0) };
            body.Children.Add(agree);
        }
        DialogCard.MaxWidth = 660;
        var task = requireAcceptance
            ? ConfirmAsync($"VYTRIX Terms of Agreement (v{Terms.Version})", body, "Accept", cancelText: "Decline")
            : ConfirmAsync($"VYTRIX Terms of Agreement (v{Terms.Version})", body, "Close", showCancel: false);
        if (agree is not null)
        {
            DialogOk.IsEnabled = false;
            agree.Checked += (_, _) => DialogOk.IsEnabled = true;
            agree.Unchecked += (_, _) => DialogOk.IsEnabled = false;
        }
        var ok = await task;
        return requireAcceptance ? ok && agree!.IsChecked == true : ok;
    }

    private void CloseDialog(bool result)
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        DialogBody.Content = null;
        DialogOk.IsEnabled = true;
        _dialogOpen = false;
        UpdateScene();
        DialogCard.MaxWidth = 560;
        var d = _dialog;
        _dialog = null;
        d?.TrySetResult(result);
    }

    private void DialogOk_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
    private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);

    // ───────────────────────── Toasts

    public void Toast(string message, ToastKind kind = ToastKind.Info)
    {
        var (icon, brush) = kind switch
        {
            ToastKind.Success => ("IcoCheck", "GoodBrush"),
            ToastKind.Warning => ("IcoAlert", "WarnBrush"),
            ToastKind.Error => ("IcoAlert", "BadBrush"),
            _ => ("IcoInfo", "AccentHoverBrush"),
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var ico = Ui.IconPath(icon, 16, brush);
        ico.VerticalAlignment = VerticalAlignment.Top;
        ico.Margin = new Thickness(0, 1, 12, 0);
        g.Children.Add(ico);
        var tb = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        Grid.SetColumn(tb, 1);
        g.Children.Add(tb);
        // Coloured edge on the left says at a glance whether it's good news or a problem.
        var shell = new Grid();
        shell.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Res(brush), CornerRadius = new CornerRadius(10, 0, 0, 10) });
        g.Margin = new Thickness(16, 12, 14, 12);
        shell.Children.Add(g);
        var card = new Border
        {
            Background = Ui.Res("Surface3Brush"), BorderBrush = Ui.Res("BorderStrongBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 8, 0, 0), Child = shell, ClipToBounds = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black },
            Cursor = Cursors.Hand, ToolTip = "Click to dismiss",
        };
        card.MouseLeftButtonUp += (_, _) => ToastLayer.Children.Remove(card);
        ToastLayer.Children.Add(card);
        while (ToastLayer.Children.Count > 4) ToastLayer.Children.RemoveAt(0);
        if (!Settings.Current.ReduceMotion)
        {
            // Slide in from the right with a soft ease.
            var slide = new TranslateTransform(36, 0);
            card.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 9 : 5) };
        timer.Tick += (_, _) => { timer.Stop(); ToastLayer.Children.Remove(card); };
        timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        CarolPlayer.Instance.Stop();
        _current?.OnHidden();
        base.OnClosed(e);
    }
}
