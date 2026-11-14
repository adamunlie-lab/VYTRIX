using System.Windows;
using System.Windows.Controls;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Native;
using Vytrix.Services;

namespace Vytrix.Views;

public sealed class OptimizationPage : PageBase
{
    public override string PageTitle => "Optimization";
    public override string PageSubtitle => "Profiles, recommendations and background activity. Every change is previewed first and can be undone.";

    private readonly ResponsiveGrid _profiles = Kit.Grid(250, 5);
    private readonly StackPanel _recs = new();
    private readonly StackPanel _bg = new();
    private readonly StackPanel _custom = new();

    protected override void Build()
    {
        Body.Children.Add(Kit.Section("Optimization profiles"));
        Body.Children.Add(_profiles);

        Body.Children.Add(Kit.Section("Custom profile"));
        Body.Children.Add(Kit.Card(_custom, "Build your own profile", "Pick exactly which tweaks and power plan your Custom profile applies. Saved automatically."));

        Body.Children.Add(Kit.Section("Recommendations"));
        Body.Children.Add(Kit.Card(_recs, "Based on this PC", "Generated from your current configuration. Nothing is changed until you act."));

        Body.Children.Add(Kit.Section("Background activity"));
        var grid = Kit.Grid(420, 2);
        var refresh = Ui.IconButton("IcoRefresh", "Refresh", "Ghost");
        refresh.Click += (_, _) => _ = LoadBackgroundAsync();
        grid.Children.Add(Kit.Card(_bg, "Background apps", "Apps running without a window in your session. Windows and security components are excluded.", refresh));

        var perms = new StackPanel();
        if (TweakCatalog.ById("perf.backgroundapps") is { } bgTweak) perms.Children.Add(new TweakView(bgTweak, compact: true) { Margin = new Thickness(0, 0, 0, 12) });
        perms.Children.Add(new TextBlock { Text = "Windows 11 manages background permission per app. Open an app's Advanced options to choose Always, Power optimized or Never.", Style = Ui.Style("Dim"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10) });
        var open = Ui.IconButton("IcoApps", "Open Installed apps settings", "Ghost");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += (_, _) => SysCmd.Open("ms-settings:appsfeatures");
        perms.Children.Add(open);
        perms.Children.Add(new TextBlock { Text = "VISUAL EFFECTS", Style = Ui.Style("Label"), Margin = new Thickness(0, 18, 0, 8) });
        foreach (var t in TweakCatalog.All.Where(t => t.Category == TweakCategory.VisualEffects))
            perms.Children.Add(new TweakView(t, compact: true) { Margin = new Thickness(0, 0, 0, 8) });
        var perf = Ui.IconButton("IcoSettings", "Open Windows Performance Options", "Ghost");
        perf.HorizontalAlignment = HorizontalAlignment.Left;
        perf.Click += (_, _) => SysCmd.Open(SysCmd.SystemExe("SystemPropertiesPerformance.exe"));
        perms.Children.Add(perf);
        grid.Children.Add(Kit.Card(perms, "Background permissions & visual effects"));
        Body.Children.Add(grid);
    }

    public override void OnShown()
    {
        BuildProfiles();
        BuildCustom();
        _ = BuildRecommendationsAsync();
        _ = LoadBackgroundAsync();
    }

    // ───────────────────────── Profiles

    private void BuildProfiles()
    {
        _profiles.Children.Clear();
        foreach (var p in Profiles.All.Append(Profiles.Custom))
        {
            bool active = Settings.Current.ActiveProfile == p.Id;
            var sp = new StackPanel();
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = p.Name, Style = Ui.Style("H2") });
            if (active) { var b = Ui.Badge("Active", "AccentHoverBrush", "AccentSoftBrush"); b.HorizontalAlignment = HorizontalAlignment.Right; head.Children.Add(b); }
            sp.Children.Add(head);
            sp.Children.Add(new TextBlock { Text = p.Tagline, Foreground = Ui.Res("AccentHoverBrush"), FontSize = 12, Margin = new Thickness(0, 2, 0, 8) });
            sp.Children.Add(new TextBlock { Text = p.Description, Style = Ui.Style("Dim"), FontSize = 12.5, MinHeight = 72 });
            sp.Children.Add(new TextBlock { Text = $"{p.TweakIds.Count} settings · power plan: {PlanLabel(p.PowerPlan)}", Style = Ui.Style("Muted"), Margin = new Thickness(0, 10, 0, 12) });
            var review = new Button { Content = "Review & apply", Style = Ui.Style(active ? "Ghost" : "Primary") };
            review.IsEnabled = p.TweakIds.Count > 0 || p.PowerPlan != "keep";
            review.Click += async (_, _) => await ReviewAndApply(p, review);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(review);
            if (Journal.All.Any(r => r.Kind == ChangeKind.Profile && r.Target == p.Id))
            {
                var undo = new Button { Content = "Revert", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Restore every setting this profile changed" };
                undo.Click += async (_, _) =>
                {
                    if (!await Shell.ConfirmAsync($"Revert the {p.Name} profile?", "VYTRIX restores the previous value of every setting this profile changed, and switches back to the power plan you had before.", "Revert")) return;
                    await Kit.RunAsync(undo, () => Profiles.RevertAsync(p));
                    BuildProfiles();
                };
                row.Children.Add(undo);
            }
            sp.Children.Add(row);
            var card = new Border { Style = Ui.Style("Card"), Child = sp };
            if (active) card.BorderBrush = Ui.Res("AccentBrush");
            _profiles.Children.Add(card);
        }
    }

    private static string PlanLabel(string p) => p switch { "performance" => "High performance", "balanced" => "Balanced", _ => "unchanged" };

    private async Task ReviewAndApply(OptProfile p, Button trigger)
    {
        var items = await Task.Run(() => Profiles.Preview(p));
        var plan = await Task.Run(() => Profiles.TargetPlan(p));
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = p.Description, Style = Ui.Style("Dim"), Margin = new Thickness(0, 0, 0, 12) });
        var list = new StackPanel();
        foreach (var i in items)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel();
            name.Children.Add(new TextBlock { Text = i.Tweak.Name, FontSize = 12.5 });
            name.Children.Add(new TextBlock { Text = i.Tweak.ExpectedEffect, Style = Ui.Style("Muted"), FontSize = 11.5, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left });
            g.Children.Add(name);
            var badge = i.Action == "Will apply" ? Ui.Badge("Will apply", "AccentHoverBrush", "AccentSoftBrush")
                : i.Action == "Already set" ? Ui.Badge("Already set", "GoodBrush", "GoodSoftBrush")
                : Ui.Badge("Skipped", "TextMutedBrush", "Surface3Brush");
            badge.ToolTip = i.Action;
            badge.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(badge, 1);
            g.Children.Add(badge);
            list.Children.Add(g);
        }
        var pg = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        pg.Children.Add(new TextBlock { Text = "Power plan", FontSize = 12.5 });
        var current = PowerService.Active;
        string planText = p.PowerPlan == "keep" ? "Unchanged"
            : plan is null ? "No High performance plan — add it on the Gaming page"
            : plan.Guid == current?.Guid ? $"{plan.Name} (already active)" : $"{current?.Name} → {plan.Name}";
        pg.Children.Add(new TextBlock { Text = planText, Style = Ui.Style("Muted"), HorizontalAlignment = HorizontalAlignment.Right });
        list.Children.Add(pg);
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        int willApply = items.Count(i => i.Action == "Will apply");
        if (items.Any(i => i.Action.Contains("administrator")))
            body.Children.Add(Kit.Notice("Some settings are skipped because VYTRIX is not running as administrator.", "warn"));
        body.Children.Add(new TextBlock
        {
            Text = Settings.Current.CreateRestorePointBeforeProfiles && Elevation.IsAdmin
                ? "A System Restore point is created first. Each change is also journaled so it can be reverted individually."
                : "Each change is journaled so it can be reverted from the Restore Center.",
            Style = Ui.Style("Muted"), Margin = new Thickness(0, 10, 0, 0),
        });
        if (!await Shell.ConfirmAsync($"Apply {p.Name} profile — {willApply} change(s)", body, "Apply profile")) return;

        await Kit.RunAsync(trigger, () => Profiles.ApplyAsync(p, new Progress<string>(s => Log.Info("profile", s))));
        BuildProfiles();
        _ = BuildRecommendationsAsync();
    }

    private void BuildCustom()
    {
        _custom.Children.Clear();
        var sel = Settings.Current.CustomProfileTweaks;
        var grid = Kit.Grid(260, 3, 8);
        foreach (var t in TweakCatalog.All)
        {
            var cb = new CheckBox { Content = t.Name, IsChecked = sel.Contains(t.Id), ToolTip = $"{t.CategoryLabel} · {t.RiskLabel}\n{t.Description}", Margin = new Thickness(0, 3, 0, 3) };
            if (t.UnsupportedReason is { } why) { cb.IsEnabled = false; cb.ToolTip = why; }
            cb.Checked += (_, _) => { if (!sel.Contains(t.Id)) sel.Add(t.Id); Settings.Save(); BuildProfiles(); };
            cb.Unchecked += (_, _) => { sel.Remove(t.Id); Settings.Save(); BuildProfiles(); };
            grid.Children.Add(cb);
        }
        _custom.Children.Add(grid);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Power plan", Style = Ui.Style("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var combo = new ComboBox { Width = 200 };
        foreach (var (id, label) in new[] { ("keep", "Leave unchanged"), ("balanced", "Balanced"), ("performance", "High performance") })
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            if (Settings.Current.CustomProfilePowerPlan == id) combo.SelectedIndex = combo.Items.Count - 1;
        }
        combo.SelectionChanged += (_, _) => { Settings.Current.CustomProfilePowerPlan = (string)((ComboBoxItem)combo.SelectedItem).Tag; Settings.Save(); BuildProfiles(); };
        row.Children.Add(combo);
        _custom.Children.Add(row);
    }

    // ───────────────────────── Recommendations

    private async Task BuildRecommendationsAsync()
    {
        var recs = await Task.Run(() =>
        {
            var list = new List<(string title, string why, string kind, string? tweakId, string? page)>();
            void FromTweak(string id, string why)
            {
                if (TweakCatalog.ById(id) is { } t && t.GetState() == TweakState.NotApplied) list.Add((t.Name, why, "tweak", id, null));
            }
            FromTweak("gaming.gamemode", "Game Mode is off. It is on by default and has no downside outside games.");
            if (SystemInfo.Gpus.Any(g => g.HagsSupported == 1 && g.HagsEnabled == 0))
                FromTweak("gaming.hags", "Your GPU supports hardware-accelerated GPU scheduling but it is off.");
            FromTweak("gaming.gamedvr", "Background game recording is on and uses your GPU's video encoder during games.");
            FromTweak("win.fileext", "File extensions are hidden, which makes disguised files like report.pdf.exe harder to spot.");
            var startup = StartupService.EnabledCount();
            if (startup > 8) list.Add(("Review startup apps", $"{startup} apps start with Windows. Disabling ones you don't need speeds up sign-in.", "page", null, "startup"));
            var sys = SystemInfo.Volumes().FirstOrDefault(v => v.Name.StartsWith(Environment.SystemDirectory[..1], StringComparison.OrdinalIgnoreCase));
            if (sys is not null && 100 - sys.UsedPct < 15) list.Add(("Free up space on the system drive", $"Only {100 - sys.UsedPct:0}% free. Windows Update needs room, and SSDs slow down when nearly full.", "page", null, "storage"));
            var plan = PowerService.Active;
            if (plan?.Guid == PowerService.PowerSaver && !SystemInfo.IsLaptop) list.Add(("Switch from Power saver", "This desktop is using Power saver, which limits CPU performance.", "page", null, "gaming"));
            if (TimeSpan.FromMilliseconds(VxNative.vx_get_uptime_ms()).TotalDays > 7) list.Add(("Restart your PC", "It has been running for over a week. A restart finishes pending updates and clears leaked resources.", "info", null, null));
            return list;
        });

        _recs.Children.Clear();
        if (recs.Count == 0)
        {
            _recs.Children.Add(Kit.EmptyState("IcoCheck", "Nothing to recommend", "Your configuration already follows every recommendation VYTRIX checks for."));
            return;
        }
        foreach (var r in recs)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ico = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Ui.Res("AccentSoftBrush"), Child = Ui.IconPath("IcoOptimize", 14, "AccentHoverBrush"), VerticalAlignment = VerticalAlignment.Top };
            g.Children.Add(ico);
            var sp = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
            sp.Children.Add(new TextBlock { Text = r.title, FontSize = 13 });
            sp.Children.Add(new TextBlock { Text = r.why, Style = Ui.Style("Muted") });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            if (r.kind != "info")
            {
                var b = new Button { Content = r.kind == "tweak" ? "Review" : "Open", Style = Ui.Style("Ghost"), VerticalAlignment = VerticalAlignment.Center };
                var (tid, page) = (r.tweakId, r.page);
                b.Click += (_, _) =>
                {
                    if (tid is not null) Shell.Navigate("tweaks", p => ((TweaksPage)p).Focus(tid));
                    else if (page is not null) Shell.Navigate(page);
                };
                Grid.SetColumn(b, 2);
                g.Children.Add(b);
            }
            _recs.Children.Add(g);
        }
    }

    // ───────────────────────── Background apps

    private async Task LoadBackgroundAsync()
    {
        _bg.Children.Clear();
        _bg.Children.Add(new Spinner { Width = 22, Height = 22, Stroke = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        var procs = await Task.Run(() => ProcessService.Snapshot(withPublisher: true)
            .Where(p => p.Class == ProcClass.Background && !p.Protected)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Items: g.ToList(), Mem: g.Sum(x => (double)x.WorkingSet), Publisher: g.First().Publisher))
            .OrderByDescending(x => x.Mem).Take(15).ToList());
        _bg.Children.Clear();
        if (procs.Count == 0)
        {
            _bg.Children.Add(Kit.EmptyState("IcoCheck", "No background apps", "Only Windows components are running in the background."));
            return;
        }
        foreach (var p in procs)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = p.Items.Count > 1 ? $"{p.Name}  ×{p.Items.Count}" : p.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(p.Publisher) ? "Unknown publisher" : p.Publisher, Style = Ui.Style("Muted"), FontSize = 11 });
            g.Children.Add(sp);
            var mem = new TextBlock { Text = Fmt.Bytes(p.Mem), Style = Ui.Style("Dim"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(mem, 1);
            g.Children.Add(mem);
            var end = new Button { Content = "End", Style = Ui.Style("Ghost"), Padding = new Thickness(12, 4, 12, 4) };
            end.Click += async (_, _) =>
            {
                if (!await Shell.ConfirmAsync($"End {p.Name}?", $"{p.Items.Count} process(es) will be closed. Unsaved data in this app may be lost. It will start again next time it is launched or at sign-in if it's a startup app.", "End process", danger: true)) return;
                await Kit.RunAsync(end, async () =>
                {
                    var results = new List<(bool, string)>();
                    foreach (var item in p.Items) results.Add(await ProcessService.EndAsync(item, force: true));
                    return results.All(r => r.Item1) ? (true, $"{p.Name} ended.") : results.First(r => !r.Item1);
                });
                await LoadBackgroundAsync();
            };
            Grid.SetColumn(end, 2);
            g.Children.Add(end);
            _bg.Children.Add(g);
        }
    }
}
