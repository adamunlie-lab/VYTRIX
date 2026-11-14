using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

public sealed class GamingPage : PageBase
{
    public override string PageTitle => "Gaming";
    public override string PageSubtitle => "Windows gaming features, power plans and per-game launch profiles. No placebo FPS tweaks.";

    private readonly StackPanel _plans = new();
    private readonly StackPanel _games = new();
    private readonly StackPanel _overlays = new();
    private readonly StackPanel _gpuInfo = new();

    // Launchers, overlays and sync tools commonly running while gaming. Shown for information; ending them is optional.
    private static readonly Dictionary<string, string> GameAdjacent = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steamwebhelper.exe"] = "Steam browser components", ["steam.exe"] = "Steam", ["EpicGamesLauncher.exe"] = "Epic Games Launcher",
        ["Battle.net.exe"] = "Battle.net", ["EADesktop.exe"] = "EA app", ["GalaxyClient.exe"] = "GOG Galaxy", ["RiotClientServices.exe"] = "Riot Client",
        ["Overwolf.exe"] = "Overwolf overlay", ["Discord.exe"] = "Discord", ["Spotify.exe"] = "Spotify", ["OneDrive.exe"] = "OneDrive sync",
        ["ms-teams.exe"] = "Microsoft Teams", ["Teams.exe"] = "Microsoft Teams", ["NVIDIA Overlay.exe"] = "NVIDIA overlay", ["UbisoftConnect.exe"] = "Ubisoft Connect",
        ["upc.exe"] = "Ubisoft Connect", ["XboxPcApp.exe"] = "Xbox app", ["GameBar.exe"] = "Xbox Game Bar",
    };

    protected override void Build()
    {
        Body.Children.Add(Kit.Section("Windows gaming features"));
        var g = Kit.Grid(380, 2);
        foreach (var t in TweakCatalog.All.Where(t => t.Category == TweakCategory.Gaming)) g.Children.Add(new TweakView(t));
        Body.Children.Add(g);

        Body.Children.Add(Kit.Section("Graphics"));
        var gg = Kit.Grid(380, 2);
        gg.Children.Add(Kit.Card(_gpuInfo, "GPU scheduling status", "Read directly from the display driver (D3DKMT)"));
        var fso = new StackPanel();
        fso.Children.Add(new TextBlock { Text = "Fullscreen optimizations let Windows run \"exclusive fullscreen\" games through the desktop compositor, enabling fast Alt-Tab, overlays and Auto HDR with the same latency as true exclusive mode on modern Windows.", Style = Ui.Style("Dim"), FontSize = 12.5 });
        fso.Children.Add(new TextBlock { Text = "Only disable them for a specific game if it shows stutter, wrong refresh rate or overlay problems. VYTRIX applies this per game (in a launch profile below), never globally.", Style = Ui.Style("Dim"), FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0) });
        gg.Children.Add(Kit.Card(fso, "Fullscreen optimizations", "What it is and when to change it"));
        Body.Children.Add(gg);

        Body.Children.Add(Kit.Section("Power plan"));
        Body.Children.Add(Kit.Card(_plans, "Power plans", "Switching is instant and reversible. Higher-performance plans use more power and produce more heat."));

        Body.Children.Add(Kit.Section("Game launch profiles"));
        var add = Ui.IconButton("IcoPlus", "Add game", "Primary");
        add.Click += async (_, _) => await EditGame(null);
        Body.Children.Add(Kit.Card(_games, "Launch profiles", "Launch a game with a power plan, optional high priority, and chosen background apps closed. The previous plan is restored when the game exits.", add));

        Body.Children.Add(Kit.Section("Launchers & overlays"));
        var refresh = Ui.IconButton("IcoRefresh", "Refresh", "Ghost");
        refresh.Click += (_, _) => _ = LoadOverlaysAsync();
        Body.Children.Add(Kit.Card(_overlays, "Game-related background apps", "Launchers, overlays and sync tools currently running. Closing them frees memory and avoids overlay conflicts; they do not usually affect FPS.", refresh));
    }

    public override void OnShown()
    {
        BuildGpuInfo();
        BuildPlans();
        BuildGames();
        _ = LoadOverlaysAsync();
    }

    private void BuildGpuInfo()
    {
        _gpuInfo.Children.Clear();
        var gpus = SystemInfo.Gpus.Where(x => x.IsSoftware == 0).ToList();
        if (gpus.Count == 0) { _gpuInfo.Children.Add(Kit.EmptyState("IcoAlert", "No hardware GPU", "Windows reported no hardware display adapter.")); return; }
        foreach (var x in gpus)
        {
            _gpuInfo.Children.Add(new TextBlock { Text = x.Name, Style = Ui.Style("H3"), Margin = new Thickness(0, 4, 0, 4) });
            _gpuInfo.Children.Add(Kit.KeyValue("Dedicated memory", Fmt.Bytes(x.DedicatedVideoMemory)));
            _gpuInfo.Children.Add(Kit.KeyValue("HAGS supported", x.HagsSupported switch { 1 => "Yes", 0 => "No", _ => "Unknown (driver did not report)" }));
            _gpuInfo.Children.Add(Kit.KeyValue("HAGS active now", x.HagsEnabled switch { 1 => "Yes", 0 => "No", _ => "Unknown" }));
        }
        _gpuInfo.Children.Add(new TextBlock { Text = "\"Active now\" reflects the current boot. After changing the setting, it updates following a restart.", Style = Ui.Style("Muted"), Margin = new Thickness(0, 8, 0, 0) });
    }

    private void BuildPlans()
    {
        _plans.Children.Clear();
        var plans = PowerService.List();
        if (plans.Count == 0) { _plans.Children.Add(Kit.EmptyState("IcoAlert", "No power plans found", "Windows did not return any power schemes.")); return; }
        foreach (var p in plans)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new TextBlock { Text = p.Name, FontSize = 13, FontWeight = p.IsActive ? FontWeights.SemiBold : FontWeights.Normal });
            if (p.IsActive) { var b = Ui.Badge("Active", "AccentHoverBrush", "AccentSoftBrush"); b.Margin = new Thickness(10, 0, 0, 0); name.Children.Add(b); }
            sp.Children.Add(name);
            sp.Children.Add(new TextBlock { Text = PowerService.Describe(p), Style = Ui.Style("Muted") });
            g.Children.Add(sp);
            if (!p.IsActive)
            {
                var act = new Button { Content = "Activate", Style = Ui.Style("Ghost"), VerticalAlignment = VerticalAlignment.Center };
                act.Click += async (_, _) => { await Kit.RunAsync(act, () => Task.FromResult(PowerService.SetActive(p))); BuildPlans(); };
                Grid.SetColumn(act, 1);
                g.Children.Add(act);
            }
            _plans.Children.Add(g);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        bool hasHigh = plans.Any(p => p.Guid == PowerService.HighPerformance || p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase));
        bool hasUlt = plans.Any(p => p.Guid == PowerService.Ultimate || p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
        if (!hasHigh) row.Children.Add(AddPlanButton("Add High performance", PowerService.HighPerformance));
        if (!hasUlt) row.Children.Add(AddPlanButton("Add Ultimate Performance", PowerService.Ultimate));
        if (row.Children.Count > 0) _plans.Children.Add(row);
        if (SystemInfo.IsLaptop) _plans.Children.Add(Kit.Notice("This is a laptop. Performance plans reduce battery life noticeably; Balanced is recommended on battery.", "info"));
    }

    private Button AddPlanButton(string text, string guid)
    {
        var b = Ui.IconButton("IcoPlus", text, "Ghost");
        b.Margin = new Thickness(0, 0, 8, 0);
        b.ToolTip = "Copies Windows' built-in (hidden) plan so you can select it. Delete it later from Control Panel → Power Options.";
        b.Click += async (_, _) => { await Kit.RunAsync(b, () => { var r = PowerService.AddBuiltIn(guid); return Task.FromResult((r.Item1, r.Item2)); }); BuildPlans(); };
        return b;
    }

    // ───────────────────────── Game profiles

    private void BuildGames()
    {
        _games.Children.Clear();
        if (GameProfiles.Items.Count == 0)
        {
            _games.Children.Add(Kit.EmptyState("IcoGaming", "No game profiles yet", "Add a game to launch it with a performance power plan and your chosen background apps closed."));
            return;
        }
        foreach (var gp in GameProfiles.Items)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = gp.Name, Style = Ui.Style("H3") });
            var bits = new List<string>();
            if (gp.UsePerformancePlan) bits.Add("performance plan");
            if (gp.HighPriority) bits.Add("high priority");
            if (gp.CloseApps.Count > 0) bits.Add($"closes {gp.CloseApps.Count} app(s)");
            if (File.Exists(gp.ExePath) && FullscreenOptimizations.IsDisabled(gp.ExePath)) bits.Add("fullscreen optimizations off");
            sp.Children.Add(new TextBlock { Text = bits.Count > 0 ? string.Join(" · ", bits) : "Launch only", Style = Ui.Style("Muted") });
            sp.Children.Add(new TextBlock { Text = File.Exists(gp.ExePath) ? gp.ExePath : $"Missing: {gp.ExePath}", Style = Ui.Style("Muted"), FontSize = 11, Foreground = Ui.Res(File.Exists(gp.ExePath) ? "TextMutedBrush" : "BadBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(sp);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var launch = Ui.IconButton("IcoPlay", "Launch", "Primary");
            launch.IsEnabled = File.Exists(gp.ExePath);
            launch.Click += async (_, _) => await Kit.RunAsync(launch, () => GameProfiles.LaunchAsync(gp, s => Shell.Toast(s)));
            var edit = new Button { Content = "Edit", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            edit.Click += async (_, _) => await EditGame(gp);
            var del = new Button { Content = "Remove", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            del.Click += async (_, _) =>
            {
                if (!await Shell.ConfirmAsync($"Remove {gp.Name}?", "Only the launch profile is removed. The game itself is not touched.", "Remove")) return;
                GameProfiles.Remove(gp);
                BuildGames();
            };
            actions.Children.Add(launch); actions.Children.Add(edit); actions.Children.Add(del);
            Grid.SetColumn(actions, 1);
            g.Children.Add(actions);
            _games.Children.Add(g);
        }
    }

    private async Task EditGame(GameProfile? existing)
    {
        var gp = existing ?? new GameProfile();
        var body = new StackPanel { Width = 470 };
        TextBox Field(string label, string value, string ph)
        {
            body.Children.Add(new TextBlock { Text = label, Style = Ui.Style("Label"), Margin = new Thickness(0, 10, 0, 4) });
            var tb = new TextBox { Text = value, Tag = ph };
            body.Children.Add(tb);
            return tb;
        }
        var name = Field("NAME", gp.Name, "e.g. Valorant");
        body.Children.Add(new TextBlock { Text = "GAME EXECUTABLE", Style = Ui.Style("Label"), Margin = new Thickness(0, 10, 0, 4) });
        var exeRow = new Grid();
        exeRow.ColumnDefinitions.Add(new ColumnDefinition());
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var exe = new TextBox { Text = gp.ExePath, Tag = @"C:\Games\Game\game.exe" };
        exeRow.Children.Add(exe);
        var browse = new Button { Content = "Browse…", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose the game executable" };
            if (dlg.ShowDialog() == true) { exe.Text = dlg.FileName; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(dlg.FileName); }
        };
        Grid.SetColumn(browse, 1);
        exeRow.Children.Add(browse);
        body.Children.Add(exeRow);
        var args = Field("LAUNCH ARGUMENTS (OPTIONAL)", gp.Arguments, "");
        var perf = new CheckBox { Content = "Switch to a performance power plan while playing", IsChecked = gp.UsePerformancePlan, Margin = new Thickness(0, 14, 0, 0) };
        var prio = new CheckBox { Content = "Set process priority to High", IsChecked = gp.HighPriority, Margin = new Thickness(0, 8, 0, 0), ToolTip = "Gives the game priority over other normal-priority apps. Avoid with anti-cheat games that forbid priority changes." };
        bool fsoNow = File.Exists(gp.ExePath) && FullscreenOptimizations.IsDisabled(gp.ExePath);
        var fso = new CheckBox { Content = "Disable fullscreen optimizations for this game", IsChecked = fsoNow, Margin = new Thickness(0, 8, 0, 0), ToolTip = "Same as the Compatibility tab of the exe's Properties. Only use if the game stutters or has refresh-rate issues." };
        body.Children.Add(perf); body.Children.Add(prio); body.Children.Add(fso);

        body.Children.Add(new TextBlock { Text = "CLOSE THESE APPS BEFORE LAUNCH", Style = Ui.Style("Label"), Margin = new Thickness(0, 14, 0, 4) });
        var running = ProcessService.Snapshot().Where(p => !p.Protected && p.Class is ProcClass.Background or ProcClass.App)
            .Select(p => p.Name).Union(gp.CloseApps, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        var checks = new WrapPanel();
        foreach (var n in running)
            checks.Children.Add(new CheckBox { Content = n, IsChecked = gp.CloseApps.Contains(n, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 4, 14, 4), Width = 210 });
        body.Children.Add(new ScrollViewer { Content = checks, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = "Apps are asked to close normally first. Windows and security components are never listed.", Style = Ui.Style("Muted"), Margin = new Thickness(0, 6, 0, 0) });

        if (!await Shell.ConfirmAsync(existing is null ? "Add game profile" : $"Edit {gp.Name}", body, "Save")) return;
        if (string.IsNullOrWhiteSpace(name.Text) || !File.Exists(exe.Text) || !exe.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Shell.Toast("A name and an existing .exe file are required.", ToastKind.Error);
            return;
        }
        gp.Name = name.Text.Trim();
        gp.ExePath = exe.Text.Trim();
        gp.Arguments = args.Text.Trim();
        gp.UsePerformancePlan = perf.IsChecked == true;
        gp.HighPriority = prio.IsChecked == true;
        gp.CloseApps = checks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
        GameProfiles.Save(gp);
        if ((fso.IsChecked == true) != FullscreenOptimizations.IsDisabled(gp.ExePath))
        {
            var r = FullscreenOptimizations.Set(gp.ExePath, fso.IsChecked == true);
            Shell.Toast(r.Item2, r.Item1 ? ToastKind.Success : ToastKind.Error);
        }
        Shell.Toast($"{gp.Name} saved.", ToastKind.Success);
        BuildGames();
    }

    private async Task LoadOverlaysAsync()
    {
        _overlays.Children.Clear();
        var procs = await Task.Run(() => ProcessService.Snapshot().Where(p => GameAdjacent.ContainsKey(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => (Name: g.Key, Items: g.ToList(), Mem: g.Sum(x => (double)x.WorkingSet))).OrderByDescending(x => x.Mem).ToList());
        if (procs.Count == 0) { _overlays.Children.Add(Kit.EmptyState("IcoCheck", "None running", "No known launchers or overlays are running right now.")); return; }
        foreach (var p in procs)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = GameAdjacent[p.Name], FontSize = 12.5 });
            sp.Children.Add(new TextBlock { Text = p.Items.Count > 1 ? $"{p.Name} ×{p.Items.Count}" : p.Name, Style = Ui.Style("Muted"), FontSize = 11 });
            g.Children.Add(sp);
            var mem = new TextBlock { Text = Fmt.Bytes(p.Mem), Style = Ui.Style("Dim"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(mem, 1); g.Children.Add(mem);
            var end = new Button { Content = "Close", Style = Ui.Style("Ghost"), Padding = new Thickness(12, 4, 12, 4) };
            end.Click += async (_, _) =>
            {
                if (!await Shell.ConfirmAsync($"Close {GameAdjacent[p.Name]}?", "The app is asked to close normally; if it doesn't respond it is ended. Unsaved data in it may be lost.", "Close app", danger: true)) return;
                await Kit.RunAsync(end, async () =>
                {
                    var rs = new List<(bool, string)>();
                    foreach (var i in p.Items.Where(i => !i.Protected)) rs.Add(await ProcessService.EndAsync(i, force: false));
                    return rs.Count > 0 && rs.All(r => r.Item1) ? (true, $"{GameAdjacent[p.Name]} closed.") : rs.FirstOrDefault(r => !r.Item1) is { Item2: not null } f ? f : (false, "Nothing was closed.");
                });
                await LoadOverlaysAsync();
            };
            Grid.SetColumn(end, 2); g.Children.Add(end);
            _overlays.Children.Add(g);
        }
    }
}
