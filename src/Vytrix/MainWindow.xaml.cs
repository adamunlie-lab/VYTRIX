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
        ];
        BuildNav();
        UpdateAdminState();
        BuildScene();
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

    private SakuraTree? _tree;
    private PetalField? _petals;

    private void BuildScene()
    {
        _tree = new SakuraTree();
        SceneHost.Children.Add(new Viewbox
        {
            Child = _tree, Stretch = Stretch.Uniform, Width = 700, Height = 700 * SakuraTree.DesignHeight / SakuraTree.DesignWidth,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -30, -8),
        });
        _petals = new PetalField();
        PetalHost.Children.Add(_petals);

        ThemeManager.Changed += UpdateScene;
        Settings.Changed += UpdateScene;
        StateChanged += (_, _) => UpdateScene();
        Activated += (_, _) => UpdateScene();
        Deactivated += (_, _) => UpdateScene();
        Loaded += (_, _) => UpdateScene();
    }

    /// <summary>
    /// Shows the scene only in the Cherry Blossom theme, and animates it only while VYTRIX
    /// is the active, visible window and Reduce motion is off, so it costs nothing in the background.
    /// </summary>
    /// <summary>Raised with whether theme scenery may animate right now (Overview's hero tree listens).</summary>
    public static event Action<bool>? SceneAnimationChanged;
    public static bool SceneAnimating { get; private set; }

    private void UpdateScene()
    {
        if (_tree is null || _petals is null) return;
        bool show = ThemeManager.Current == ThemeManager.CherryBlossom;
        // The Overview has its own hero tree, so the background tree steps aside there.
        bool overview = _current is OverviewPage;
        SceneHost.Visibility = show && !overview ? Visibility.Visible : Visibility.Collapsed;
        PetalHost.Visibility = show && Settings.Current.ShowFallingPetals ? Visibility.Visible : Visibility.Collapsed;
        bool animate = show && IsActive && WindowState != WindowState.Minimized && !Settings.Current.ReduceMotion;
        _tree.SetRunning(animate && !overview);
        _petals.SetRunning(animate && Settings.Current.ShowFallingPetals);
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
        var card = new Border
        {
            Background = Ui.Res("Surface3Brush"), BorderBrush = Ui.Res("BorderStrongBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0), Child = g,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
            Cursor = Cursors.Hand, ToolTip = "Click to dismiss",
        };
        card.MouseLeftButtonUp += (_, _) => ToastLayer.Children.Remove(card);
        ToastLayer.Children.Add(card);
        while (ToastLayer.Children.Count > 4) ToastLayer.Children.RemoveAt(0);
        Kit.FadeIn(card);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 9 : 5) };
        timer.Tick += (_, _) => { timer.Stop(); ToastLayer.Children.Remove(card); };
        timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _current?.OnHidden();
        base.OnClosed(e);
    }
}
