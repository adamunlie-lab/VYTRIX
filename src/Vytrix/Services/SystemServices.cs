using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Vytrix.Core;
using Vytrix.Native;

namespace Vytrix.Services;

// ═══════════════════════════════════════════════════════════════ Processes

public enum ProcClass { App, Background, Windows, Service }

public sealed class ProcInfo
{
    public uint Pid { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public ulong WorkingSet { get; init; }
    public ulong PrivateBytes { get; init; }
    public uint Threads { get; init; }
    public ProcClass Class { get; init; }
    public bool Protected { get; init; }
    public string Publisher { get; set; } = "";
    public string ClassLabel => Class switch { ProcClass.App => "App", ProcClass.Background => "Background", ProcClass.Windows => "Windows", _ => "Service" };
    public string MemoryText => Fmt.Bytes(WorkingSet);
    public string PrivateText => Fmt.Bytes(PrivateBytes);
}

public static class ProcessService
{
    // Processes VYTRIX will never offer to end: core OS, shell, and security components.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe", "ctfmon.exe", "explorer.exe",
        "RuntimeBroker.exe", "audiodg.exe", "taskhostw.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe",
        "SearchHost.exe", "TextInputHost.exe", "conhost.exe", "WUDFHost.exe", "spoolsv.exe", "dllhost.exe", "SecurityHealthService.exe",
        "SecurityHealthSystray.exe", "MsMpEng.exe", "NisSrv.exe", "MpDefenderCoreService.exe", "smartscreen.exe", "SgrmBroker.exe",
        "wlanext.exe", "LsaIso.exe", "Secure System", "VYTRIX.exe", "WmiPrvSE.exe", "TrustedInstaller.exe", "TiWorker.exe",
        "MoUsoCoreWorker.exe", "usocoreworker.exe", "SystemSettings.exe", "ApplicationFrameHost.exe", "LogonUI.exe", "userinit.exe",
    };

    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly Dictionary<string, string> PublisherCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsProtected(string name) => Protected.Contains(name);

    public static List<ProcInfo> Snapshot(bool withPublisher = false)
    {
        var list = new List<ProcInfo>();
        foreach (var p in VxNative.EnumProcesses())
        {
            bool isWin = p.Path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(p.Path) && p.SessionId == 0);
            var cls = p.HasVisibleWindow == 1 ? ProcClass.App
                : p.SessionId == 0 ? (isWin ? ProcClass.Windows : ProcClass.Service)
                : isWin ? ProcClass.Windows : ProcClass.Background;
            var info = new ProcInfo
            {
                Pid = p.Pid, Name = p.Name, Path = p.Path, WorkingSet = p.WorkingSet, PrivateBytes = p.PrivateBytes,
                Threads = p.ThreadCount, Class = cls, Protected = Protected.Contains(p.Name) || isWin || p.SessionId == 0,
            };
            if (withPublisher) info.Publisher = PublisherOf(p.Path);
            list.Add(info);
        }
        return list;
    }

    public static string PublisherOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        lock (PublisherCache)
            if (PublisherCache.TryGetValue(path, out var c)) return c;
        string result;
        try { result = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? ""; } catch { result = ""; }
        lock (PublisherCache) PublisherCache[path] = result;
        return result;
    }

    /// <summary>Asks a process to close (graceful for windowed apps), then terminates it if requested.</summary>
    public static async Task<(bool, string)> EndAsync(ProcInfo p, bool force)
    {
        if (p.Protected) return (false, $"{p.Name} is a protected Windows or security component and cannot be ended from VYTRIX.");
        try
        {
            using var proc = Process.GetProcessById((int)p.Pid);
            if (!force && proc.MainWindowHandle != IntPtr.Zero && proc.CloseMainWindow())
            {
                if (await Task.Run(() => proc.WaitForExit(5000))) { Log.Info("process", $"Closed {p.Name} ({p.Pid}) gracefully."); return (true, $"{p.Name} closed."); }
            }
            proc.Kill();
            await Task.Run(() => proc.WaitForExit(3000));
            Log.Info("process", $"Ended {p.Name} ({p.Pid}).");
            return (true, $"{p.Name} ended.");
        }
        catch (ArgumentException) { return (true, $"{p.Name} had already exited."); }
        catch (Exception ex) { return (false, $"Could not end {p.Name}: {ex.Message}"); }
    }
}

// ═══════════════════════════════════════════════════════════════ Startup

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed class StartupItem
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public StartupSource Source { get; init; }
    public bool Enabled { get; set; }
    public string Publisher { get; init; } = "";
    public bool TargetMissing { get; init; }
    public bool NeedsAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;
    public string SourceLabel => Source switch
    {
        StartupSource.UserRun => "Registry (current user)",
        StartupSource.MachineRun => "Registry (all users)",
        StartupSource.MachineRun32 => "Registry (all users, 32-bit)",
        StartupSource.UserFolder => "Startup folder (current user)",
        _ => "Startup folder (all users)",
    };
    public string StatusLabel => Enabled ? "Enabled" : "Disabled";
}

/// <summary>
/// Manages startup entries exactly the way Task Manager does: entries are never
/// deleted, only marked disabled in Explorer's StartupApproved keys. Fully reversible.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        ReadRun(items, Hive.HKCU, RunKey, StartupSource.UserRun);
        ReadRun(items, Hive.HKLM, RunKey, StartupSource.MachineRun);
        ReadRun(items, Hive.HKLM, Run32Key, StartupSource.MachineRun32);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder);
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static int EnabledCount() { try { return List().Count(i => i.Enabled); } catch { return -1; } }

    private static void ReadRun(List<StartupItem> items, Hive hive, string key, StartupSource src)
    {
        using var b = Reg.Base(hive);
        using var k = b.OpenSubKey(key);
        if (k is null) return;
        foreach (var name in k.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var cmd = k.GetValue(name) as string ?? "";
            var exe = ExtractExe(cmd);
            items.Add(new StartupItem
            {
                Name = name, Command = cmd, TargetPath = exe, Source = src, Enabled = IsEnabled(src, name),
                Publisher = ProcessService.PublisherOf(exe), TargetMissing = exe.Length > 0 && !File.Exists(exe),
            });
        }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, StartupSource src)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fn = System.IO.Path.GetFileName(file);
            if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : file;
            items.Add(new StartupItem
            {
                Name = fn, Command = file, TargetPath = target, Source = src, Enabled = IsEnabled(src, fn),
                Publisher = ProcessService.PublisherOf(target), TargetMissing = target.Length > 0 && !File.Exists(target),
            });
        }
    }

    private static string ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return "";
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string path = sc.TargetPath;
            return Environment.ExpandEnvironmentVariables(path ?? "");
        }
        catch { return ""; }
    }

    public static string ExtractExe(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : cmd.Trim('"');
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
    }

    private static (Hive, string) ApprovedLocation(StartupSource src) => src switch
    {
        StartupSource.UserRun => (Hive.HKCU, Approved + @"\Run"),
        StartupSource.MachineRun => (Hive.HKLM, Approved + @"\Run"),
        StartupSource.MachineRun32 => (Hive.HKLM, Approved + @"\Run32"),
        StartupSource.UserFolder => (Hive.HKCU, Approved + @"\StartupFolder"),
        _ => (Hive.HKLM, Approved + @"\StartupFolder"),
    };

    private static bool IsEnabled(StartupSource src, string name)
    {
        var (h, p) = ApprovedLocation(src);
        // Odd first byte (e.g. 0x03) means disabled; missing value means enabled.
        return Reg.Read(h, p, name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    public static Task<(bool, string)> SetEnabledAsync(StartupItem item, bool enabled) => Task.Run<(bool, string)>(() =>
    {
        if (item.NeedsAdmin && !Elevation.IsAdmin) return (false, $"\"{item.Name}\" starts for all users; changing it needs administrator rights.");
        try
        {
            var (h, p) = ApprovedLocation(item.Source);
            var snap = Reg.Capture(h, p, item.Name);
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            Reg.Write(h, p, item.Name, data, RegistryValueKind.Binary);
            item.Enabled = enabled;
            Journal.Add(new ChangeRecord
            {
                Kind = ChangeKind.Startup, Target = item.Name, Snapshots = [snap],
                Title = $"{(enabled ? "Enabled" : "Disabled")} startup app: {item.Name}",
                Detail = $"{item.SourceLabel}. The entry itself was not deleted.",
            });
            return (true, $"{item.Name} will {(enabled ? "" : "no longer ")}start with Windows.");
        }
        catch (Exception ex) { return (false, $"Could not change {item.Name}: {ex.Message}"); }
    });
}

// ═══════════════════════════════════════════════════════════════ Power

public sealed record PowerPlan(string Guid, string Name, bool IsActive);

public static class PowerService
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static List<PowerPlan> List() => VxNative.EnumPowerSchemes().Select(s => new PowerPlan(s.Guid, s.Name, s.IsActive == 1)).ToList();
    public static PowerPlan? Active => List().FirstOrDefault(p => p.IsActive);

    public static string Describe(PowerPlan p) => p.Guid switch
    {
        Balanced => "Windows default. Boosts the CPU when needed and saves power when idle. Recommended for most people.",
        HighPerformance => "Keeps the CPU at higher minimum clocks and disables some power saving. Small responsiveness gains, more heat and power.",
        Ultimate => "Removes remaining power-management latency. Intended for workstations; on gaming desktops the difference from High performance is usually negligible.",
        PowerSaver => "Reduces performance to save energy.",
        _ => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ? "Copy of Ultimate Performance." :
             p.Name.Contains("High", StringComparison.OrdinalIgnoreCase) ? "Copy of High performance." : "Custom or manufacturer power plan.",
    };

    public static (bool, string) SetActive(PowerPlan plan, string reason = "Changed from VYTRIX")
    {
        var prev = Active;
        if (prev?.Guid == plan.Guid) return (true, $"{plan.Name} is already active.");
        if (VxNative.vx_set_power_scheme(plan.Guid) != 1) return (false, $"Windows refused to activate {plan.Name}.");
        Journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.PowerPlan, Target = "active", Title = $"Power plan: {plan.Name}",
            Detail = $"{reason}. Previous plan: {prev?.Name ?? "unknown"}.", Extra = { ["previous"] = prev?.Guid ?? Balanced },
        });
        return (true, $"Power plan switched to {plan.Name}.");
    }

    /// <summary>Makes a hidden built-in plan (High performance / Ultimate) available by duplicating it.</summary>
    public static (bool, string, PowerPlan?) AddBuiltIn(string sourceGuid)
    {
        var buf = new char[64];
        if (VxNative.vx_duplicate_power_scheme(sourceGuid, buf, buf.Length) != 1)
            return (false, "Windows did not allow this plan to be added. On Modern Standby laptops only Balanced is available.", null);
        var guid = new string(buf).TrimEnd('\0');
        var plan = List().FirstOrDefault(p => p.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));
        Log.Change("power", $"Added power plan {plan?.Name} ({guid}).");
        return (true, $"{plan?.Name ?? "Plan"} added.", plan);
    }

    public static PowerPlan? FindPerformancePlan()
    {
        var plans = List();
        return plans.FirstOrDefault(p => p.Guid == HighPerformance)
            ?? plans.FirstOrDefault(p => p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase))
            ?? plans.FirstOrDefault(p => p.Guid == Ultimate || p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
    }
}

// ═══════════════════════════════════════════════════════════════ Network

public sealed class AdapterInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public long SpeedBps { get; init; }
    public string Mac { get; init; } = "";
    public List<string> IPv4 { get; init; } = [];
    public List<string> IPv6 { get; init; } = [];
    public List<string> Gateways { get; init; } = [];
    public List<string> Dns { get; init; } = [];
    public bool DhcpEnabled { get; init; }
    public bool DnsIsStatic { get; init; }
    public string SpeedText => SpeedBps <= 0 ? "Unavailable" : SpeedBps >= 1_000_000_000 ? $"{SpeedBps / 1e9:0.#} Gbps" : $"{SpeedBps / 1e6:0} Mbps";
    public string DnsMode => DnsIsStatic ? "Manual" : "Automatic (DHCP)";
}

public sealed record DnsPreset(string Name, string Primary, string Secondary, string Note);

public sealed class PingResult
{
    public string Target { get; init; } = "";
    public int Sent { get; set; }
    public int Received { get; set; }
    public List<uint> Rtts { get; } = [];
    public string? Error { get; set; }
    public double LossPct => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
    public double Avg => Rtts.Count == 0 ? -1 : Rtts.Average(r => (double)r);
    public double Min => Rtts.Count == 0 ? -1 : Rtts.Min();
    public double Max => Rtts.Count == 0 ? -1 : Rtts.Max();
    public double Jitter => Rtts.Count < 2 ? 0 : Rtts.Zip(Rtts.Skip(1), (a, b) => Math.Abs((double)a - b)).Average();
}

public static class NetworkService
{
    public static readonly DnsPreset[] Presets =
    [
        new("Automatic (from router)", "", "", "Use the DNS your router or ISP provides. Windows default."),
        new("Cloudflare", "1.1.1.1", "1.0.0.1", "Fast public resolver with a strong privacy policy."),
        new("Google Public DNS", "8.8.8.8", "8.8.4.4", "Large, reliable public resolver."),
        new("Quad9", "9.9.9.9", "149.112.112.112", "Blocks known malicious domains. Non-profit."),
        new("Cloudflare (malware blocking)", "1.1.1.2", "1.0.0.2", "Cloudflare with malware filtering."),
    ];

    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // Skip NDIS/WFP filter-driver bindings (e.g. "Ethernet-QoS Packet Scheduler-0000"): they mirror the real adapter.
            if (System.Text.RegularExpressions.Regex.IsMatch(ni.Name, @"-\d{4}$")) continue;
            var ip = ni.GetIPProperties();
            if (!ip.UnicastAddresses.Any() && !ip.GatewayAddresses.Any()) continue;
            bool dhcp = false;
            try { dhcp = ip.GetIPv4Properties()?.IsDhcpEnabled ?? false; } catch { }
            var ns = Reg.Read(Hive.HKLM, $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ni.Id}", "NameServer") as string;
            list.Add(new AdapterInfo
            {
                Id = ni.Id, Name = ni.Name, Description = ni.Description,
                Type = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    _ => ni.NetworkInterfaceType.ToString(),
                },
                SpeedBps = ni.Speed,
                Mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                IPv4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList(),
                IPv6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList(),
                Gateways = ip.GatewayAddresses.Where(g => !g.Address.Equals(IPAddress.Any)).Select(g => g.Address.ToString()).ToList(),
                Dns = ip.DnsAddresses.Select(d => d.ToString()).ToList(),
                DhcpEnabled = dhcp,
                DnsIsStatic = !string.IsNullOrWhiteSpace(ns),
            });
        }
        // Physical adapters with a gateway first.
        return list.OrderByDescending(a => a.Gateways.Count > 0).ThenBy(a => a.Type == "Ethernet" ? 0 : 1).ToList();
    }

    public static async Task<PingResult> PingAsync(string host, int count, IProgress<PingResult>? progress = null, CancellationToken ct = default)
    {
        var r = new PingResult { Target = host };
        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            var (status, rtt) = await Task.Run(() => (VxNative.vx_ping(host, 2000, out var t), t));
            r.Sent++;
            if (status == 0) { r.Received++; r.Rtts.Add(rtt); }
            else if (status == 2) { r.Error = "Could not resolve host."; break; }
            progress?.Report(r);
            await Task.Delay(250, ct).ContinueWith(_ => { });
        }
        return r;
    }

    private static string Netsh => SysCmd.SystemExe("netsh.exe");

    public static async Task<(bool, string)> SetDnsAsync(AdapterInfo a, DnsPreset preset)
    {
        if (!Elevation.IsAdmin) return (false, "Changing DNS servers requires administrator rights.");
        var record = new ChangeRecord
        {
            Kind = ChangeKind.Dns, Target = a.Id, Title = $"DNS on {a.Name}: {preset.Name}",
            Detail = $"Previous: {(a.DnsIsStatic ? string.Join(", ", a.Dns) : "Automatic")}",
            Extra = { ["adapter"] = a.Name, ["wasStatic"] = a.DnsIsStatic.ToString(), ["previous"] = string.Join(",", a.Dns.Where(d => !d.Contains(':'))) },
        };
        (int Code, string Output) r;
        if (string.IsNullOrEmpty(preset.Primary))
            r = await SysCmd.RunAsync(Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={a.Name}", "source=dhcp"]);
        else
        {
            r = await SysCmd.RunAsync(Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={a.Name}", "source=static", $"address={preset.Primary}", "register=primary", "validate=no"]);
            if (r.Code == 0 && preset.Secondary.Length > 0)
                r = await SysCmd.RunAsync(Netsh, ["interface", "ipv4", "add", "dnsservers", $"name={a.Name}", $"address={preset.Secondary}", "index=2", "validate=no"]);
        }
        if (r.Code != 0) return (false, $"netsh reported an error: {r.Output}");
        await SysCmd.RunAsync(SysCmd.SystemExe("ipconfig.exe"), ["/flushdns"]);
        Journal.Add(record);
        return (true, $"DNS for {a.Name} set to {preset.Name}. IPv4 only; IPv6 DNS is unchanged.");
    }

    public static async Task<(bool, string)> RollbackDnsAsync(ChangeRecord r)
    {
        if (!Elevation.IsAdmin) return (false, "Restoring DNS servers requires administrator rights.");
        var name = r.Extra.GetValueOrDefault("adapter", "");
        var wasStatic = bool.TryParse(r.Extra.GetValueOrDefault("wasStatic"), out var ws) && ws;
        var prev = r.Extra.GetValueOrDefault("previous", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        (int Code, string Output) res;
        if (!wasStatic || prev.Length == 0)
            res = await SysCmd.RunAsync(Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={name}", "source=dhcp"]);
        else
        {
            res = await SysCmd.RunAsync(Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={name}", "source=static", $"address={prev[0]}", "register=primary", "validate=no"]);
            for (int i = 1; i < prev.Length && res.Code == 0; i++)
                res = await SysCmd.RunAsync(Netsh, ["interface", "ipv4", "add", "dnsservers", $"name={name}", $"address={prev[i]}", $"index={i + 1}", "validate=no"]);
        }
        if (res.Code != 0) return (false, $"netsh reported an error: {res.Output}");
        Journal.MarkRolledBack(r);
        return (true, $"DNS for {name} restored.");
    }

    public static async Task<(bool, string)> FlushDnsAsync()
    {
        var r = await SysCmd.RunAsync(SysCmd.SystemExe("ipconfig.exe"), ["/flushdns"]);
        Log.Info("network", "Flushed DNS resolver cache.");
        return (r.Code == 0, r.Code == 0 ? "DNS resolver cache cleared." : r.Output);
    }

    public static async Task<(bool, string)> RenewIpAsync()
    {
        var r = await SysCmd.RunAsync(SysCmd.SystemExe("ipconfig.exe"), ["/renew"], 60_000);
        Log.Info("network", "Requested DHCP lease renewal.");
        return (r.Code == 0, r.Code == 0 ? "IP address lease renewed." : "Renewal finished with warnings: " + r.Output.Split('\n').LastOrDefault());
    }

    public static async Task<(bool, string)> ResetWinsockAsync()
    {
        if (!Elevation.IsAdmin) return (false, "Resetting the network stack requires administrator rights.");
        var r = await SysCmd.RunAsync(Netsh, ["winsock", "reset"]);
        Journal.Add(new ChangeRecord { Kind = ChangeKind.Tweak, Target = "net.winsockreset", Title = "Reset Winsock catalog", Detail = "Restores the Windows socket catalog to defaults. A restart is required.", Reversible = false });
        return (r.Code == 0, r.Code == 0 ? "Winsock reset. Restart your PC to finish." : r.Output);
    }
}
