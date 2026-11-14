using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

public sealed class MemoryPage : PageBase
{
    public override string PageTitle => "Memory";
    public override string PageSubtitle => "How your RAM is being used. VYTRIX has no \"RAM cleaner\": forcing memory out of apps just makes Windows reload it from disk.";

    private readonly Dictionary<string, TextBlock> _stats = [];
    private readonly Meter _usage = new() { Height = 10 };
    private readonly Sparkline _graph = new() { Height = 90, Margin = new Thickness(0, 14, 0, 0) };
    private readonly StackPanel _procs = new();
    private int _tick;
    private bool _byPrivate = true;

    protected override void Build()
    {
        var top = Kit.Grid(150, 6, 10);
        foreach (var (k, tip) in new[]
        {
            ("In use", "Physical memory currently used by apps, the OS and drivers."),
            ("Available", "Memory that can be given to apps immediately (free + standby cache)."),
            ("Committed", "Memory the system has promised to processes, backed by RAM or the page file."),
            ("Commit limit", "Physical RAM plus page file size. If commit reaches this, apps can fail to allocate."),
            ("System cache", "File data cached in RAM. Windows releases it instantly when apps need memory."),
            ("Kernel (paged / non-paged)", "Memory used by the Windows kernel and drivers."),
        })
        {
            var v = new TextBlock { Style = Ui.Style("Number"), FontSize = 20, Margin = new Thickness(0, 6, 0, 0) };
            _stats[k] = v;
            var sp = new StackPanel { ToolTip = tip };
            sp.Children.Add(new TextBlock { Text = k, Style = Ui.Style("Muted") });
            sp.Children.Add(v);
            top.Children.Add(new Border { Style = Ui.Style("Card"), Padding = new Thickness(14, 12, 14, 12), Child = sp });
        }
        Body.Children.Add(top);

        var usage = new StackPanel();
        _usage.Fill = Ui.Res("GoodBrush");
        usage.Children.Add(_usage);
        _graph.Stroke = Ui.Res("GoodBrush");
        usage.Children.Add(_graph);
        Body.Children.Add(Kit.Section("Usage"));
        Body.Children.Add(Kit.Card(usage, "Physical memory", "Last 90 seconds"));

        Body.Children.Add(Kit.Section("High-memory applications"));
        var toggle = new StackPanel { Orientation = Orientation.Horizontal };
        var a = new RadioButton { Content = "Private bytes", Style = Ui.Style("Chip"), IsChecked = true, GroupName = "memsort", ToolTip = "Memory that belongs only to the process — the best measure of what it costs." };
        var b = new RadioButton { Content = "Working set", Style = Ui.Style("Chip"), GroupName = "memsort", ToolTip = "Physical RAM the process currently occupies, including shared pages." };
        a.Checked += (_, _) => { _byPrivate = true; _ = LoadProcsAsync(); };
        b.Checked += (_, _) => { _byPrivate = false; _ = LoadProcsAsync(); };
        toggle.Children.Add(a); toggle.Children.Add(b);
        Body.Children.Add(Kit.Card(_procs, "Top 20 processes", "Grouped by executable", toggle));

        Body.Children.Add(Kit.Section("Diagnostics"));
        var diag = new StackPanel();
        diag.Children.Add(DiagTool("Windows Memory Diagnostic", "Tests your RAM for hardware faults. Windows restarts into the test, which takes 10–20 minutes. You choose when to restart.", "Open", () => SysCmd.Open(SysCmd.SystemExe("MdSched.exe"))));
        diag.Children.Add(DiagTool("Resource Monitor", "Detailed live view of memory, including hard faults, standby and modified lists.", "Open", () => SysCmd.Open(SysCmd.SystemExe("resmon.exe"))));
        var modules = new StackPanel();
        if (SystemInfo.MemoryModules.Count == 0) modules.Children.Add(new TextBlock { Text = SystemInfo.Loaded ? "Module details unavailable." : "Loading module details…", Style = Ui.Style("Muted") });
        foreach (var m in SystemInfo.MemoryModules)
            modules.Children.Add(Kit.KeyValue(m.Bank, $"{Fmt.Bytes(m.Capacity)} · {(m.SpeedMhz > 0 ? $"{m.SpeedMhz} MT/s" : "speed unknown")} · {m.Manufacturer} {m.PartNumber}".Trim()));
        var g = Kit.Grid(380, 2);
        g.Children.Add(Kit.Card(diag, "Memory diagnostics"));
        g.Children.Add(Kit.Card(modules, "Installed modules", "From SMBIOS via WMI"));
        Body.Children.Add(g);
    }

    private static Grid DiagTool(string title, string desc, string button, Action act)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        sp.Children.Add(new TextBlock { Text = desc, Style = Ui.Style("Muted") });
        g.Children.Add(sp);
        var b = new Button { Content = button, Style = Ui.Style("Ghost"), VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => act();
        Grid.SetColumn(b, 1);
        g.Children.Add(b);
        return g;
    }

    public override void OnShown() { Telemetry.Instance.Updated += Refresh; Refresh(); _ = LoadProcsAsync(); }
    public override void OnHidden() => Telemetry.Instance.Updated -= Refresh;

    private void Refresh()
    {
        var m = Telemetry.Instance.Memory;
        double used = m.TotalPhys - m.AvailPhys;
        _stats["In use"].Text = Fmt.Bytes(used);
        _stats["Available"].Text = Fmt.Bytes(m.AvailPhys);
        _stats["Committed"].Text = Fmt.Bytes(m.CommitTotal);
        _stats["Commit limit"].Text = Fmt.Bytes(m.CommitLimit);
        _stats["System cache"].Text = Fmt.Bytes(m.SystemCache);
        _stats["Kernel (paged / non-paged)"].Text = $"{Fmt.Bytes(m.KernelPaged)} / {Fmt.Bytes(m.KernelNonPaged)}";
        _stats["Kernel (paged / non-paged)"].FontSize = 15;
        _usage.Value = m.TotalPhys > 0 ? used * 100 / m.TotalPhys : 0;
        _usage.Fill = Ui.Res(_usage.Value > 85 ? "WarnBrush" : "GoodBrush");
        _graph.SetValues(Telemetry.Instance.RamHistory);
        if (++_tick % 5 == 0) _ = LoadProcsAsync();
    }

    private async Task LoadProcsAsync()
    {
        bool byPriv = _byPrivate;
        var groups = await Task.Run(() => ProcessService.Snapshot(withPublisher: true)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count(), Priv: g.Sum(x => (double)x.PrivateBytes), Ws: g.Sum(x => (double)x.WorkingSet), Pub: g.First().Publisher, Cls: g.First().ClassLabel))
            .OrderByDescending(x => byPriv ? x.Priv : x.Ws).Take(20).ToList());
        _procs.Children.Clear();
        double max = groups.Count > 0 ? (byPriv ? groups[0].Priv : groups[0].Ws) : 1;
        foreach (var p in groups)
        {
            double v = byPriv ? p.Priv : p.Ws;
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = p.Count > 1 ? $"{p.Name}  ×{p.Count}" : p.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = $"{p.Cls}{(string.IsNullOrEmpty(p.Pub) ? "" : " · " + p.Pub)}", Style = Ui.Style("Muted"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            g.Children.Add(sp);
            var meter = new Meter { Value = v * 100 / max, Fill = Ui.Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            Grid.SetColumn(meter, 1); g.Children.Add(meter);
            var t = new TextBlock { Text = Fmt.Bytes(v), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["NumberFont"] };
            Grid.SetColumn(t, 2); g.Children.Add(t);
            _procs.Children.Add(g);
        }
    }
}

public sealed class StoragePage : PageBase
{
    public override string PageTitle => "Storage";
    public override string PageSubtitle => "Disk usage, safe cleanup with a full preview, and the folders taking the most space. Personal files are never deleted.";

    private readonly ResponsiveGrid _volumes = Kit.Grid(260, 4);
    private readonly StackPanel _cleanup = new();
    private readonly StackPanel _folders = new();
    private List<CleanupCategory> _cats = StorageService.Categories();
    private bool _scanned;
    private CancellationTokenSource? _folderCts;

    protected override void Build()
    {
        Body.Children.Add(_volumes);
        Body.Children.Add(Kit.Section("Cleanup"));
        Body.Children.Add(Kit.Card(_cleanup, "Temporary and cache files", "Only system-managed temp and cache locations are scanned. Files changed in the last 24 hours and files in use are skipped."));
        Body.Children.Add(Kit.Section("Large folders"));
        Body.Children.Add(Kit.Card(_folders, "Your profile folders", "Read-only analysis. Open a folder in Explorer to decide what to keep."));
        RenderCleanup();
        RenderFoldersIdle();
    }

    public override void OnShown() => RenderVolumes();
    public override void OnHidden() => _folderCts?.Cancel();

    private void RenderVolumes()
    {
        _volumes.Children.Clear();
        foreach (var v in SystemInfo.Volumes())
        {
            var sp = new StackPanel();
            var h = new Grid();
            h.Children.Add(new TextBlock { Text = $"{v.Label} ({v.Name.TrimEnd('\\')})", Style = Ui.Style("H3") });
            h.Children.Add(new TextBlock { Text = v.Format, Style = Ui.Style("Muted"), HorizontalAlignment = HorizontalAlignment.Right });
            sp.Children.Add(h);
            sp.Children.Add(new TextBlock { Text = $"{Fmt.Bytes(v.Free)} free of {Fmt.Bytes(v.Total)}", Style = Ui.Style("Dim"), Margin = new Thickness(0, 6, 0, 10) });
            sp.Children.Add(new Meter { Value = v.UsedPct, Fill = Ui.Res(v.UsedPct > 90 ? "BadBrush" : v.UsedPct > 80 ? "WarnBrush" : "AccentBrush") });
            _volumes.Children.Add(new Border { Style = Ui.Style("Card"), Child = sp });
        }
        if (SystemInfo.Disks.Count > 0)
        {
            var sp = new StackPanel();
            foreach (var d in SystemInfo.Disks) sp.Children.Add(Kit.KeyValue(d.Model, $"{Fmt.Bytes(d.Size)} {d.MediaType} · {d.BusType} · {d.Health}"));
            _volumes.Children.Add(Kit.Card(sp, "Physical disks"));
        }
    }

    private void RenderCleanup()
    {
        _cleanup.Children.Clear();
        if (!Elevation.IsAdmin) _cleanup.Children.Add(Kit.Notice("Some locations (Windows Temp, error reports, Delivery Optimization) need administrator mode and will be skipped.", "info"));
        foreach (var c in _cats)
        {
            if (c.RequiresAdmin && !Elevation.IsAdmin) c.Selected = false;   // cannot be cleaned in standard mode
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cb = new CheckBox { IsChecked = c.Selected, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0), IsEnabled = !(c.RequiresAdmin && !Elevation.IsAdmin) };
            cb.Checked += (_, _) => { c.Selected = true; UpdateTotal(); };
            cb.Unchecked += (_, _) => { c.Selected = false; UpdateTotal(); };
            g.Children.Add(cb);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = c.Name, FontSize = 13 });
            sp.Children.Add(new TextBlock { Text = c.Description, Style = Ui.Style("Muted") });
            if (c.Note is not null) sp.Children.Add(new TextBlock { Text = c.Note, Foreground = Ui.Res("WarnBrush"), FontSize = 11.5 });
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            right.Children.Add(new TextBlock { Text = _scanned ? c.SizeText : "—", FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["NumberFont"], FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right });
            if (_scanned && c.FileCount > 0 && !c.IsRecycleBin)
            {
                var view = new Button { Content = $"Preview {c.FileCount:N0} files", Style = Ui.Style("Link"), FontSize = 11.5 };
                view.Click += async (_, _) => await PreviewFiles(c);
                right.Children.Add(view);
            }
            else if (_scanned) right.Children.Add(new TextBlock { Text = $"{c.FileCount:N0} items", Style = Ui.Style("Muted"), HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetColumn(right, 2); g.Children.Add(right);
            _cleanup.Children.Add(g);
        }
        var bar = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        _total = new TextBlock { Style = Ui.Style("H3"), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(_total);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var scan = Ui.IconButton("IcoSearch", _scanned ? "Rescan" : "Scan", _scanned ? "Ghost" : "Primary");
        scan.Click += async (_, _) => await Kit.RunAsync(scan, async () =>
        {
            _cats = StorageService.Categories();
            await StorageService.ScanAsync(_cats);
            _scanned = true;
            RenderCleanup();
            return (true, $"Found {Fmt.Bytes(_cats.Where(c => c.Selected).Sum(c => c.Bytes))} in selected locations.");
        }, toastOnSuccess: false);
        actions.Children.Add(scan);
        if (_scanned)
        {
            var clean = Ui.IconButton("IcoTrash", "Clean selected", "Primary");
            clean.Margin = new Thickness(8, 0, 0, 0);
            clean.Click += async (_, _) => await Clean(clean);
            actions.Children.Add(clean);
        }
        bar.Children.Add(actions);
        _cleanup.Children.Add(bar);
        UpdateTotal();
    }

    private TextBlock _total = new();

    private void UpdateTotal()
    {
        _total.Text = _scanned ? $"Selected: {Fmt.Bytes(_cats.Where(c => c.Selected && !(c.RequiresAdmin && !Elevation.IsAdmin)).Sum(c => c.Bytes))}" : "Scan to see how much space can be recovered";
    }

    private async Task PreviewFiles(CleanupCategory c)
    {
        var list = new ListBox { Style = Ui.Style("PlainList"), MaxHeight = 360, Width = 500 };
        foreach (var f in c.Files.OrderByDescending(f => f.Length).Take(500))
            list.Items.Add(new TextBlock { Text = $"{Fmt.Bytes(f.Length),10}   {f.FullName}", FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Margin = new Thickness(4, 2, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.FullName });
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = c.FileCount > 500 ? $"Largest 500 of {c.FileCount:N0} files:" : $"{c.FileCount:N0} files:", Style = Ui.Style("Muted"), Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(list);
        await Shell.AlertAsync(c.Name, body);
    }

    private async Task Clean(Button trigger)
    {
        var sel = _cats.Where(c => c.Selected && c.Bytes > 0 && !(c.RequiresAdmin && !Elevation.IsAdmin)).ToList();
        if (sel.Count == 0) { Shell.Toast("Nothing selected to clean.", ToastKind.Info); return; }
        var body = new StackPanel();
        foreach (var c in sel) body.Children.Add(Kit.KeyValue(c.Name, $"{c.SizeText} · {c.FileCount:N0} item(s)"));
        body.Children.Add(new TextBlock { Text = "Deleted files do not go to the Recycle Bin and cannot be restored. Files in use are skipped automatically." + (sel.Any(c => c.IsRecycleBin) ? " Emptying the Recycle Bin permanently deletes everything in it." : ""), Style = Ui.Style("Dim"), Margin = new Thickness(0, 12, 0, 0) });
        if (!await Shell.ConfirmAsync($"Delete {Fmt.Bytes(sel.Sum(c => c.Bytes))} of temporary files?", body, "Delete permanently", danger: true)) return;
        await Kit.RunAsync(trigger, async () =>
        {
            var (freed, deleted, skipped) = await StorageService.CleanAsync(sel);
            await StorageService.ScanAsync(_cats);
            RenderCleanup();
            RenderVolumes();
            return (true, $"Freed {Fmt.Bytes(freed)} ({deleted:N0} items). {(skipped > 0 ? $"{skipped:N0} skipped because they were in use or protected." : "")}");
        });
    }

    private void RenderFoldersIdle()
    {
        _folders.Children.Clear();
        var b = Ui.IconButton("IcoSearch", "Analyze folders", "Primary");
        b.Click += async (_, _) => await AnalyzeAsync(null, "Your profile");
        _folders.Children.Add(Kit.EmptyState("IcoFolder", "Find what's using space", "Sizes Desktop, Documents, Downloads, media folders and AppData. Nothing is changed.", b));
    }

    private async Task AnalyzeAsync(string? path, string title)
    {
        _folderCts?.Cancel();
        _folderCts = new CancellationTokenSource();
        var ct = _folderCts.Token;
        _folders.Children.Clear();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = path ?? title, Style = Ui.Style("H3"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 200, 0) });
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (path is not null)
        {
            var back = new Button { Content = "← Back", Style = Ui.Style("Ghost"), Padding = new Thickness(10, 4, 10, 4) };
            back.Click += async (_, _) => await AnalyzeAsync(null, "Your profile");
            nav.Children.Add(back);
            var open = new Button { Content = "Open in Explorer", Style = Ui.Style("Ghost"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
            open.Click += (_, _) => SysCmd.Open(path);
            nav.Children.Add(open);
        }
        head.Children.Add(nav);
        _folders.Children.Add(head);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        status.Children.Add(new Spinner { Width = 16, Height = 16, Stroke = Ui.Res("AccentBrush") });
        status.Children.Add(new TextBlock { Text = "Measuring…", Style = Ui.Style("Muted"), Margin = new Thickness(8, 0, 0, 0) });
        _folders.Children.Add(status);
        var rows = new StackPanel();
        _folders.Children.Add(rows);
        try
        {
            var items = path is null ? await StorageService.LargeFoldersAsync(null, ct) : await StorageService.ChildrenAsync(path, ct);
            if (ct.IsCancellationRequested) return;
            _folders.Children.Remove(status);
            if (items.Count == 0) { rows.Children.Add(Kit.EmptyState("IcoFolder", "Empty", "No subfolders to show.")); return; }
            double max = items.Max(i => i.Bytes);
            foreach (var f in items)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent, ToolTip = $"{f.Path}\n{f.Files:N0} files — click to look inside" };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                g.Children.Add(new TextBlock { Text = f.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                var m = new Meter { Value = max > 0 ? f.Bytes * 100 / max : 0, Fill = Ui.Res("Accent2Brush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
                Grid.SetColumn(m, 1); g.Children.Add(m);
                var s = new TextBlock { Text = f.SizeText, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(s, 2); g.Children.Add(s);
                var target = f;
                g.MouseLeftButtonUp += async (_, _) => await AnalyzeAsync(target.Path, target.Name);
                rows.Children.Add(g);
            }
        }
        catch (OperationCanceledException) { }
    }
}
