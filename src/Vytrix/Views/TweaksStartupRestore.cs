using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

public sealed class TweaksPage : PageBase
{
    public override string PageTitle => "Tweak Library";
    public override string PageSubtitle => "Documented Windows settings only, organised by the part of your PC they affect — each with its real effect, risk, compatibility and one-click restore.";

    private sealed class Section
    {
        public TweakComponent Component;
        public FrameworkElement Root = null!;
        public TextBlock Summary = null!;
        public ResponsiveGrid Grid = null!;
        public List<TweakView> Views = [];
    }

    private readonly List<TweakView> _views = [];
    private readonly List<Section> _sections = [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = new() { Style = Ui.Style("Muted"), Margin = new Thickness(0, 0, 0, 4) };
    private readonly List<RadioButton> _resetChips = [];
    private string _query = "";
    private TweakComponent? _component;
    private string _stateFilter = "All";
    private TextBox _search = null!;
    private Border? _empty;

    protected override void Build()
    {
        _search = Kit.Search("Search tweaks…", q => { _query = q; Filter(); }, 320);
        HeaderActions.Children.Add(_search);

        var chips = new WrapPanel();
        var all = new RadioButton { Content = $"All ({TweakCatalog.All.Count})", Style = Ui.Style("Chip"), GroupName = "tcomp", IsChecked = true };
        all.Checked += (_, _) => { _component = null; Filter(); };
        chips.Children.Add(all);
        _resetChips.Add(all);
        foreach (var c in Components.All)
        {
            int n = TweakCatalog.All.Count(t => t.Component == c.Id);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Ui.IconPath(c.Icon, 13));
            content.Children.Add(new TextBlock { Text = $"{c.Label} ({n})", Margin = new Thickness(7, 0, 0, 0) });
            var rb = new RadioButton { Content = content, Style = Ui.Style("Chip"), GroupName = "tcomp", ToolTip = c.Blurb };
            var id = c.Id;
            rb.Checked += (_, _) => { _component = id; Filter(); };
            chips.Children.Add(rb);
        }
        var states = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        foreach (var s in new[] { "All", "Applied", "Not applied", "Unsupported" })
        {
            var rb = new RadioButton { Content = s == "All" ? "Any state" : s, Style = Ui.Style("Chip"), GroupName = "tstate", IsChecked = s == "All" };
            rb.Checked += (_, _) => { _stateFilter = s; Filter(); };
            states.Children.Add(rb);
            if (s == "All") _resetChips.Add(rb);
        }
        Body.Children.Add(chips);
        Body.Children.Add(states);
        Body.Children.Add(_count);
        Body.Children.Add(_list);

        foreach (var c in Components.All)
        {
            var sec = new Section { Component = c.Id };
            var root = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Ui.Res("AccentSoftBrush"), Child = Ui.IconPath(c.Icon, 16, "AccentHoverBrush") });
            var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var tl = new StackPanel { Orientation = Orientation.Horizontal };
            tl.Children.Add(new TextBlock { Text = c.Label, Style = Ui.Style("H2"), FontSize = 16 });
            sec.Summary = new TextBlock { Style = Ui.Style("Muted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 1) };
            tl.Children.Add(sec.Summary);
            titles.Children.Add(tl);
            titles.Children.Add(new TextBlock { Text = c.Blurb, Style = Ui.Style("Muted") });
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            root.Children.Add(head);

            sec.Grid = Kit.Grid(440, 2, 10);
            foreach (var t in TweakCatalog.All.Where(t => t.Component == c.Id).OrderBy(t => t.Risk).ThenBy(t => t.Name))
            {
                var v = new TweakView(t);
                sec.Views.Add(v);
                _views.Add(v);
                sec.Grid.Children.Add(v);
            }
            if (sec.Views.Count > 0) root.Children.Add(sec.Grid);
            else root.Children.Add(NoTweaksNote(c.Id));
            sec.Root = root;
            _sections.Add(sec);
            _list.Children.Add(root);
        }
        _empty = Kit.EmptyState("IcoSearch", "No tweaks match", "Try a different search term, component or state.");
        _list.Children.Add(_empty);
        Filter();
    }

    /// <summary>Explains, honestly, why a component has no tweaks.</summary>
    private Border NoTweaksNote(TweakComponent c)
    {
        string text = c switch
        {
            TweakComponent.Memory => "There are no memory tweaks, by design. Windows already uses spare RAM as a cache and frees it instantly when an app needs it. \"RAM cleaners\", standby-list purges and paging-file tricks make things slower, not faster. The Memory page shows what is really using your RAM.",
            _ => "No documented, measurable tweaks are available for this area.",
        };
        var open = new Button { Content = "Open Memory page", Style = Ui.Style("Ghost"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => Shell.Navigate("memory");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = text, Style = Ui.Style("Dim"), FontSize = 12.5 });
        if (c == TweakComponent.Memory) sp.Children.Add(open);
        return new Border { Style = Ui.Style("Card"), Child = sp };
    }

    public override void OnShown() { foreach (var v in _views) v.Sync(); Filter(); }

    private bool Matches(Tweak t, TweakState st) =>
        (_stateFilter == "All" || (_stateFilter == "Applied" && st == TweakState.Applied) || (_stateFilter == "Not applied" && st is TweakState.NotApplied or TweakState.Partial) || (_stateFilter == "Unsupported" && st == TweakState.Unsupported))
        && (_query.Length == 0 || t.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(_query, StringComparison.OrdinalIgnoreCase)
            || t.Explanation.Contains(_query, StringComparison.OrdinalIgnoreCase) || t.ComponentLabel.Contains(_query, StringComparison.OrdinalIgnoreCase));

    private void Filter()
    {
        int shown = 0;
        bool unfiltered = _query.Length == 0 && _stateFilter == "All";
        foreach (var sec in _sections)
        {
            bool inScope = _component is null || _component == sec.Component;
            int visible = 0, applied = 0;
            foreach (var v in sec.Views)
            {
                var st = v.Tweak.GetState();
                if (st == TweakState.Applied) applied++;
                bool ok = inScope && Matches(v.Tweak, st);
                v.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
                if (ok) visible++;
            }
            shown += visible;
            sec.Summary.Text = sec.Views.Count == 0 ? "no tweaks" : $"{sec.Views.Count} tweak{(sec.Views.Count == 1 ? "" : "s")} · {applied} applied";
            // A component with no tweaks still shows its explanation when browsing, not when searching.
            bool showSection = inScope && (visible > 0 || (sec.Views.Count == 0 && unfiltered));
            sec.Root.Visibility = showSection ? Visibility.Visible : Visibility.Collapsed;
            sec.Grid.InvalidateMeasure();
        }
        _count.Text = $"{shown} of {_views.Count} tweaks shown";
        bool anySection = _sections.Any(s => s.Root.Visibility == Visibility.Visible);
        if (_empty is not null) _empty.Visibility = anySection ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Scrolls to a tweak and expands its details (used by search and recommendations).</summary>
    public void Focus(string id)
    {
        _search.Text = "";
        foreach (var rb in _resetChips) rb.IsChecked = true;
        var v = _views.FirstOrDefault(x => x.Tweak.Id == id);
        if (v is null) return;
        v.ExpandDetails();
        Dispatcher.BeginInvoke(() => v.BringIntoView(new Rect(0, 0, 10, 200)), System.Windows.Threading.DispatcherPriority.Loaded);
        v.BorderBrush = Ui.Res("AccentBrush");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) => { timer.Stop(); v.BorderBrush = Ui.Res("BorderBrush"); };
        timer.Start();
    }
}

public sealed class StartupPage : PageBase
{
    public override string PageTitle => "Startup";
    public override string PageSubtitle => "Programs that launch when you sign in. Disabling works exactly like Task Manager: the entry stays, it just doesn't run.";

    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = new() { Style = Ui.Style("Muted"), Margin = new Thickness(0, 0, 0, 12) };

    protected override void Build()
    {
        var refresh = Ui.IconButton("IcoRefresh", "Refresh", "Ghost");
        refresh.Click += (_, _) => _ = LoadAsync();
        var tm = Ui.IconButton("IcoList", "Task Manager", "Ghost");
        tm.Margin = new Thickness(8, 0, 0, 0);
        tm.ToolTip = "Opens Task Manager's Startup apps page, which shows Windows' measured startup impact.";
        tm.Click += (_, _) => SysCmd.Open(SysCmd.SystemExe("taskmgr.exe"));
        HeaderActions.Children.Add(refresh);
        HeaderActions.Children.Add(tm);
        var bootCard = Kit.Card(_boot, "Boot times", "How long your last few startups took. Disable apps you don't need below, then compare after your next restart.");
        bootCard.Margin = new Thickness(0, 0, 0, 16);
        Body.Children.Add(bootCard);
        if (TweakCatalog.ById("startup.delay") is { } d) Body.Children.Add(new TweakView(d, compact: true) { Margin = new Thickness(0, 0, 0, 16) });
        Body.Children.Add(_summary);
        Body.Children.Add(Kit.Card(_list));
    }

    private readonly StackPanel _boot = new();

    public override void OnShown() { _ = LoadAsync(); _ = LoadBootAsync(); }

    private async Task LoadBootAsync()
    {
        _boot.Children.Clear();
        _boot.Children.Add(new Spinner { Width = 20, Height = 20, Stroke = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        var (boots, slow) = await Task.Run(() => (BootTime.Recent(8), BootTime.SlowItems()));
        _boot.Children.Clear();
        if (boots.Count == 0) { _boot.Children.Add(new TextBlock { Text = "Windows hasn't recorded any recent startups that VYTRIX can read.", Style = Ui.Style("Muted") }); return; }

        bool measured = boots[0].Measured;
        var latest = boots[0];
        var older = boots.Skip(1).Select(b => b.Seconds).ToList();
        var headline = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        headline.Inlines.Add(new System.Windows.Documents.Run($"Last startup: {latest.Seconds:0.0} s") { FontWeight = FontWeights.SemiBold });
        if (older.Count > 0)
        {
            var avg = older.Average();
            var diff = latest.Seconds - avg;
            headline.Inlines.Add(new System.Windows.Documents.Run(Math.Abs(diff) < 1
                ? $"  ·  about the same as your average of {avg:0.0} s"
                : $"  ·  {Math.Abs(diff):0.0} s {(diff < 0 ? "faster" : "slower")} than your average of {avg:0.0} s")
            { Foreground = Ui.Res(diff < -1 ? "GoodBrush" : diff > 1 ? "WarnBrush" : "TextDimBrush") });
        }
        _boot.Children.Add(headline);

        foreach (var b in boots)
        {
            var detail = b.DesktopReadySeconds is { } dr && b.BackgroundSeconds is { } bg ? $"desktop ready {dr:0.0} s, then {bg:0.0} s finishing in the background" : "";
            _boot.Children.Add(Kit.KeyValue(b.When.ToString("ddd d MMM, HH:mm"), $"{b.Seconds:0.0} s" + (detail.Length > 0 ? $"   ({detail})" : "")));
        }

        if (measured && slow.Count > 0)
        {
            _boot.Children.Add(new TextBlock { Text = "SLOWING STARTUP (REPORTED BY WINDOWS)", Style = Ui.Style("Label"), Margin = new Thickness(0, 14, 0, 6) });
            foreach (var s in slow)
                _boot.Children.Add(Kit.KeyValue($"{s.Kind}", $"{s.Name}: added {s.DelaySeconds:0.0} s on {s.When:d MMM}"));
        }
        _boot.Children.Add(new TextBlock
        {
            Text = measured
                ? "Measured by Windows from power-on until the desktop is ready and startup work is finished."
                : "From power-on until you signed in, so it includes the time spent at the sign-in screen. In administrator mode VYTRIX shows Windows' own measurement and what's slowing startup.",
            Style = Ui.Style("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });
    }

    private async Task LoadAsync()
    {
        _list.Children.Clear();
        _list.Children.Add(new Spinner { Width = 22, Height = 22, Stroke = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        List<StartupItem> items;
        try { items = await Task.Run(StartupService.List); }
        catch (Exception ex)
        {
            _list.Children.Clear();
            _list.Children.Add(Kit.EmptyState("IcoAlert", "Couldn't read startup entries", ex.Message));
            return;
        }
        _list.Children.Clear();
        _summary.Text = $"{items.Count(i => i.Enabled)} enabled · {items.Count(i => !i.Enabled)} disabled · Store-app startup tasks and scheduled tasks are managed in Settings → Apps → Startup.";
        if (items.Count == 0) { _list.Children.Add(Kit.EmptyState("IcoStartup", "No startup apps", "Nothing is configured to start when you sign in.")); return; }
        foreach (var i in items)
        {
            var g = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new TextBlock { Text = i.Name, FontSize = 13 });
            if (i.TargetMissing) { var b = Ui.Badge("File missing", "WarnBrush", "WarnSoftBrush"); b.Margin = new Thickness(8, 0, 0, 0); b.ToolTip = "The program this entry points to no longer exists. Disabling it is safe."; name.Children.Add(b); }
            if (i.NeedsAdmin) { var b = Ui.Badge("All users", "TextDimBrush", "Surface3Brush"); b.Margin = new Thickness(8, 0, 0, 0); b.ToolTip = "Changing this entry requires administrator mode."; name.Children.Add(b); }
            sp.Children.Add(name);
            sp.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(i.Publisher) ? "Unknown publisher" : i.Publisher, Style = Ui.Style("Muted") });
            sp.Children.Add(new TextBlock { Text = i.Command, Style = Ui.Style("Muted"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, ToolTip = i.Command });
            g.Children.Add(sp);
            var src = new TextBlock { Text = i.SourceLabel, Style = Ui.Style("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            Grid.SetColumn(src, 1); g.Children.Add(src);
            var sw = Kit.Switch(i.Enabled, i.Enabled ? "Starts with Windows" : "Disabled");
            sw.IsEnabled = !i.NeedsAdmin || Elevation.IsAdmin;
            bool syncing = false;
            async Task Change(bool on)
            {
                if (syncing) return;
                var (ok, msg) = await StartupService.SetEnabledAsync(i, on);
                Shell.Toast(msg, ok ? ToastKind.Success : ToastKind.Error);
                if (!ok) { syncing = true; sw.IsChecked = !on; syncing = false; }
                _summary.Text = $"{items.Count(x => x.Enabled)} enabled · {items.Count(x => !x.Enabled)} disabled";
            }
            sw.Checked += async (_, _) => await Change(true);
            sw.Unchecked += async (_, _) => await Change(false);
            Grid.SetColumn(sw, 2); g.Children.Add(sw);
            _list.Children.Add(g);
            _list.Children.Add(new Border { Height = 1, Background = Ui.Res("BorderBrush") });
        }
        if (_list.Children.Count > 0) _list.Children.RemoveAt(_list.Children.Count - 1);
    }
}

public sealed class RestorePage : PageBase
{
    public override string PageTitle => "Restore Center";
    public override string PageSubtitle => "Every change VYTRIX makes is recorded with its previous value. Roll back one change, or everything, at any time.";

    private readonly StackPanel _points = new();
    private readonly StackPanel _journal = new();
    private readonly StackPanel _log = new();
    private bool _showRolledBack;

    protected override void Build()
    {
        var g = Kit.Grid(400, 2);
        var create = Ui.IconButton("IcoShield", "Create restore point", "Primary");
        create.IsEnabled = Elevation.IsAdmin;
        create.ToolTip = Elevation.IsAdmin ? null : "Requires administrator mode";
        create.Click += async (_, _) => { await Kit.RunAsync(create, () => RestorePoints.CreateAsync($"VYTRIX manual checkpoint {DateTime.Now:g}")); _ = LoadPointsAsync(); };
        g.Children.Add(Kit.Card(_points, "System Restore points", "Windows snapshots of system files and registry. Use them if something goes wrong outside VYTRIX's own rollback.", create));

        var safety = new StackPanel();
        safety.Children.Add(Kit.KeyValue("Change journal", "Every change records the exact previous registry value, DNS servers or power plan."));
        safety.Children.Add(Kit.KeyValue("Registry backups", $".reg exports before every registry change"));
        safety.Children.Add(Kit.KeyValue("Backup folder", AppPaths.Backups));
        safety.Children.Add(Kit.KeyValue("Action logs", AppPaths.Logs));
        safety.Children.Add(Kit.KeyValue("Protected", "Windows Defender, Firewall, Windows Update and core services are never modified."));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var ob = new Button { Content = "Open backups", Style = Ui.Style("Ghost") };
        ob.Click += (_, _) => SysCmd.Open(AppPaths.Backups);
        var ol = new Button { Content = "Open logs", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        ol.Click += (_, _) => SysCmd.Open(AppPaths.Logs);
        var sr = new Button { Content = "Open System Restore", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        sr.Click += (_, _) => RestorePoints.OpenSystemRestore();
        row.Children.Add(ob); row.Children.Add(ol); row.Children.Add(sr);
        safety.Children.Add(row);
        g.Children.Add(Kit.Card(safety, "Safety system"));
        Body.Children.Add(g);

        Body.Children.Add(Kit.Section("Change history"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var showAll = new CheckBox { Content = "Show rolled-back", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        showAll.Checked += (_, _) => { _showRolledBack = true; RenderJournal(); };
        showAll.Unchecked += (_, _) => { _showRolledBack = false; RenderJournal(); };
        var all = Ui.IconButton("IcoUndo", "Roll back everything", "Danger");
        all.Click += async (_, _) => await RollbackAll(all);
        actions.Children.Add(showAll);
        actions.Children.Add(all);
        Body.Children.Add(Kit.Card(_journal, "Changes made by VYTRIX", "Newest first", actions));

        Body.Children.Add(Kit.Section("Activity log"));
        Body.Children.Add(Kit.Card(_log, "Today's log", "Structured log of everything VYTRIX did, including errors"));
        Journal.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RenderJournal(); });
    }

    public override void OnShown()
    {
        _ = LoadPointsAsync();
        RenderJournal();
        RenderLog();
    }

    private async Task LoadPointsAsync()
    {
        _points.Children.Clear();
        _points.Children.Add(new Spinner { Width = 20, Height = 20, Stroke = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        var pts = await Task.Run(RestorePoints.List);
        _points.Children.Clear();
        if (!Elevation.IsAdmin) _points.Children.Add(Kit.Notice("Listing and creating restore points requires administrator mode.", "info"));
        if (pts.Count == 0) { _points.Children.Add(Kit.EmptyState("IcoShield", "No restore points found", Elevation.IsAdmin ? "System Protection may be off for your system drive. Turn it on in System Properties → System Protection." : "Relaunch as administrator to view restore points.")); return; }
        foreach (var p in pts.Take(8)) _points.Children.Add(Kit.KeyValue(p.Created.ToString("g"), p.Description));
    }

    private void RenderJournal()
    {
        _journal.Children.Clear();
        var records = Journal.All.Where(r => _showRolledBack || !r.RolledBack).ToList();
        if (records.Count == 0) { _journal.Children.Add(Kit.EmptyState("IcoRestore", "No changes yet", "When you apply a tweak, profile, DNS or startup change, it appears here with a rollback button.")); return; }
        foreach (var r in records.Take(200))
        {
            var g = new Grid { Margin = new Thickness(0, 7, 0, 7), Opacity = r.RolledBack ? 0.55 : 1 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = r.Time.ToString("MMM d, HH:mm"), Style = Ui.Style("Muted"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var t = new StackPanel { Orientation = Orientation.Horizontal };
            t.Children.Add(new TextBlock { Text = r.Title, FontSize = 13 });
            var kind = Ui.Badge(r.Kind.ToString(), "TextDimBrush", "Surface3Brush"); kind.Margin = new Thickness(8, 0, 0, 0); t.Children.Add(kind);
            if (r.RolledBack) { var rb = Ui.Badge($"Rolled back {r.RolledBackAt:g}", "GoodBrush", "GoodSoftBrush"); rb.Margin = new Thickness(6, 0, 0, 0); t.Children.Add(rb); }
            sp.Children.Add(t);
            sp.Children.Add(new TextBlock { Text = r.Detail, Style = Ui.Style("Muted") });
            if (r.Snapshots.Count > 0)
                sp.Children.Add(new TextBlock { Text = string.Join("\n", r.Snapshots.Select(s => $"{s.Display} — was {(s.CreatedRoot is not null ? "absent (key will be removed)" : s.KeyOnly ? "present" : s.ValueExisted ? (s.StringData ?? s.NumberData?.ToString() ?? (s.MultiData is null ? "binary data" : string.Join(";", s.MultiData))) : "not set")}")), Style = Ui.Style("Muted"), FontSize = 11, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            if (!r.RolledBack && r.Reversible)
            {
                var b = Ui.IconButton("IcoUndo", "Roll back", "Ghost");
                b.VerticalAlignment = VerticalAlignment.Top;
                var rec = r;
                b.Click += async (_, _) =>
                {
                    if (!await Shell.ConfirmAsync("Roll back this change?", $"{rec.Title}\n\nVYTRIX restores the value recorded before this change.", "Roll back")) return;
                    await Kit.RunAsync(b, () => TweakEngine.RollbackRecordAsync(rec));
                    RenderJournal();
                };
                Grid.SetColumn(b, 2); g.Children.Add(b);
            }
            else if (!r.Reversible && !r.RolledBack)
            {
                var n = new TextBlock { Text = "Not reversible", Style = Ui.Style("Muted"), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Informational record (for example deleted temp files, or a profile summary whose individual changes are listed separately)." };
                Grid.SetColumn(n, 2); g.Children.Add(n);
            }
            _journal.Children.Add(g);
            _journal.Children.Add(new Border { Height = 1, Background = Ui.Res("BorderBrush") });
        }
    }

    private async Task RollbackAll(Button trigger)
    {
        var open = Journal.All.Where(r => r.Reversible && !r.RolledBack).ToList();
        if (open.Count == 0) { Shell.Toast("There are no changes to roll back.", ToastKind.Info); return; }
        if (!await Shell.ConfirmAsync($"Roll back all {open.Count} change(s)?", "Every reversible change VYTRIX has made is undone, newest first. Some settings take effect after a restart.", "Roll back everything", danger: true)) return;
        await Kit.RunAsync(trigger, async () =>
        {
            int ok = 0; var failed = new List<string>();
            foreach (var r in open)   // newest first
            {
                if (r.RolledBack) continue;   // may have been closed by an earlier tweak revert
                var res = await TweakEngine.RollbackRecordAsync(r);
                if (res.Ok) ok++; else failed.Add($"{r.Title}: {res.Message}");
            }
            RenderJournal();
            return failed.Count == 0 ? (true, $"Rolled back {ok} change(s).") : (false, $"Rolled back {ok}; {failed.Count} failed:\n" + string.Join("\n", failed.Take(5)));
        });
    }

    private void RenderLog()
    {
        _log.Children.Clear();
        var entries = Log.ReadDay(DateTime.Now).Reverse().Take(150).ToList();
        if (entries.Count == 0) { _log.Children.Add(Kit.EmptyState("IcoList", "No log entries today", "")); return; }
        var list = new ListBox { Style = Ui.Style("PlainList"), MaxHeight = 360 };
        foreach (var e in entries)
        {
            var g = new Grid { Margin = new Thickness(4, 3, 4, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = e.Time.ToString("HH:mm:ss"), Style = Ui.Style("Muted"), FontSize = 11.5 });
            var lv = new TextBlock { Text = e.Level.ToString(), FontSize = 11.5, Foreground = Ui.Res(e.Level switch { LogLevel.Error => "BadBrush", LogLevel.Warn => "WarnBrush", LogLevel.Change => "AccentHoverBrush", _ => "TextMutedBrush" }) };
            Grid.SetColumn(lv, 1); g.Children.Add(lv);
            var ar = new TextBlock { Text = e.Area, Style = Ui.Style("Muted"), FontSize = 11.5 };
            Grid.SetColumn(ar, 2); g.Children.Add(ar);
            var m = new TextBlock { Text = e.Message, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(m, 3); g.Children.Add(m);
            list.Items.Add(g);
        }
        _log.Children.Add(list);
    }
}
