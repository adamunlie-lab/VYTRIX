using System.Windows;
using System.Windows.Controls;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

public sealed class HardwarePage : PageBase
{
    public override string PageTitle => "Hardware";
    public override string PageSubtitle => "Your components as reported by the CPU, display driver, firmware and Windows Storage.";

    private readonly ResponsiveGrid _grid = Kit.Grid(380, 2);

    protected override void Build()
    {
        Body.Children.Add(_grid);
        SystemInfo.LoadedChanged += () => Dispatcher.BeginInvoke(Render);
    }

    private readonly StackPanel _sensors = new();

    public override void OnShown() { Render(); Telemetry.Instance.Updated += RenderSensors; }
    public override void OnHidden() => Telemetry.Instance.Updated -= RenderSensors;

    private void RenderSensors()
    {
        _sensors.Children.Clear();
        var t = Telemetry.Instance;
        var s = t.Last;
        string Read(double c, string ok, string missing) => !t.HasSample ? "Measuring…" : c < 0 ? missing : string.Format(ok, c);
        _sensors.Children.Add(Kit.KeyValue("GPU temperature", Read(s.GpuTempC, "{0:0} °C (NVML)", "Unavailable — only NVIDIA exposes this through a public API (NVML)")));
        _sensors.Children.Add(Kit.KeyValue("ACPI thermal zone", Read(s.AcpiTempC, "{0:0} °C", "Unavailable — firmware does not report a thermal zone to Windows")));
        _sensors.Children.Add(Kit.KeyValue("CPU package", "Not exposed by Windows. Reading it needs a kernel driver, which VYTRIX deliberately does not install."));
    }

    private void Render()
    {
        _grid.Children.Clear();
        var c = SystemInfo.Cpu;
        var cpu = new StackPanel();
        cpu.Children.Add(Kit.KeyValue("Model", c.Brand));
        cpu.Children.Add(Kit.KeyValue("Vendor", c.Vendor));
        cpu.Children.Add(Kit.KeyValue("Cores / threads", $"{c.PhysicalCores} / {c.LogicalProcessors}"));
        cpu.Children.Add(Kit.KeyValue("L2 / L3 cache", $"{c.L2CacheKB / 1024.0:0.#} MB / {c.L3CacheKB / 1024.0:0.#} MB"));
        cpu.Children.Add(Kit.KeyValue("Rated clock", c.MaxMhz > 0 ? $"{c.MaxMhz / 1000.0:0.00} GHz (base, as reported to Windows)" : "Unavailable"));
        _grid.Children.Add(Kit.Card(cpu, "Processor", "CPUID and Windows topology"));

        foreach (var g in SystemInfo.Gpus.Where(g => g.IsSoftware == 0))
        {
            var sp = new StackPanel();
            sp.Children.Add(Kit.KeyValue("Model", g.Name));
            sp.Children.Add(Kit.KeyValue("Dedicated memory", Fmt.Bytes(g.DedicatedVideoMemory)));
            sp.Children.Add(Kit.KeyValue("Shared memory", Fmt.Bytes(g.SharedSystemMemory)));
            sp.Children.Add(Kit.KeyValue("Vendor / device ID", $"{g.VendorId:X4} / {g.DeviceId:X4}"));
            sp.Children.Add(Kit.KeyValue("HW GPU scheduling", g.HagsSupported switch { 1 => g.HagsEnabled == 1 ? "Supported, on" : "Supported, off", 0 => "Not supported", _ => "Unknown" }));
            _grid.Children.Add(Kit.Card(sp, "Graphics", "DXGI and D3DKMT"));
        }

        var mem = new StackPanel();
        mem.Children.Add(Kit.KeyValue("Installed", Fmt.Bytes(SystemInfo.TotalMemory) + " usable"));
        if (!SystemInfo.Loaded) mem.Children.Add(new TextBlock { Text = "Loading module details…", Style = Ui.Style("Muted") });
        foreach (var m in SystemInfo.MemoryModules)
            mem.Children.Add(Kit.KeyValue(m.Bank, $"{Fmt.Bytes(m.Capacity)} · {(m.SpeedMhz > 0 ? $"{m.SpeedMhz} MT/s" : "speed unknown")} · {m.Manufacturer} {m.PartNumber}".Trim()));
        _grid.Children.Add(Kit.Card(mem, "Memory"));

        var board = new StackPanel();
        board.Children.Add(Kit.KeyValue("System", SystemInfo.SystemModel));
        board.Children.Add(Kit.KeyValue("Motherboard", SystemInfo.Motherboard));
        board.Children.Add(Kit.KeyValue("BIOS / UEFI", SystemInfo.Bios));
        board.Children.Add(Kit.KeyValue("Form factor", SystemInfo.Loaded ? (SystemInfo.IsLaptop ? "Laptop / portable" : "Desktop") : "…"));
        board.Children.Add(Kit.KeyValue("Windows", $"{SystemInfo.OsName} {SystemInfo.OsVersion} (build {SystemInfo.OsBuild})"));
        if (SystemInfo.InstallDate is { } d) board.Children.Add(Kit.KeyValue("Installed on", d.ToString("D")));
        board.Children.Add(Kit.KeyValue("Computer name", SystemInfo.ComputerName));
        _grid.Children.Add(Kit.Card(board, "System & firmware", "SMBIOS via WMI"));

        var disks = new StackPanel();
        if (SystemInfo.Disks.Count == 0) disks.Children.Add(new TextBlock { Text = SystemInfo.Loaded ? "Disk details unavailable." : "Loading…", Style = Ui.Style("Muted") });
        foreach (var d2 in SystemInfo.Disks) disks.Children.Add(Kit.KeyValue(d2.Model, $"{Fmt.Bytes(d2.Size)} · {d2.MediaType} · {d2.BusType} · {d2.Health}"));
        _grid.Children.Add(Kit.Card(disks, "Storage devices", "Windows Storage Management (MSFT_PhysicalDisk)"));

        RenderSensors();
        if (_sensors.Parent is Panel oldCard) oldCard.Children.Remove(_sensors);   // detach from the previous render's card
        _grid.Children.Add(Kit.Card(_sensors, "Sensors", "What this PC exposes to Windows"));
    }
}

public sealed class AppsPage : PageBase
{
    public override string PageTitle => "Installed Apps";
    public override string PageSubtitle => "Desktop programs registered with Windows. Uninstalling always runs the app's own uninstaller.";

    private readonly ListBox _list = new() { Style = Ui.Style("PlainList"), MaxHeight = 2000 };
    private readonly TextBlock _count = new() { Style = Ui.Style("Muted"), Margin = new Thickness(0, 0, 0, 10) };
    private List<InstalledApp> _apps = [];
    private string _q = "";
    private string _sort = "Name";

    protected override void Build()
    {
        HeaderActions.Children.Add(Kit.Search("Search apps or publishers…", q => { _q = q; Render(); }));
        var open = new Button { Content = "Windows Apps settings", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => SysCmd.Open("ms-settings:appsfeatures");
        HeaderActions.Children.Add(open);
        var sort = new WrapPanel();
        foreach (var s in new[] { "Name", "Size", "Install date" })
        {
            var rb = new RadioButton { Content = s, Style = Ui.Style("Chip"), GroupName = "appsort", IsChecked = s == "Name" };
            rb.Checked += (_, _) => { _sort = s; Render(); };
            sort.Children.Add(rb);
        }
        Body.Children.Add(sort);
        Body.Children.Add(_count);
        Body.Children.Add(Kit.Card(_list));
        // The page itself scrolls; let the list grow and disable its own scrolling.
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
    }

    public override async void OnShown()
    {
        if (_apps.Count == 0)
        {
            _count.Text = "Loading…";
            _apps = await Task.Run(InstalledApps.List);
        }
        Render();
    }

    private void Render()
    {
        IEnumerable<InstalledApp> q = _apps.Where(a => _q.Length == 0 || a.Name.Contains(_q, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(_q, StringComparison.OrdinalIgnoreCase));
        q = _sort switch { "Size" => q.OrderByDescending(a => a.SizeBytes), "Install date" => q.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue), _ => q };
        var items = q.ToList();
        _count.Text = $"{items.Count} of {_apps.Count} apps · total reported size {Fmt.Bytes(items.Sum(a => a.SizeBytes))} (sizes are self-reported by installers)";
        _list.Items.Clear();
        if (items.Count == 0) { _list.Items.Add(Kit.EmptyState("IcoApps", "No apps match", "Try a different search.")); return; }
        foreach (var a in items)
        {
            var g = new Grid { Margin = new Thickness(6, 6, 6, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = a.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = $"{(a.Publisher.Length > 0 ? a.Publisher : "Unknown publisher")}{(a.Version.Length > 0 ? " · " + a.Version : "")} · {a.Scope}", Style = Ui.Style("Muted"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            g.Children.Add(sp);
            var date = new TextBlock { Text = a.DateText, Style = Ui.Style("Muted"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(date, 1); g.Children.Add(date);
            var size = new TextBlock { Text = a.SizeText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 14, 0) };
            Grid.SetColumn(size, 2); g.Children.Add(size);
            var un = new Button { Content = "Uninstall", Style = Ui.Style("Ghost"), Padding = new Thickness(10, 4, 10, 4), IsEnabled = a.UninstallString.Length > 0 };
            var app = a;
            un.Click += async (_, _) =>
            {
                if (!await Shell.ConfirmAsync($"Uninstall {app.Name}?", $"VYTRIX will open {app.Name}'s own uninstaller. Follow its steps to finish; nothing is removed silently.", "Open uninstaller")) return;
                var r = InstalledApps.LaunchUninstaller(app);
                Shell.Toast(r.Item2, r.Item1 ? ToastKind.Info : ToastKind.Error);
                _apps = [];   // reload next time the page is shown
            };
            Grid.SetColumn(un, 3); g.Children.Add(un);
            _list.Items.Add(g);
        }
    }
}

public sealed class ServicesPage : PageBase
{
    public override string PageTitle => "Services";
    public override string PageSubtitle => "Windows services with safety restrictions. Security, update and core services are locked and cannot be changed here.";

    private readonly ListBox _list = new() { Style = Ui.Style("PlainList") };
    private readonly TextBlock _count = new() { Style = Ui.Style("Muted"), Margin = new Thickness(0, 0, 0, 10) };
    private List<ServiceInfo> _all = [];
    private string _q = "", _filter = "All";

    protected override void Build()
    {
        HeaderActions.Children.Add(Kit.Search("Search services…", q => { _q = q; Render(); }));
        if (!Elevation.IsAdmin) Body.Children.Add(Kit.Notice("Viewing only. Changing services requires administrator mode.", "info"));
        var chips = new WrapPanel();
        foreach (var f in new[] { "All", "Running", "Optional", "Caution", "Protected" })
        {
            var rb = new RadioButton { Content = f, Style = Ui.Style("Chip"), GroupName = "svc", IsChecked = f == "All" };
            rb.Checked += (_, _) => { _filter = f; Render(); };
            chips.Children.Add(rb);
        }
        Body.Children.Add(chips);
        Body.Children.Add(Kit.Notice("Optional = services with a documented reason you might set them to Manual. Caution = third-party or less common Windows services; only change them if you know what they do. Startup-type changes are journaled and can be rolled back.", "info"));
        Body.Children.Add(_count);
        Body.Children.Add(Kit.Card(_list));
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
    }

    public override async void OnShown()
    {
        _count.Text = "Loading services…";
        _all = await Task.Run(WinServices.List);
        Render();
    }

    private void Render()
    {
        var items = _all.Where(s =>
            (_q.Length == 0 || s.DisplayName.Contains(_q, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(_q, StringComparison.OrdinalIgnoreCase)) &&
            (_filter == "All" || (_filter == "Running" && s.Status == "Running") || s.SafetyLabel == _filter)).ToList();
        _count.Text = $"{items.Count} of {_all.Count} services";
        _list.Items.Clear();
        foreach (var s in items)
        {
            var g = new Grid { Margin = new Thickness(6, 7, 6, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var t = new StackPanel { Orientation = Orientation.Horizontal };
            t.Children.Add(new TextBlock { Text = s.DisplayName, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
            var badge = s.Safety switch
            {
                SvcSafety.Protected => Ui.Badge("Protected", "TextMutedBrush", "Surface3Brush"),
                SvcSafety.Optional => Ui.Badge("Optional", "GoodBrush", "GoodSoftBrush"),
                _ => Ui.Badge("Caution", "WarnBrush", "WarnSoftBrush"),
            };
            badge.Margin = new Thickness(8, 0, 0, 0);
            t.Children.Add(badge);
            sp.Children.Add(t);
            sp.Children.Add(new TextBlock { Text = s.Advice ?? (s.Description.Length > 0 ? s.Description : s.Name), Style = Ui.Style("Muted"), FontSize = 11.5, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{s.Name}\n{s.Description}" });
            g.Children.Add(sp);
            var st = new TextBlock { Text = s.Status, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res(s.Status == "Running" ? "GoodBrush" : "TextMutedBrush"), FontSize = 12 };
            Grid.SetColumn(st, 1); g.Children.Add(st);
            if (s.Safety == SvcSafety.Protected || !Elevation.IsAdmin)
            {
                var stt = new TextBlock { Text = s.StartType, Style = Ui.Style("Dim"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                Grid.SetColumn(stt, 2); g.Children.Add(stt);
            }
            else
            {
                var combo = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
                foreach (var o in new[] { "Automatic", "Manual", "Disabled" }) combo.Items.Add(o);
                combo.SelectedItem = s.StartType.StartsWith("Automatic") ? "Automatic" : s.StartType;
                if (combo.SelectedItem is null) combo.Text = s.StartType;
                var svc = s;
                combo.SelectionChanged += async (_, _) =>
                {
                    var current = svc.StartType.StartsWith("Automatic") ? "Automatic" : svc.StartType;
                    if (combo.SelectedItem is not string target) return;
                    if (target == current) return;
                    var warn = target == "Disabled" ? "\n\nDisabled services cannot be started by apps that need them. Prefer Manual unless you're sure." : "";
                    if (!await Shell.ConfirmAsync($"Set {svc.DisplayName} to {target}?", $"{svc.Advice ?? svc.Description}{warn}\n\nThe previous startup type ({svc.StartType}) is recorded and can be restored from the Restore Center.", "Change", danger: svc.Safety == SvcSafety.Caution || target == "Disabled"))
                    {
                        combo.SelectedItem = current;
                        return;
                    }
                    var r = await WinServices.SetStartTypeAsync(svc, target);
                    Shell.Toast(r.Item2, r.Item1 ? ToastKind.Success : ToastKind.Error);
                    if (!r.Item1) combo.SelectedItem = current;
                };
                Grid.SetColumn(combo, 2); g.Children.Add(combo);
                if (s.Status == "Running" && s.CanStop || s.Status == "Stopped")
                {
                    bool running = s.Status == "Running";
                    var b = new Button { Content = running ? "Stop" : "Start", Style = Ui.Style("Ghost"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    b.Click += async (_, _) =>
                    {
                        if (running && !await Shell.ConfirmAsync($"Stop {svc.DisplayName}?", "Features that depend on this service stop working until it is started again or you restart.", "Stop service", danger: true)) return;
                        await Kit.RunAsync(b, () => WinServices.StartStopAsync(svc, !running));
                        Render();
                    };
                    Grid.SetColumn(b, 3); g.Children.Add(b);
                }
            }
            _list.Items.Add(g);
        }
    }
}

public sealed class BenchmarkPage : PageBase
{
    public override string PageTitle => "Benchmark";
    public override string PageSubtitle => "Uses the Windows System Assessment Tool (winsat), Microsoft's built-in benchmark. Run tests before and after changes to measure real differences.";

    private readonly StackPanel _scores = new();
    private readonly StackPanel _results = new();
    private readonly TextBlock _status = new() { Style = Ui.Style("Muted"), Margin = new Thickness(0, 8, 0, 0) };

    protected override void Build()
    {
        if (!Elevation.IsAdmin) Body.Children.Add(Kit.Notice("WinSAT requires administrator mode. Saved scores can still be viewed.", "warn"));
        var g = Kit.Grid(400, 2);
        g.Children.Add(Kit.Card(_scores, "Windows Experience scores", "Last formal assessment (scale 1.0–9.9)"));
        var tests = new StackPanel();
        void Test(string id, string name, string desc)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(new TextBlock { Text = name, FontSize = 13 });
            sp.Children.Add(new TextBlock { Text = desc, Style = Ui.Style("Muted") });
            row.Children.Add(sp);
            var b = Ui.IconButton("IcoPlay", "Run", "Ghost");
            b.IsEnabled = Elevation.IsAdmin;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Click += async (_, _) =>
            {
                if (id == "formal" && !await Shell.ConfirmAsync("Run the full assessment?", "This runs every WinSAT test and takes several minutes. Close games and heavy apps first for accurate results.", "Run")) return;
                await Kit.RunAsync(b, async () =>
                {
                    _status.Text = $"Running {name}… keep other apps idle for accurate results.";
                    var (ok, lines, msg) = await Benchmark.RunAsync(id);
                    _status.Text = msg;
                    ShowResults(name, lines);
                    if (id == "formal") RenderScores();
                    return (ok, ok ? $"{name} finished." : msg);
                });
            };
            Grid.SetColumn(b, 1);
            row.Children.Add(b);
            tests.Children.Add(row);
        }
        Test("disk-seq", "Disk — sequential read", "Large-file read throughput of the system drive (~30 s).");
        Test("disk-ran", "Disk — random read", "4K random read performance, which drives app launch speed (~30 s).");
        Test("mem", "Memory bandwidth", "System RAM copy throughput (~15 s).");
        Test("cpu", "CPU — encryption", "AES encryption throughput using all cores (~20 s).");
        Test("formal", "Full assessment", "All tests; updates the scores on the left (several minutes).");
        tests.Children.Add(_status);
        g.Children.Add(Kit.Card(tests, "Run a test"));
        Body.Children.Add(g);
        Body.Children.Add(Kit.Section("Results"));
        Body.Children.Add(Kit.Card(_results));
        _results.Children.Add(Kit.EmptyState("IcoBenchmark", "No results yet", "Run a test to see measured throughput from winsat."));
    }

    public override void OnShown() => RenderScores();

    private async void RenderScores()
    {
        _scores.Children.Clear();
        var s = await Task.Run(Benchmark.ReadScores);
        if (s is null || s.State == 3 || s.Cpu <= 0)
        {
            _scores.Children.Add(Kit.EmptyState("IcoBenchmark", "No assessment on record", "Run the full assessment to generate scores."));
            return;
        }
        void Score(string label, double v)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.Children.Add(new TextBlock { Text = label, Style = Ui.Style("Dim") });
            var m = new Meter { Value = v / 9.9 * 100, Fill = Ui.Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(m, 1); g.Children.Add(m);
            var t = new TextBlock { Text = v.ToString("0.0"), HorizontalAlignment = HorizontalAlignment.Right, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["NumberFont"] };
            Grid.SetColumn(t, 2); g.Children.Add(t);
            _scores.Children.Add(g);
        }
        Score("Processor", s.Cpu); Score("Memory", s.Memory); Score("Primary disk", s.Disk); Score("Graphics", s.Graphics); Score("Gaming graphics", s.D3D);
        _scores.Children.Add(new TextBlock { Text = (s.When is { } w ? $"Assessed {w:g}. " : "") + (s.State == 2 ? "Hardware has changed since this assessment; re-run for current scores. " : "") + "Windows 11 no longer measures gaming graphics meaningfully, so that score is often 9.9.", Style = Ui.Style("Muted"), Margin = new Thickness(0, 10, 0, 0) });
    }

    private void ShowResults(string test, List<BenchLine> lines)
    {
        _results.Children.Clear();
        _results.Children.Add(new TextBlock { Text = $"{test} — {DateTime.Now:t}", Style = Ui.Style("H3"), Margin = new Thickness(0, 0, 0, 8) });
        if (lines.Count == 0) { _results.Children.Add(new TextBlock { Text = "winsat did not report throughput figures for this test.", Style = Ui.Style("Muted") }); return; }
        foreach (var l in lines) _results.Children.Add(Kit.KeyValue(l.Label, l.Value));
        Log.Info("bench", $"{test}: " + string.Join("; ", lines.Select(l => $"{l.Label}={l.Value}")));
    }
}

public sealed class SettingsPage : PageBase
{
    public override string PageTitle => "Settings";
    public override string PageSubtitle => "Appearance, safety behaviour, updates and data.";

    private readonly List<RadioButton> _accentChips = [];

    protected override void Build()
    {
        var s = Settings.Current;
        var g = Kit.Grid(400, 2);

        var look = new StackPanel();
        look.Children.Add(new TextBlock { Text = "THEME", Style = Ui.Style("Label"), Margin = new Thickness(0, 0, 0, 8) });
        var themes = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var th in ThemeManager.Themes)
        {
            var rb = new RadioButton { Content = th, Style = Ui.Style("Chip"), GroupName = "theme", IsChecked = s.Theme == th,
                ToolTip = th == ThemeManager.CherryBlossom ? "Night sakura palette with a swaying cherry tree and falling petals." : "The original graphite and violet look, no scenery." };
            var name = th;
            rb.Checked += (_, _) =>
            {
                s.Theme = name;
                s.Accent = name == ThemeManager.CherryBlossom ? "Sakura" : "Violet";   // each theme's signature accent
                ThemeManager.ApplyTheme(name);
                ThemeManager.Apply(s.Accent);
                foreach (var a in _accentChips) a.IsChecked = (string)a.Tag == s.Accent;
                Settings.Save();
            };
            themes.Children.Add(rb);
        }
        look.Children.Add(themes);
        look.Children.Add(new TextBlock { Text = "ACCENT COLOUR", Style = Ui.Style("Label"), Margin = new Thickness(0, 0, 0, 8) });
        var accents = new WrapPanel();
        foreach (var a in ThemeManager.Accents)
        {
            var rb = new RadioButton { Style = Ui.Style("Chip"), GroupName = "accent", IsChecked = s.Accent == a.Name, Tag = a.Name };
            _accentChips.Add(rb);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new System.Windows.Media.SolidColorBrush(a.Main), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = a.Name });
            rb.Content = sp;
            var name = a.Name;
            rb.Checked += (_, _) => { s.Accent = name; Settings.Save(); ThemeManager.Apply(name); };
            accents.Children.Add(rb);
        }
        look.Children.Add(accents);
        look.Children.Add(Option("Falling petals", "Cherry Blossom theme: petals drift across the window.", s.ShowFallingPetals, v => s.ShowFallingPetals = v));
        look.Children.Add(Option("Reduce motion", "Turns off page and dialog animations, and holds the cherry tree still.", s.ReduceMotion, v => s.ReduceMotion = v));
        look.Children.Add(new TextBlock { Text = "LIVE MONITORING INTERVAL", Style = Ui.Style("Label"), Margin = new Thickness(0, 14, 0, 6) });
        var interval = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var ms in new[] { 500, 1000, 2000, 5000 }) { interval.Items.Add(new ComboBoxItem { Content = ms < 1000 ? $"{ms} ms" : $"{ms / 1000} s", Tag = ms }); if (s.TelemetryIntervalMs == ms) interval.SelectedIndex = interval.Items.Count - 1; }
        interval.SelectionChanged += (_, _) => { s.TelemetryIntervalMs = (int)((ComboBoxItem)interval.SelectedItem).Tag; Settings.Save(); Telemetry.Instance.SetInterval(s.TelemetryIntervalMs); };
        look.Children.Add(interval);
        g.Children.Add(Kit.Card(look, "Appearance & monitoring"));

        var safety = new StackPanel();
        safety.Children.Add(Option("Confirm before applying tweaks", "Show what will change before every tweak. Moderate and advanced tweaks always ask.", s.ConfirmBeforeApplying, v => s.ConfirmBeforeApplying = v));
        safety.Children.Add(Option("Export registry before changes", "Save a .reg backup of each key before it is modified.", s.ExportRegistryBeforeChanges, v => s.ExportRegistryBeforeChanges = v));
        safety.Children.Add(Option("Restore point before profiles", "Create a System Restore point before applying a profile (administrator mode).", s.CreateRestorePointBeforeProfiles, v => s.CreateRestorePointBeforeProfiles = v));
        g.Children.Add(Kit.Card(safety, "Safety"));

        var upd = new StackPanel();
        upd.Children.Add(Kit.KeyValue("Installed version", $"VYTRIX {Updater.Current.ToString(3)}"));
        upd.Children.Add(Kit.KeyValue("Update channel", s.UpdateFeedUrl == Updater.DefaultFeed ? $"Official releases (github.com/{Updater.Repository})" : "Custom feed"));
        upd.Children.Add(Option("Check for updates automatically", "At startup and every 6 hours while VYTRIX is open.", s.CheckUpdatesOnStart, v => s.CheckUpdatesOnStart = v));
        upd.Children.Add(Option("Download updates in the background", "The installer is fetched and verified ahead of time. Installing always asks you first.", s.AutoDownloadUpdates, v => s.AutoDownloadUpdates = v));
        var updRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var check = Ui.IconButton("IcoRefresh", "Check now", "Ghost");
        check.Click += async (_, _) => await Kit.RunAsync(check, () => Shell.CheckForUpdatesAsync(userInitiated: true));
        var releases = new Button { Content = "Release notes", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        releases.Click += (_, _) => SysCmd.Open($"https://github.com/{Updater.Repository}/releases");
        updRow.Children.Add(check);
        updRow.Children.Add(releases);
        upd.Children.Add(updRow);
        // Advanced: a custom feed (for testing or self-hosting). Installer downloads must still come from GitHub hosting.
        var adv = new TextBlock { Text = "Advanced: custom update feed", Foreground = Ui.Res("AccentHoverBrush"), FontSize = 12, Margin = new Thickness(0, 12, 0, 0), Cursor = System.Windows.Input.Cursors.Hand };
        var feedPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var feed = new TextBox { Text = s.UpdateFeedUrl, Tag = Updater.DefaultFeed };
        feed.LostFocus += (_, _) => { s.UpdateFeedUrl = string.IsNullOrWhiteSpace(feed.Text) ? Updater.DefaultFeed : feed.Text.Trim(); feed.Text = s.UpdateFeedUrl; Settings.Save(); };
        feedPanel.Children.Add(feed);
        feedPanel.Children.Add(new TextBlock { Text = "Leave empty for the official channel. Every update is checked against its SHA-256 checksum before it runs.", Style = Ui.Style("Muted"), Margin = new Thickness(0, 6, 0, 0) });
        adv.MouseLeftButtonUp += (_, _) => feedPanel.Visibility = feedPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        upd.Children.Add(adv);
        upd.Children.Add(feedPanel);
        g.Children.Add(Kit.Card(upd, "Updates"));

        var data = new StackPanel();
        data.Children.Add(Kit.KeyValue("Data folder", AppPaths.Root));
        data.Children.Add(Kit.KeyValue("Reports", AppPaths.Reports));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var report = Ui.IconButton("IcoExport", "Export system report", "Primary");
        report.Click += async (_, _) => await Kit.RunAsync(report, async () => { var p = await Reports.ExportAsync(); SysCmd.Open(p); return (true, $"Report saved: {p}"); });
        var openData = new Button { Content = "Open data folder", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        openData.Click += (_, _) => SysCmd.Open(AppPaths.Root);
        var restartExplorer = new Button { Content = "Restart Explorer", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Applies Explorer-related tweaks without signing out. Open File Explorer windows will close." };
        restartExplorer.Click += async (_, _) =>
        {
            if (!await Shell.ConfirmAsync("Restart Windows Explorer?", "The taskbar and desktop disappear for a moment and open File Explorer windows close. Other apps are not affected.", "Restart Explorer")) return;
            await Kit.RunAsync(restartExplorer, async () =>
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } }
                await Task.Delay(1500);
                if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0) System.Diagnostics.Process.Start(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                Log.Info("explorer", "Explorer restarted by user.");
                return (true, "Explorer restarted.");
            });
        };
        row.Children.Add(report); row.Children.Add(openData); row.Children.Add(restartExplorer);
        data.Children.Add(row);
        data.Children.Add(new TextBlock { Text = $"VYTRIX {Updater.Current.ToString(3)} · Precision Performance. Zero Compromise.", Style = Ui.Style("Muted"), Margin = new Thickness(0, 16, 0, 0) });
        g.Children.Add(Kit.Card(data, "Data & reports"));

        var legal = new StackPanel();
        legal.Children.Add(Kit.KeyValue("Version", $"v{Terms.Version}"));
        legal.Children.Add(Kit.KeyValue("Accepted", s.AcceptedTermsAt is { } at && Terms.Accepted ? at.ToString("f") : "Not yet accepted"));
        legal.Children.Add(new TextBlock { Text = "Covers what VYTRIX changes, your responsibilities, the limits of rollback, privacy (no data is collected), and the warranty disclaimer.", Style = Ui.Style("Muted"), Margin = new Thickness(0, 6, 0, 10) });
        var view = Ui.IconButton("IcoInfo", "Read the Terms of Agreement", "Ghost");
        view.HorizontalAlignment = HorizontalAlignment.Left;
        view.Click += async (_, _) => await Shell.ShowTermsAsync(requireAcceptance: false);
        legal.Children.Add(view);
        g.Children.Add(Kit.Card(legal, "Terms of Agreement"));
        Body.Children.Add(g);
    }

    private static Grid Option(string title, string? desc, bool value, Action<bool> set)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        if (desc is not null) sp.Children.Add(new TextBlock { Text = desc, Style = Ui.Style("Muted") });
        g.Children.Add(sp);
        var sw = Kit.Switch(value);
        sw.Checked += (_, _) => { set(true); Settings.Save(); };
        sw.Unchecked += (_, _) => { set(false); Settings.Save(); };
        Grid.SetColumn(sw, 1);
        g.Children.Add(sw);
        return g;
    }
}
