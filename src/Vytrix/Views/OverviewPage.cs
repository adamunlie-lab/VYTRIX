using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Native;
using Vytrix.Services;

namespace Vytrix.Views;

/// <summary>A live metric tile: label, large value, detail lines and a history graph.</summary>
internal sealed class LiveTile
{
    public readonly Border Root;
    public readonly TextBlock Value = new() { Style = Ui.Style("Number") };
    public readonly TextBlock Unit = new() { Style = Ui.Style("Muted"), FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 5) };
    public readonly TextBlock Name = new() { Style = Ui.Style("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    public readonly StackPanel Details = new() { Margin = new Thickness(0, 10, 0, 0) };
    public readonly Sparkline Graph = new() { Height = 54, Margin = new Thickness(0, 12, 0, 0) };

    public LiveTile(string label, string icon, string accentKey)
    {
        Graph.Stroke = Ui.Res(accentKey);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.IconPath(icon, 15, accentKey));
        head.Children.Add(new TextBlock { Text = label, Style = Ui.Style("H3"), Margin = new Thickness(9, 0, 0, 0) });
        var val = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        val.Children.Add(Value);
        val.Children.Add(Unit);
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(Name);
        Name.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(val);
        sp.Children.Add(Graph);
        sp.Children.Add(Details);
        Root = new Border { Style = Ui.Style("Card"), Child = sp };
    }

    public TextBlock Line(string key)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = key, Style = Ui.Style("Muted"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), ToolTip = key });
        var v = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        Details.Children.Add(g);
        return v;
    }
}

public sealed class OverviewPage : PageBase
{
    public override string PageTitle => "Overview";
    public override string PageSubtitle => "Live system status. Every reading comes from Windows or your hardware; anything unavailable is labelled as such.";

    private LiveTile _cpu = null!, _gpu = null!, _ram = null!, _net = null!;
    private TextBlock _cpuTemp = null!, _cpuClock = null!, _gpuTemp = null!, _gpuVram = null!, _ramAvail = null!, _ramCommit = null!, _netDown = null!, _netUp = null!, _diskIo = null!;
    private readonly Dictionary<string, TextBlock> _info = [];
    private readonly StackPanel _health = new();
    private readonly StackPanel _optStatus = new();
    private int _slowTick;

    protected override void Build()
    {
        HeaderActions.Children.Add(MakeQuick("IcoExport", "Export report", async b => await Kit.RunAsync(b, async () =>
        {
            var path = await Reports.ExportAsync();
            SysCmd.Open(path);
            return (true, $"Report saved to {path}");
        })));

        Body.Children.Add(BuildHero());
        ThemeManager.Changed += () => { ApplyHeroTheme(); UpdateHeroAnimation(); };
        MainWindow.SceneAnimationChanged += _ => UpdateHeroAnimation();
        IsVisibleChanged += (_, _) => UpdateHeroAnimation();

        var tiles = Kit.Grid(215, 4);
        _cpu = new LiveTile("Processor", "IcoHardware", "AccentHoverBrush");
        _cpu.Name.Text = SystemInfo.Cpu.Brand;
        _cpu.Unit.Text = "%";
        _cpuTemp = _cpu.Line("Temperature");
        _cpuClock = _cpu.Line("Cores / threads");
        _cpuClock.Text = $"{SystemInfo.Cpu.PhysicalCores} / {SystemInfo.Cpu.LogicalProcessors}";

        var gpu = SystemInfo.PrimaryGpu;
        _gpu = new LiveTile("Graphics", "IcoGaming", "Accent2Brush");
        _gpu.Name.Text = gpu?.Name ?? "No hardware GPU detected";
        _gpu.Unit.Text = "%";
        _gpuTemp = _gpu.Line("Temperature");
        _gpuVram = _gpu.Line("Dedicated memory");

        _ram = new LiveTile("Memory", "IcoMemory", "GoodBrush");
        _ram.Name.Text = $"{Fmt.Bytes(SystemInfo.TotalMemory)} installed";
        _ram.Unit.Text = "%";
        _ramAvail = _ram.Line("Available");
        _ramCommit = _ram.Line("Committed");

        _net = new LiveTile("Network & disk", "IcoNetwork", "WarnBrush");
        _net.Name.Text = "All adapters combined";
        _net.Graph.AutoScale = true;
        _netDown = _net.Line("Download");
        _netUp = _net.Line("Upload");
        _diskIo = _net.Line("Disk R / W");
        foreach (var t in new[] { _cpu, _gpu, _ram, _net }) tiles.Children.Add(t.Root);
        Body.Children.Add(tiles);

        // System info strip
        Body.Children.Add(Kit.Section("System"));
        var info = Kit.Grid(200, 6, 10);
        foreach (var (key, icon) in new[] { ("Windows", "IcoOverview"), ("Uptime", "IcoClock"), ("Power plan", "IcoBolt"), ("Background processes", "IcoList"), ("Startup apps", "IcoStartup"), ("System drive", "IcoStorage") })
        {
            var v = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
            _info[key] = v;
            var sp = new StackPanel();
            var h = new StackPanel { Orientation = Orientation.Horizontal };
            h.Children.Add(Ui.IconPath(icon, 13, "TextMutedBrush"));
            h.Children.Add(new TextBlock { Text = key, Style = Ui.Style("Muted"), Margin = new Thickness(7, 0, 0, 0) });
            sp.Children.Add(h);
            sp.Children.Add(v);
            info.Children.Add(new Border { Style = Ui.Style("Card"), Padding = new Thickness(14, 12, 14, 12), Child = sp });
        }
        Body.Children.Add(info);

        // Health + optimisation status + quick actions
        Body.Children.Add(Kit.Section("Status"));
        var status = Kit.Grid(320, 3);
        status.Children.Add(Kit.Card(_health, "System health", "Checks based on live readings"));
        status.Children.Add(Kit.Card(_optStatus, "Optimization status", "What VYTRIX has changed"));
        var qa = new StackPanel();
        qa.Children.Add(QuickAction("IcoOptimize", "Apply an optimization profile", "Review every change before it's made", () => Shell.Navigate("optimization")));
        qa.Children.Add(QuickAction("IcoTrash", "Clean temporary files", "Preview first, nothing personal is touched", () => Shell.Navigate("storage")));
        qa.Children.Add(QuickAction("IcoShield", "Create a restore point", "Windows System Restore snapshot", () => Shell.Navigate("restore")));
        qa.Children.Add(QuickAction("IcoStartup", "Review startup apps", "Speed up sign-in", () => Shell.Navigate("startup")));
        status.Children.Add(Kit.Card(qa, "Quick actions"));
        Body.Children.Add(status);

        _info["Windows"].Text = $"{SystemInfo.OsName.Replace("Windows ", "")} {SystemInfo.OsVersion}";
        _info["Windows"].ToolTip = $"{SystemInfo.OsName} {SystemInfo.OsVersion}, build {SystemInfo.OsBuild}";
    }

    // ───────────────────────── Theme hero banner

    private Border? _hero;
    private readonly ContentControl _heroArt = new() { Focusable = false, IsHitTestVisible = false };
    private SakuraTree? _heroSakura;
    private ChristmasTree? _heroXmas;
    private FireworksSky? _heroFireworks;
    private Viewbox? _heroSakuraBox, _heroXmasBox, _heroFireworksBox;
    private FrameworkElement? _heroGraphite;
    private readonly TextBlock _heroBrand = new() { Style = Ui.Style("Label"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _greeting = new() { Style = Ui.Style("H1"), FontSize = 30 };
    private readonly TextBlock _heroStatus = new() { Style = Ui.Style("Dim"), FontSize = 14, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _heroActions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
    private string? _lastHealth;

    private Border BuildHero()
    {
        var grid = new Grid();
        grid.Children.Add(_heroArt);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32, 0, 340, 0) };
        _heroBrand.Foreground = Ui.Res("AccentHoverBrush");
        text.Children.Add(_heroBrand);
        text.Children.Add(_greeting);
        text.Children.Add(_heroStatus);
        text.Children.Add(new TextBlock
        {
            Text = $"{SystemInfo.OsName} {SystemInfo.OsVersion}  ·  {SystemInfo.Cpu.Brand}  ·  {SystemInfo.PrimaryGpu?.Name ?? "No GPU"}",
            Style = Ui.Style("Muted"), Margin = new Thickness(0, 12, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        });
        text.Children.Add(_heroActions);
        grid.Children.Add(text);
        _hero = new Border
        {
            Height = 248, CornerRadius = new CornerRadius(16), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 18),
            BorderBrush = Ui.Res("BorderStrongBrush"), BorderThickness = new Thickness(1), Child = grid,
        };
        // Rounded clipping so the artwork respects the banner's corners.
        _hero.SizeChanged += (_, e) => _hero.Clip = new RectangleGeometry(new Rect(e.NewSize), 16, 16);
        CarolPlayer.Instance.StateChanged += () => Dispatcher.BeginInvoke(BuildHeroActions);
        ApplyHeroTheme();
        return _hero;
    }

    /// <summary>Swaps the banner's artwork, colours and copy for the active theme.</summary>
    private void ApplyHeroTheme()
    {
        if (_hero is null) return;
        var theme = ThemeManager.Current;
        switch (theme)
        {
            case ThemeManager.Christmas:
                _heroXmas ??= new ChristmasTree();
                _heroXmasBox ??= new Viewbox
                {
                    Child = _heroXmas, Stretch = Stretch.Uniform, Height = 282,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 44, -16),
                };
                _heroArt.Content = _heroXmasBox;
                _hero.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0xF4, 0x12, 0x2E, 0x24), 0),
                        new GradientStop(Color.FromArgb(0xF4, 0x0A, 0x18, 0x14), 0.6),
                        new GradientStop(Color.FromArgb(0xF4, 0x2A, 0x10, 0x14), 1),
                    },
                };
                _heroBrand.Text = "VYTRIX  ·  CHRISTMAS EDITION";
                break;
            case ThemeManager.NewYear:
                _heroFireworks ??= new FireworksSky();
                _heroFireworksBox ??= new Viewbox
                {
                    Child = _heroFireworks, Stretch = Stretch.Uniform, Height = 270,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 24, -10),
                };
                _heroArt.Content = _heroFireworksBox;
                _hero.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0xF4, 0x16, 0x1C, 0x3E), 0),
                        new GradientStop(Color.FromArgb(0xF4, 0x0A, 0x0D, 0x22), 0.6),
                        new GradientStop(Color.FromArgb(0xF4, 0x2A, 0x20, 0x10), 1),
                    },
                };
                _heroBrand.Text = $"VYTRIX  ·  NEW YEAR {FireworksSky.CelebratedYear(DateTime.Now)}";
                break;
            case ThemeManager.CherryBlossom:
                _heroSakura ??= new SakuraTree();
                _heroSakuraBox ??= new Viewbox
                {
                    Child = _heroSakura, Stretch = Stretch.Uniform, Height = 285,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 36, -4),
                };
                _heroArt.Content = _heroSakuraBox;
                _hero.Background = new LinearGradientBrush(Color.FromArgb(0xF0, 0x2A, 0x16, 0x26), Color.FromArgb(0xF0, 0x16, 0x0D, 0x15), 90);
                _heroBrand.Text = "VYTRIX  ·  CHERRY BLOSSOM";
                break;
            default:
                _heroGraphite ??= BuildGraphiteArt();
                _heroArt.Content = _heroGraphite;
                _hero.Background = new LinearGradientBrush(Color.FromRgb(0x17, 0x15, 0x22), Color.FromRgb(0x0D, 0x0D, 0x11), 25);
                _heroBrand.Text = "VYTRIX  ·  PRECISION PERFORMANCE";
                break;
        }
        UpdateGreeting(_lastHealth);
        BuildHeroActions();
    }

    /// <summary>Graphite artwork: a large, softly glowing logo mark.</summary>
    private static FrameworkElement BuildGraphiteArt()
    {
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Right, Width = 360 };
        g.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 320, Height = 320, Margin = new Thickness(0, -40, -60, 0),
            Fill = new RadialGradientBrush(Color.FromArgb(0x55, 0x8B, 0x5C, 0xF6), Color.FromArgb(0x00, 0x8B, 0x5C, 0xF6)),
        });
        var mark = new System.Windows.Shapes.Path { Data = Ui.Icon("LogoMark"), Stretch = Stretch.Uniform, Width = 150, Height = 130, Opacity = 0.9 };
        mark.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "LogoBrush");
        g.Children.Add(mark);
        return g;
    }

    private void BuildHeroActions()
    {
        _heroActions.Children.Clear();
        if (ThemeManager.Current == ThemeManager.Christmas)
        {
            var player = CarolPlayer.Instance;
            var play = Ui.IconButton(player.IsPlaying ? "IcoPause" : "IcoMusic", player.IsPlaying ? "Pause carols" : "Play carols", "Primary");
            play.ToolTip = "Music-box arrangements of public-domain carols, synthesized by VYTRIX on your PC.";
            play.Click += async (_, _) => await player.ToggleAsync();
            _heroActions.Children.Add(play);
            if (player.IsPlaying || player.IsPreparing)
            {
                var now = new TextBlock { Text = player.IsPreparing ? "Preparing carols…" : $"♪  {player.CurrentTitle}", Style = Ui.Style("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
                _heroActions.Children.Add(now);
                if (player.IsPlaying)
                {
                    var next = new Button { Content = "Next", Style = Ui.Style("Ghost"), Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
                    next.Click += (_, _) => player.Next();
                    _heroActions.Children.Add(next);
                }
            }
        }
    }

    private void UpdateHeroAnimation()
    {
        bool run = MainWindow.SceneAnimating && IsVisible;
        _heroSakura?.SetRunning(run && ThemeManager.Current == ThemeManager.CherryBlossom);
        _heroXmas?.SetRunning(run && ThemeManager.Current == ThemeManager.Christmas);
        _heroFireworks?.SetRunning(run && ThemeManager.Current == ThemeManager.NewYear);
    }

    private void UpdateGreeting(string? health)
    {
        _lastHealth = health;
        var now = DateTime.Now;
        var h = now.Hour;
        string greeting = h < 5 ? "Good night" : h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
        if (ThemeManager.Current == ThemeManager.Christmas)
        {
            var christmas = new DateTime(now.Month == 12 && now.Day > 26 ? now.Year + 1 : now.Year, 12, 25);
            int days = (christmas.Date - now.Date).Days;
            greeting = now.Month == 12 && now.Day == 25 ? "Merry Christmas" : now.Month == 12 && now.Day == 24 ? "Happy Christmas Eve"
                : now.Month == 12 && now.Day == 26 ? "Happy Boxing Day" : days <= 60 ? $"{days} days until Christmas" : "Season's greetings";
        }
        if (ThemeManager.Current == ThemeManager.NewYear)
        {
            int year = FireworksSky.CelebratedYear(now);
            var newYear = new DateTime(year, 1, 1);
            int days = (newYear - now.Date).Days;
            greeting = now.Month == 1 && now.Day == 1 ? "Happy New Year!"
                : now.Month == 12 && now.Day == 31 ? "Happy New Year's Eve"
                : days > 0 && days <= 60 ? $"{days} days until {year}" : $"Happy {year}";
        }
        _greeting.Text = greeting;
        _heroStatus.Text = health ?? "Checking your system…";
    }

    private static Button MakeQuick(string icon, string text, Func<Button, Task> onClick)
    {
        var b = Ui.IconButton(icon, text, "Ghost");
        b.Click += async (_, _) => await onClick(b);
        return b;
    }

    private static Button QuickAction(string icon, string title, string sub, Action go)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var ib = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Ui.Res("AccentSoftBrush"), Child = Ui.IconPath(icon, 15, "AccentHoverBrush") };
        g.Children.Add(ib);
        var sp = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        sp.Children.Add(new TextBlock { Text = sub, Style = Ui.Style("Muted"), FontSize = 11.5 });
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        var b = new Button { Content = g, Style = Ui.Style("Ghost"), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 4) };
        b.Click += (_, _) => go();
        // left-align content inside the templated button
        g.HorizontalAlignment = HorizontalAlignment.Left;
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        return b;
    }

    public override void OnShown()
    {
        Telemetry.Instance.Updated += Refresh;
        _slowTick = 0;
        Refresh();
    }

    public override void OnHidden() => Telemetry.Instance.Updated -= Refresh;

    private void Refresh()
    {
        var t = Telemetry.Instance;
        if (!t.HasSample)
        {
            foreach (var tile in new[] { _cpu, _gpu, _ram, _net }) tile.Value.Text = "…";
            foreach (var tb in new[] { _cpuTemp, _gpuTemp, _gpuVram, _ramAvail, _ramCommit, _netDown, _netUp, _diskIo }) tb.Text = "Measuring…";
            return;
        }
        var s = t.Last;
        var m = t.Memory;

        _cpu.Value.Text = s.CpuPercent < 0 ? "N/A" : $"{s.CpuPercent:0}";
        _cpu.Graph.SetValues(t.CpuHistory);
        _cpuTemp.Text = s.AcpiTempC < 0 ? "Unavailable" : $"{s.AcpiTempC:0} °C (ACPI zone)";
        _cpuTemp.ToolTip = s.AcpiTempC < 0
            ? "Windows does not expose a CPU temperature sensor on this PC. Most desktop motherboards only report it through vendor drivers, so VYTRIX shows nothing rather than guessing."
            : "Highest ACPI thermal zone reported by the firmware. This is a motherboard zone, which may differ from the CPU package temperature.";

        _gpu.Value.Text = s.GpuPercent < 0 ? "N/A" : $"{s.GpuPercent:0}";
        _gpu.Unit.Visibility = s.GpuPercent < 0 ? Visibility.Collapsed : Visibility.Visible;
        _gpu.Graph.SetValues(t.GpuHistory);
        _gpuTemp.Text = s.GpuTempC < 0 ? "Unavailable" : $"{s.GpuTempC:0} °C";
        _gpuTemp.ToolTip = s.GpuTempC < 0 ? "GPU temperature is read through NVIDIA's NVML library. Other vendors don't expose it through a public Windows API." : "Reported by the NVIDIA driver (NVML).";
        var gpu = SystemInfo.PrimaryGpu;
        _gpuVram.Text = s.GpuDedicatedUsed < 0 ? "Unavailable" : $"{Fmt.Bytes(s.GpuDedicatedUsed)} / {Fmt.Bytes(gpu?.DedicatedVideoMemory ?? 0)}";

        double used = m.TotalPhys - m.AvailPhys;
        _ram.Value.Text = m.TotalPhys > 0 ? $"{used * 100 / m.TotalPhys:0}" : "N/A";
        _ram.Graph.SetValues(t.RamHistory);
        _ramAvail.Text = $"{Fmt.Bytes(m.AvailPhys)} of {Fmt.Bytes(m.TotalPhys)}";
        _ramCommit.Text = $"{Fmt.Bytes(m.CommitTotal)} / {Fmt.Bytes(m.CommitLimit)}";

        _net.Value.Text = Fmt.Rate(Math.Max(0, s.NetRecvBps) + Math.Max(0, s.NetSendBps));
        _net.Value.FontSize = 22;
        _net.Graph.SetValues(t.NetHistory);
        _netDown.Text = Fmt.Rate(s.NetRecvBps);
        _netUp.Text = Fmt.Rate(s.NetSendBps);
        _diskIo.Text = $"{Fmt.Rate(s.DiskReadBps)} / {Fmt.Rate(s.DiskWriteBps)}";

        _info["Uptime"].Text = Fmt.Uptime(TimeSpan.FromMilliseconds(VxNative.vx_get_uptime_ms()));
        _info["Background processes"].Text = t.TotalProcesses == 0 ? "…" : $"{t.BackgroundProcesses} of {t.TotalProcesses}";

        if (_slowTick++ % 10 == 0) _ = RefreshSlowAsync();
    }

    // Tweak states rarely change on their own: re-read them once a minute, or right after a tweak changes.
    private static List<GpuDriver>? _drivers;
    private List<(Tweak t, TweakState s)>? _tweakStates;
    private DateTime _tweakStatesAt;
    private bool _tweakHooked;

    private async Task RefreshSlowAsync()
    {
        if (!_tweakHooked) { TweakEngine.TweakChanged += _ => _tweakStates = null; _tweakHooked = true; }
        var plan = await Task.Run(() => PowerService.Active);
        var startup = await Task.Run(StartupService.EnabledCount);
        var vols = await Task.Run(SystemInfo.Volumes);
        if (_tweakStates is null || DateTime.Now - _tweakStatesAt > TimeSpan.FromMinutes(1))
        {
            _tweakStates = await Task.Run(() => TweakCatalog.All.Select(t => (t, s: t.GetState())).ToList());
            _tweakStatesAt = DateTime.Now;
        }
        var states = _tweakStates;
        _info["Power plan"].Text = plan?.Name ?? "Unavailable";
        _info["Startup apps"].Text = startup < 0 ? "Unavailable" : $"{startup} enabled";
        var sys = vols.FirstOrDefault(v => v.Name.StartsWith(System.IO.Path.GetPathRoot(Environment.SystemDirectory)![..1], StringComparison.OrdinalIgnoreCase));
        _info["System drive"].Text = sys is null ? "Unavailable" : $"{Fmt.Bytes(sys.Free)} free";

        // Health checks
        var m = Telemetry.Instance.Memory;
        var s = Telemetry.Instance.Last;
        var checks = new List<(bool ok, string title, string detail)>();
        double load = m.TotalPhys > 0 ? (m.TotalPhys - m.AvailPhys) * 100.0 / m.TotalPhys : 0;
        checks.Add((load < 85, "Memory pressure", load < 85 ? $"{load:0}% in use — healthy" : $"{load:0}% in use — close apps or check Memory"));
        double commit = m.CommitLimit > 0 ? m.CommitTotal * 100.0 / m.CommitLimit : 0;
        checks.Add((commit < 90, "Commit charge", $"{commit:0}% of the commit limit"));
        if (sys is not null) checks.Add((100 - sys.UsedPct >= 10, "System drive space", $"{100 - sys.UsedPct:0}% free{(100 - sys.UsedPct < 10 ? " — Windows needs room for updates" : "")}"));
        if (s.GpuTempC >= 0) checks.Add((s.GpuTempC < 85, "GPU temperature", $"{s.GpuTempC:0} °C"));
        var up = TimeSpan.FromMilliseconds(VxNative.vx_get_uptime_ms());
        checks.Add((up.TotalDays < 7, "Uptime", up.TotalDays < 7 ? Fmt.Uptime(up) : $"{(int)up.TotalDays} days — a restart applies pending updates"));
        if (SystemInfo.Disks.Count > 0) checks.Add((SystemInfo.Disks.All(d => d.Health is "Healthy" or "Unknown"), "Disk health", string.Join(", ", SystemInfo.Disks.Select(d => d.Health)) + " (Windows Storage report)"));
        _drivers ??= await Task.Run(GpuDrivers.Read);   // drivers don't change while VYTRIX is open
        foreach (var d in _drivers.Where(d => d.Freshness >= 0))
            checks.Add((d.Freshness < 2, "Graphics driver", $"{d.Vendor} {d.DisplayVersion}, {d.AgeText}{(d.Freshness == 2 ? " — update it on the Gaming page" : "")}"));

        _health.Children.Clear();
        int passing = checks.Count(c => c.ok);
        _health.Children.Add(new TextBlock { Text = $"{passing} of {checks.Count} checks passing", Style = Ui.Style("H3"), Margin = new Thickness(0, 0, 0, 8), Foreground = Ui.Res(passing == checks.Count ? "GoodBrush" : "WarnBrush") });
        foreach (var c in checks) _health.Children.Add(CheckRow(c.ok, c.title, c.detail));
        var failing = checks.Where(c => !c.ok).Select(c => c.title.ToLowerInvariant()).ToList();
        UpdateGreeting(failing.Count == 0
            ? $"Your PC is in good shape: all {checks.Count} health checks are passing."
            : $"{passing} of {checks.Count} health checks passing. Worth a look: {string.Join(", ", failing)}.");

        // Optimisation status
        _optStatus.Children.Clear();
        int applied = states.Count(x => x.s == TweakState.Applied);
        int supported = states.Count(x => x.s != TweakState.Unsupported);
        var profile = Profiles.All.FirstOrDefault(p => p.Id == Settings.Current.ActiveProfile)?.Name ?? (Settings.Current.ActiveProfile == "custom" ? "Custom" : "None applied");
        _optStatus.Children.Add(Kit.KeyValue("Active profile", profile));
        _optStatus.Children.Add(Kit.KeyValue("Tweaks applied", $"{applied} of {supported} supported"));
        var meter = new Meter { Value = supported == 0 ? 0 : applied * 100.0 / supported, Fill = Ui.Res("AccentBrush"), Margin = new Thickness(0, 8, 0, 12) };
        _optStatus.Children.Add(meter);
        var last = Journal.All.FirstOrDefault();
        _optStatus.Children.Add(Kit.KeyValue("Last change", last is null ? "No changes yet" : $"{last.Title}"));
        if (last is not null) _optStatus.Children.Add(Kit.KeyValue("When", last.Time.ToString("g")));
        _optStatus.Children.Add(Kit.KeyValue("Rollback available", $"{Journal.All.Count(r => r.Reversible && !r.RolledBack)} change(s)"));
    }

    private static Grid CheckRow(bool ok, string title, string detail)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Ui.Res(ok ? "GoodSoftBrush" : "WarnSoftBrush"), VerticalAlignment = VerticalAlignment.Top, Child = Ui.IconPath(ok ? "IcoCheck" : "IcoAlert", 10, ok ? "GoodBrush" : "WarnBrush") };
        g.Children.Add(dot);
        var sp = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 12.5 });
        sp.Children.Add(new TextBlock { Text = detail, Style = Ui.Style("Muted"), FontSize = 11.5 });
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        return g;
    }
}
