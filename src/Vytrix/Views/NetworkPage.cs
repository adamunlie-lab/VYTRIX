using System.Windows;
using System.Windows.Controls;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

public sealed class NetworkPage : PageBase
{
    public override string PageTitle => "Network";
    public override string PageSubtitle => "Adapter details, DNS and real latency diagnostics. Windows settings cannot lower your ping to a game server; your connection and route decide that.";

    private readonly StackPanel _adapters = new();
    private readonly StackPanel _dns = new();
    private readonly StackPanel _diag = new();
    private List<AdapterInfo> _list = [];
    private CancellationTokenSource? _pingCts;

    protected override void Build()
    {
        var refresh = Ui.IconButton("IcoRefresh", "Refresh", "Ghost");
        refresh.Click += (_, _) => Load();
        HeaderActions.Children.Add(refresh);

        Body.Children.Add(Kit.Section("Adapters"));
        Body.Children.Add(_adapters);

        Body.Children.Add(Kit.Section("Diagnostics"));
        var g = Kit.Grid(420, 2);
        g.Children.Add(Kit.Card(_diag, "Latency & packet loss", "Sends 20 ICMP echo requests to each target. Some servers ignore ping, which shows as loss."));
        g.Children.Add(Kit.Card(BuildTools(), "Troubleshooting tools", "Standard Windows network repairs, explained before they run"));
        Body.Children.Add(g);

        Body.Children.Add(Kit.Section("DNS"));
        Body.Children.Add(Kit.Card(_dns, "DNS servers", "DNS translates names like example.com into addresses. A different resolver can make lookups faster or add malware filtering. It does not change ping to a game server."));
        BuildDiagnostics();
    }

    public override void OnShown() => Load();

    public override void OnHidden() => _pingCts?.Cancel();

    private void Load()
    {
        try { _list = NetworkService.Adapters(); }
        catch (Exception ex) { _list = []; Log.Error("network", "Adapter enumeration failed", ex); }
        _adapters.Children.Clear();
        if (_list.Count == 0) { _adapters.Children.Add(Kit.Card(Kit.EmptyState("IcoNetwork", "No active network adapters", "Connect to a network to see adapter details."))); BuildDns(); return; }
        var grid = Kit.Grid(380, 2);
        foreach (var a in _list)
        {
            var sp = new StackPanel();
            sp.Children.Add(Kit.KeyValue("Type", a.Type));
            sp.Children.Add(Kit.KeyValue("Link speed", a.SpeedText, "Negotiated link rate between this PC and the router/access point. Not your internet speed."));
            sp.Children.Add(Kit.KeyValue("IPv4", a.IPv4.Count > 0 ? string.Join(", ", a.IPv4) : "None"));
            if (a.IPv6.Count > 0) sp.Children.Add(Kit.KeyValue("IPv6", string.Join("\n", a.IPv6.Take(2))));
            sp.Children.Add(Kit.KeyValue("Gateway", a.Gateways.Count > 0 ? string.Join(", ", a.Gateways) : "None"));
            sp.Children.Add(Kit.KeyValue("DNS servers", a.Dns.Count > 0 ? string.Join("\n", a.Dns) : "None"));
            sp.Children.Add(Kit.KeyValue("DNS mode", a.DnsMode));
            sp.Children.Add(Kit.KeyValue("IP assignment", a.DhcpEnabled ? "Automatic (DHCP)" : "Manual"));
            sp.Children.Add(Kit.KeyValue("MAC address", a.Mac));
            grid.Children.Add(Kit.Card(sp, a.Name, a.Description));
        }
        _adapters.Children.Add(grid);
        BuildDns();
    }

    private void BuildDns()
    {
        _dns.Children.Clear();
        var targets = _list.Where(a => a.Gateways.Count > 0).ToList();
        if (targets.Count == 0) { _dns.Children.Add(Kit.EmptyState("IcoNetwork", "No connected adapter", "DNS can be changed once an adapter has a gateway.")); return; }
        if (!Elevation.IsAdmin) _dns.Children.Add(Kit.Notice("Changing DNS requires administrator mode.", "warn"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        row.Children.Add(new TextBlock { Text = "Adapter", Style = Ui.Style("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var combo = new ComboBox { Width = 260 };
        foreach (var a in targets) combo.Items.Add(new ComboBoxItem { Content = $"{a.Name} ({a.Type})", Tag = a });
        combo.SelectedIndex = 0;
        row.Children.Add(combo);
        _dns.Children.Add(row);

        var grid = Kit.Grid(220, 5, 10);
        foreach (var p in NetworkService.Presets)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = p.Name, Style = Ui.Style("H3") });
            sp.Children.Add(new TextBlock { Text = p.Primary.Length > 0 ? $"{p.Primary}, {p.Secondary}" : "Router / ISP", Foreground = Ui.Res("AccentHoverBrush"), FontSize = 12, Margin = new Thickness(0, 3, 0, 6) });
            sp.Children.Add(new TextBlock { Text = p.Note, Style = Ui.Style("Muted"), MinHeight = 48 });
            var b = new Button { Content = "Use", Style = Ui.Style("Ghost"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = Elevation.IsAdmin };
            b.Click += async (_, _) =>
            {
                var a = (AdapterInfo)((ComboBoxItem)combo.SelectedItem).Tag;
                var msg = $"VYTRIX will run:\n\nnetsh interface ipv4 set dnsservers name=\"{a.Name}\" " + (p.Primary.Length > 0 ? $"source=static address={p.Primary} (+ {p.Secondary})" : "source=dhcp") +
                          $"\n\nCurrent DNS: {(a.DnsIsStatic ? string.Join(", ", a.Dns) : "Automatic")}. This is recorded so you can roll back from the Restore Center. Only IPv4 DNS is changed; the DNS cache is flushed afterwards.";
                if (!await Shell.ConfirmAsync($"Use {p.Name} on {a.Name}?", msg, "Change DNS")) return;
                await Kit.RunAsync(b, () => NetworkService.SetDnsAsync(a, p));
                Load();
            };
            sp.Children.Add(b);
            grid.Children.Add(new Border { Style = Ui.Style("Inset"), Child = sp });
        }
        _dns.Children.Add(grid);
    }

    private void BuildDiagnostics()
    {
        _diag.Children.Clear();
        var results = new StackPanel();
        var run = Ui.IconButton("IcoPlay", "Run diagnostics", "Primary");
        run.HorizontalAlignment = HorizontalAlignment.Left;
        run.Click += async (_, _) =>
        {
            _pingCts?.Cancel();
            _pingCts = new CancellationTokenSource();
            var ct = _pingCts.Token;
            run.IsEnabled = false;
            results.Children.Clear();
            var targets = new List<(string label, string host)>();
            var gw = _list.SelectMany(a => a.Gateways).FirstOrDefault(x => !x.Contains(':'));
            if (gw is not null) targets.Add(("Your router", gw));
            targets.Add(("Cloudflare", "1.1.1.1"));
            targets.Add(("Google", "8.8.8.8"));
            targets.Add(("DNS resolution + reachability", "www.microsoft.com"));
            try
            {
                foreach (var (label, host) in targets)
                {
                    var row = DiagRow(label, host);
                    results.Children.Add(row.Root);
                    var r = await NetworkService.PingAsync(host, 20, new Progress<PingResult>(row.Update), ct);
                    row.Update(r);
                    if (ct.IsCancellationRequested) break;
                }
                results.Children.Add(new TextBlock { Text = Interpret(results), Style = Ui.Style("Dim"), FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0) });
                Log.Info("network", "Latency diagnostics completed.");
            }
            finally { run.IsEnabled = true; }
        };
        _diag.Children.Add(run);
        _diag.Children.Add(results);
    }

    private sealed class DiagRowUi
    {
        public Border Root = null!;
        public TextBlock Stats = null!;
        public Meter Progress = null!;
        public PingResult? Last;
        public void Update(PingResult r)
        {
            Last = r;
            Progress.Value = r.Sent * 5;
            Stats.Text = r.Error ?? (r.Received == 0 ? $"No replies ({r.Sent} sent)" : $"avg {r.Avg:0} ms · min {r.Min:0} · max {r.Max:0} · jitter {r.Jitter:0.0} ms · loss {r.LossPct:0}%");
            Stats.Foreground = Ui.Res(r.Error is not null || r.LossPct > 5 ? "WarnBrush" : "TextDimBrush");
        }
    }

    private static DiagRowUi DiagRow(string label, string host)
    {
        var ui = new DiagRowUi();
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var h = new Grid();
        h.Children.Add(new TextBlock { Text = label, FontSize = 12.5 });
        h.Children.Add(new TextBlock { Text = host, Style = Ui.Style("Muted"), HorizontalAlignment = HorizontalAlignment.Right });
        sp.Children.Add(h);
        ui.Stats = new TextBlock { Text = "Testing…", Style = Ui.Style("Dim"), FontSize = 12, Margin = new Thickness(0, 3, 0, 5) };
        sp.Children.Add(ui.Stats);
        ui.Progress = new Meter { Height = 4, Fill = Ui.Res("AccentBrush") };
        sp.Children.Add(ui.Progress);
        ui.Root = new Border { Child = sp, Tag = ui };
        return ui;
    }

    private static string Interpret(StackPanel results)
    {
        var rows = results.Children.OfType<Border>().Select(b => b.Tag).OfType<DiagRowUi>().Select(r => r.Last).Where(r => r is not null).ToList();
        if (rows.Count == 0) return "";
        var router = rows[0]!;
        if (router.Received == 0 && router.Error is null) return "Your router did not reply. Some routers block ping; if web pages load fine this is not a problem.";
        if (router.LossPct > 2 || router.Jitter > 10) return "Loss or jitter is already present between this PC and your router. On Wi-Fi, move closer, use 5 GHz/6 GHz, or use Ethernet — this is the most effective fix for unstable games.";
        if (rows.Skip(1).Any(r => r!.LossPct > 2)) return "The local link is clean but there is loss further out. This is on your ISP's side or the route; contact your ISP if it persists.";
        return "Results look healthy. Latency to a game server depends on distance and routing, which Windows settings cannot change.";
    }

    private UIElement BuildTools()
    {
        var sp = new StackPanel();
        void Tool(string title, string what, string button, bool admin, bool warn, Func<Task<(bool, string)>> op)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            t.Children.Add(new TextBlock { Text = title, FontSize = 13 });
            t.Children.Add(new TextBlock { Text = what, Style = Ui.Style("Muted") });
            g.Children.Add(t);
            var b = new Button { Content = button, Style = Ui.Style("Ghost"), VerticalAlignment = VerticalAlignment.Center, IsEnabled = !admin || Elevation.IsAdmin, ToolTip = admin && !Elevation.IsAdmin ? "Requires administrator mode" : null };
            b.Click += async (_, _) =>
            {
                if (!await Shell.ConfirmAsync($"{title}?", what, button, danger: warn)) return;
                await Kit.RunAsync(b, op);
                Load();
            };
            Grid.SetColumn(b, 1);
            g.Children.Add(b);
            sp.Children.Add(g);
        }
        Tool("Flush DNS cache", "Clears cached name lookups (ipconfig /flushdns). Fixes sites that moved servers. Harmless.", "Flush", false, false, NetworkService.FlushDnsAsync);
        Tool("Renew IP address", "Asks your router for a fresh DHCP lease (ipconfig /renew). Connections drop for a few seconds.", "Renew", false, true, NetworkService.RenewIpAsync);
        Tool("Reset Winsock", "Restores the Windows socket catalog to defaults (netsh winsock reset). Fixes corruption from old VPNs or malware cleanup. Requires a restart and some VPN software may need reinstalling. Not reversible, but returns Windows to its default state.", "Reset", true, true, NetworkService.ResetWinsockAsync);
        var ts = Ui.IconButton("IcoSettings", "Open Windows network troubleshooter", "Link");
        ts.HorizontalAlignment = HorizontalAlignment.Left;
        ts.Margin = new Thickness(0, 6, 0, 0);
        ts.Click += (_, _) => SysCmd.Open("ms-settings:network-status");
        sp.Children.Add(ts);
        return sp;
    }
}
