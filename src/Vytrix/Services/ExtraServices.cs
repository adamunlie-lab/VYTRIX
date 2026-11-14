using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Vytrix.Core;

namespace Vytrix.Services;

// ═══════════════════════════════════════════════════════════════ Windows services

public enum SvcSafety { Protected, Caution, Optional }

public sealed class ServiceInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; set; } = "";
    public string StartType { get; set; } = "";
    public SvcSafety Safety { get; init; }
    public string? Advice { get; init; }
    public bool CanStop { get; init; }
    public string SafetyLabel => Safety switch { SvcSafety.Protected => "Protected", SvcSafety.Optional => "Optional", _ => "Caution" };
}

public static class WinServices
{
    // Security, update and core OS services. VYTRIX never stops or reconfigures these.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "WdNisSvc", "WdFilter", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "MDCoreSvc",
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "CryptSvc", "TrustedInstaller", "DoSvc",
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "Schedule", "Winmgmt", "PlugPlay", "Power", "ProfSvc",
        "gpsvc", "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "LanmanWorkstation", "AudioSrv", "AudioEndpointBuilder",
        "Themes", "UserManager", "CoreMessagingRegistrar", "SystemEventsBroker", "BrokerInfrastructure", "StateRepository",
        "TimeBrokerSvc", "AppXSvc", "ClipSVC", "camsvc", "DeviceInstall", "DispBrokerDesktopSvc", "KeyIso", "VaultSvc", "Wcmsvc",
        "WlanSvc", "WinHttpAutoProxySvc", "EFS", "msiserver", "SgrmBroker", "TokenBroker", "StorSvc", "FontCache", "SENS",
        "ShellHWDetection", "W32Time", "EventSystem", "Appinfo", "AppIDSvc", "CoreMessaging", "seclogon", "SSDPSRV", "LanmanServer",
        "mpsdrv", "NgcSvc", "NgcCtnrSvc", "webthreatdefsvc", "webthreatdefusersvc", "Wecsvc", "WpnService", "InstallService",
        "DsmSvc", "DevQueryBroker", "hidserv", "TextInputManagementService", "CDPSvc", "cbdhsvc", "sppsvc", "LicenseManager",
        "VYTRIX",
    };

    // Optional services with an explanation of when it is reasonable to set them to Manual.
    private static readonly Dictionary<string, string> Optional = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DiagTrack"] = "Connected User Experiences and Telemetry. Sends diagnostic data; setting Manual reduces background uploads. Does not affect updates or security.",
        ["MapsBroker"] = "Downloaded Maps Manager. Only used by the Maps app's offline maps.",
        ["Fax"] = "Fax service. Unneeded unless you send faxes from this PC.",
        ["RetailDemo"] = "Retail demo mode for store display PCs.",
        ["lfsvc"] = "Geolocation service. Apps can't get your location while it is stopped.",
        ["WSearch"] = "Windows Search indexing. Manual saves disk activity but makes file search in Start/Explorer much slower.",
        ["Spooler"] = "Print Spooler. Safe to set Manual if you never print or use 'Print to PDF'.",
        ["XboxGipSvc"] = "Xbox accessory management. Needed for Xbox controller firmware updates.",
        ["XblAuthManager"] = "Xbox Live sign-in. Needed by Game Pass and Xbox-integrated PC games.",
        ["XblGameSave"] = "Xbox cloud saves. Needed by Game Pass games for cloud saves.",
        ["WerSvc"] = "Windows Error Reporting. Manual still allows reports when an app crashes.",
        ["PhoneSvc"] = "Telephony state for the Phone Link app.",
        ["RemoteRegistry"] = "Remote Registry. Disabled by default; should stay disabled on home PCs.",
        ["TrkWks"] = "Distributed Link Tracking. Fixes shortcuts to files moved across NTFS volumes on a network.",
        ["SysMain"] = "SysMain (Superfetch) preloads frequently used apps. Keep it on for HDDs; on fast NVMe the benefit is small either way.",
        ["wisvc"] = "Windows Insider Service. Only needed for Insider builds.",
        ["WMPNetworkSvc"] = "Windows Media Player network sharing.",
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int cch, IntPtr reserved);

    private static string Resolve(string s)
    {
        if (!s.StartsWith('@')) return s;
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : s;
    }

    public static List<ServiceInfo> List()
    {
        var list = new List<ServiceInfo>();
        foreach (var sc in ServiceController.GetServices())
        {
            using (sc)
            {
                try
                {
                    var key = $@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}";
                    // Per-user service instances (e.g. cbdhsvc_1a2b3) inherit their template's protection.
                    var baseName = sc.ServiceName.Contains('_') ? sc.ServiceName[..sc.ServiceName.LastIndexOf('_')] : sc.ServiceName;
                    var isProtected = Protected.Contains(sc.ServiceName) || Protected.Contains(baseName) || sc.ServiceName.Contains('_');
                    list.Add(new ServiceInfo
                    {
                        Name = sc.ServiceName,
                        DisplayName = Resolve(sc.DisplayName),
                        Description = Resolve(Reg.Read(Hive.HKLM, key, "Description") as string ?? ""),
                        Status = sc.Status.ToString(),
                        StartType = StartTypeOf(sc.ServiceName),
                        Safety = isProtected ? SvcSafety.Protected : Optional.ContainsKey(sc.ServiceName) ? SvcSafety.Optional : SvcSafety.Caution,
                        Advice = Optional.GetValueOrDefault(sc.ServiceName),
                        CanStop = sc.CanStop,
                    });
                }
                catch { }
            }
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string StartTypeOf(string name)
    {
        var key = $@"SYSTEM\CurrentControlSet\Services\{name}";
        var start = Reg.Read(Hive.HKLM, key, "Start") as int? ?? -1;
        var delayed = Reg.Read(Hive.HKLM, key, "DelayedAutostart") as int? == 1;
        return start switch { 2 => delayed ? "Automatic (delayed)" : "Automatic", 3 => "Manual", 4 => "Disabled", 0 => "Boot", 1 => "System", _ => "Unknown" };
    }

    public static Task<(bool, string)> SetStartTypeAsync(ServiceInfo s, string type) => Task.Run<(bool, string)>(() =>
    {
        if (s.Safety == SvcSafety.Protected) return (false, $"{s.DisplayName} is a protected Windows or security service.");
        if (!Elevation.IsAdmin) return (false, "Changing a service requires administrator rights.");
        int value = type switch { "Automatic" => 2, "Manual" => 3, "Disabled" => 4, _ => -1 };
        if (value < 0) return (false, "Unknown start type.");
        try
        {
            var key = $@"SYSTEM\CurrentControlSet\Services\{s.Name}";
            var snaps = new List<RegSnapshot> { Reg.Capture(Hive.HKLM, key, "Start"), Reg.Capture(Hive.HKLM, key, "DelayedAutostart") };
            Reg.Write(Hive.HKLM, key, "Start", value, RegistryValueKind.DWord);
            if (value != 2) Reg.DeleteValue(Hive.HKLM, key, "DelayedAutostart");
            Journal.Add(new ChangeRecord
            {
                Kind = ChangeKind.Service, Target = s.Name, Snapshots = snaps,
                Title = $"Service {s.DisplayName}: {s.StartType} → {type}",
                Detail = "Startup type change takes effect at next boot. Roll back restores the previous startup type.",
            });
            s.StartType = type;
            return (true, $"{s.DisplayName} set to {type}.");
        }
        catch (Exception ex) { return (false, $"Could not change {s.DisplayName}: {ex.Message}"); }
    });

    public static Task<(bool, string)> StartStopAsync(ServiceInfo s, bool start) => Task.Run<(bool, string)>(() =>
    {
        if (s.Safety == SvcSafety.Protected) return (false, $"{s.DisplayName} is a protected Windows or security service.");
        if (!Elevation.IsAdmin) return (false, "Starting or stopping services requires administrator rights.");
        try
        {
            using var sc = new ServiceController(s.Name);
            if (start) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
            else { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
            s.Status = sc.Status.ToString();
            Log.Change("service", $"{(start ? "Started" : "Stopped")} {s.DisplayName}");
            return (true, $"{s.DisplayName} {(start ? "started" : "stopped")}.");
        }
        catch (Exception ex) { return (false, $"{s.DisplayName}: {ex.InnerException?.Message ?? ex.Message}"); }
    });
}

// ═══════════════════════════════════════════════════════════════ Installed apps

public sealed class InstalledApp
{
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public long SizeBytes { get; init; }
    public string UninstallString { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string Scope { get; init; } = "";
    public string SizeText => SizeBytes > 0 ? Fmt.Bytes(SizeBytes) : "—";
    public string DateText => InstallDate?.ToString("yyyy-MM-dd") ?? "—";
}

public static class InstalledApps
{
    public static List<InstalledApp> List()
    {
        var apps = new List<InstalledApp>();
        Read(apps, Hive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "All users");
        Read(apps, Hive.HKLM, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "All users (32-bit)");
        Read(apps, Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", "Current user");
        return apps.DistinctBy(a => (a.Name, a.Version)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Read(List<InstalledApp> apps, Hive hive, string path, string scope)
    {
        using var b = Reg.Base(hive);
        using var root = b.OpenSubKey(path);
        if (root is null) return;
        foreach (var sub in root.GetSubKeyNames())
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k is null) continue;
                var name = k.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") is string) continue;
                if (k.GetValue("ReleaseType") is string rt && (rt.Contains("Update") || rt.Contains("Hotfix"))) continue;
                DateTime? date = null;
                if (k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt)) date = dt;
                apps.Add(new InstalledApp
                {
                    Name = name.Trim(),
                    Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                    Version = k.GetValue("DisplayVersion") as string ?? "",
                    InstallDate = date,
                    SizeBytes = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0,
                    UninstallString = k.GetValue("UninstallString") as string ?? "",
                    InstallLocation = k.GetValue("InstallLocation") as string ?? "",
                    Scope = scope,
                });
            }
            catch { }
        }
    }

    /// <summary>Launches the application's own uninstaller (interactive, never silent).</summary>
    public static (bool, string) LaunchUninstaller(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.UninstallString)) return (false, "This app does not register an uninstaller. Use Settings → Apps.");
        try
        {
            var exe = StartupService.ExtractExe(app.UninstallString);
            var args = app.UninstallString.Trim();
            args = args.StartsWith('"') ? args[(args.IndexOf('"', 1) + 1)..] : args[Math.Min(args.Length, exe.Length)..];
            Process.Start(new ProcessStartInfo(exe, args.Trim()) { UseShellExecute = true });
            Log.Info("apps", $"Launched uninstaller for {app.Name}");
            return (true, $"Opened the uninstaller for {app.Name}.");
        }
        catch (Exception ex) { return (false, $"Could not start the uninstaller: {ex.Message}"); }
    }
}

// ═══════════════════════════════════════════════════════════════ Benchmark (WinSAT)

public sealed record WinSatScores(double Cpu, double Memory, double Disk, double Graphics, double D3D, double Base, int State, DateTime? When);
public sealed record BenchLine(string Label, string Value);

/// <summary>Integrates the Windows System Assessment Tool (winsat.exe), Microsoft's built-in benchmark.</summary>
public static class Benchmark
{
    public static WinSatScores? ReadScores()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT * FROM Win32_WinSAT");
            foreach (ManagementObject mo in s.Get())
            {
                DateTime? when = null;
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Performance\WinSAT\DataStore");
                if (Directory.Exists(dir))
                {
                    var latest = Directory.GetFiles(dir, "*Formal.Assessment*.xml").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    when = latest?.LastWriteTime;
                }
                return new WinSatScores(
                    Convert.ToDouble(mo["CPUScore"]), Convert.ToDouble(mo["MemoryScore"]), Convert.ToDouble(mo["DiskScore"]),
                    Convert.ToDouble(mo["GraphicsScore"]), Convert.ToDouble(mo["D3DScore"]), Convert.ToDouble(mo["WinSPRLevel"]),
                    Convert.ToInt32(mo["WinSATAssessmentState"]), when);
            }
        }
        catch (Exception ex) { Log.Warn("bench", $"Win32_WinSAT unavailable: {ex.Message}"); }
        return null;
    }

    public static async Task<(bool, List<BenchLine>, string)> RunAsync(string test, IProgress<string>? progress = null)
    {
        if (!Elevation.IsAdmin) return (false, [], "WinSAT requires administrator rights.");
        string[] args = test switch
        {
            "disk-seq" => ["disk", "-seq", "-read", "-drive", Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\', ':')],
            "disk-ran" => ["disk", "-ran", "-read", "-drive", Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\', ':')],
            "mem" => ["mem"],
            "cpu" => ["cpu", "-encryption2"],
            "formal" => ["formal"],
            _ => throw new ArgumentException(test),
        };
        progress?.Report($"Running winsat {string.Join(' ', args)} …");
        Log.Info("bench", $"winsat {string.Join(' ', args)}");
        var (code, output) = await SysCmd.RunAsync(SysCmd.SystemExe("winsat.exe"), args, 15 * 60_000);
        var lines = output.Split('\n')
            .Select(l => l.Trim()).Where(l => l.StartsWith('>') && (l.Contains("MB/s") || l.Contains("ms") && l.Contains("Latency")))
            .Select(l =>
            {
                var t = l.TrimStart('>').Trim();
                var parts = t.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length >= 2 ? new BenchLine(parts[0], string.Join("  ", parts.Skip(1))) : new BenchLine(t, "");
            }).ToList();
        return (code == 0, lines, code == 0 ? "Benchmark complete." : $"winsat exited with code {code}.");
    }
}

// ═══════════════════════════════════════════════════════════════ Optimization profiles

public sealed record OptProfile(string Id, string Name, string Tagline, string Description, IReadOnlyList<string> TweakIds, string PowerPlan);

public static class Profiles
{
    public static readonly OptProfile[] All =
    [
        new("balanced", "Balanced", "Everyday Windows",
            "Keeps Windows defaults for performance and power, and removes ads, tips and tracking that add clutter.",
            ["gaming.gamemode", "win.tips", "privacy.adid", "privacy.tailored", "win.fileext"], "balanced"),
        new("gaming", "Gaming", "Smooth, focused play",
            "Game Mode on, background recording off, flip-model for windowed games, hardware GPU scheduling where supported, and a performance power plan.",
            ["gaming.gamemode", "gaming.gamedvr", "gaming.windowedopt", "gaming.hags", "win.tips", "privacy.adid"], "performance"),
        new("competitive", "Competitive", "Minimum background activity",
            "Everything in Gaming, plus raw mouse movement, fewer visual effects, no web search in Start and no upload of updates to the internet.",
            ["gaming.gamemode", "gaming.gamedvr", "gaming.windowedopt", "gaming.hags", "gaming.mouseaccel", "vfx.transparency", "vfx.minanimate",
             "vfx.taskbaranim", "win.websearch", "win.tips", "net.deliveryopt", "perf.backgroundapps", "privacy.adid", "privacy.tailored"], "performance"),
        new("creator", "Creator", "Sustained workloads",
            "Background renders and exports keep full CPU speed, background recording off to free the video encoder, startup apps load immediately, and a performance power plan. Visual effects are left on.",
            ["perf.powerthrottling", "gaming.gamedvr", "startup.delay", "win.fileext", "win.tips", "perf.menudelay"], "performance"),
    ];

    /// <summary>The user-defined profile, built from the selections saved in Settings.</summary>
    public static OptProfile Custom => new("custom", "Custom", "Your own selection",
        "Exactly the tweaks and power plan you choose below. Nothing else is changed.",
        Settings.Current.CustomProfileTweaks.Where(id => TweakCatalog.ById(id) is not null).ToList(), Settings.Current.CustomProfilePowerPlan);

    public static OptProfile? ById(string id) => id == "custom" ? Custom : All.FirstOrDefault(p => p.Id == id);

    /// <summary>Reverts the tweaks a profile run changed, and the power plan it switched.</summary>
    public static async Task<(bool, string)> RevertAsync(OptProfile p)
    {
        var record = Journal.All.FirstOrDefault(r => r.Kind == ChangeKind.Profile && r.Target == p.Id);
        if (record is null) return (false, $"There is no record of the {p.Name} profile being applied.");
        var ids = record.Extra.GetValueOrDefault("tweaks", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int ok = 0; var failed = new List<string>();
        foreach (var id in ids)
        {
            if (TweakCatalog.ById(id) is not { } t || t.GetState() != TweakState.Applied) continue;
            var r = await TweakEngine.RevertAsync(t);
            if (r.Ok) ok++; else failed.Add(r.Message);
        }
        var plan = Journal.All.FirstOrDefault(r => r.Kind == ChangeKind.PowerPlan && !r.RolledBack && r.Detail.StartsWith($"{p.Name} profile"));
        if (plan is not null) await TweakEngine.RollbackRecordAsync(plan);
        Journal.Add(new ChangeRecord { Kind = ChangeKind.Profile, Target = p.Id + ":revert", Title = $"Reverted profile: {p.Name}", Detail = $"{ok} setting(s) restored.", Reversible = false });
        if (Settings.Current.ActiveProfile == p.Id) { Settings.Current.ActiveProfile = ""; Settings.Save(); }
        return (failed.Count == 0, failed.Count == 0 ? $"{p.Name} reverted: {ok} setting(s) restored." : $"{ok} restored, {failed.Count} failed:\n" + string.Join("\n", failed));
    }

    public sealed record PlanItem(Tweak Tweak, TweakState State, string Action);

    public static List<PlanItem> Preview(OptProfile p)
    {
        var items = new List<PlanItem>();
        foreach (var id in p.TweakIds)
        {
            if (TweakCatalog.ById(id) is not { } t) continue;
            var st = t.GetState();
            var action = st switch
            {
                TweakState.Applied => "Already set",
                TweakState.Unsupported => "Skipped — " + t.UnsupportedReason,
                _ when t.RequiresAdmin && !Elevation.IsAdmin => "Skipped — needs administrator",
                _ => "Will apply",
            };
            items.Add(new PlanItem(t, st, action));
        }
        return items;
    }

    public static PowerPlan? TargetPlan(OptProfile p) => p.PowerPlan switch
    {
        "performance" => PowerService.FindPerformancePlan(),
        "balanced" => PowerService.List().FirstOrDefault(x => x.Guid == PowerService.Balanced),
        _ => null,
    };

    public static async Task<(bool, string)> ApplyAsync(OptProfile p, IProgress<string>? progress = null)
    {
        if (Settings.Current.CreateRestorePointBeforeProfiles && Elevation.IsAdmin)
        {
            progress?.Report("Creating a restore point…");
            var rp = await RestorePoints.CreateAsync($"VYTRIX: before {p.Name} profile");
            progress?.Report(rp.Message);
        }
        var applied = new List<string>();
        var failed = new List<string>();
        foreach (var item in Preview(p).Where(i => i.Action == "Will apply"))
        {
            progress?.Report($"Applying {item.Tweak.Name}…");
            var (ok, msg) = await TweakEngine.ApplyAsync(item.Tweak, $"Part of the {p.Name} profile");
            if (ok) applied.Add(item.Tweak.Id); else failed.Add($"{item.Tweak.Name}: {msg}");
        }
        string planMsg = "";
        if (TargetPlan(p) is { } plan) { planMsg = PowerService.SetActive(plan, $"{p.Name} profile").Item2; }
        else if (p.PowerPlan == "performance") planMsg = "No High performance plan exists; add one on the Gaming page (unavailable on Modern Standby laptops).";

        Journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.Profile, Target = p.Id, Title = $"Applied profile: {p.Name}", Reversible = false,
            Detail = $"{applied.Count} settings changed. {planMsg} Each change can be rolled back individually below.",
            Extra = { ["tweaks"] = string.Join(",", applied) },
        });
        Settings.Current.ActiveProfile = p.Id;
        Settings.Save();
        var summary = $"{p.Name} applied: {applied.Count} change(s).";
        if (failed.Count > 0) summary += $" {failed.Count} could not be applied.";
        return (failed.Count == 0, summary + (failed.Count > 0 ? "\n" + string.Join("\n", failed) : ""));
    }
}

// ═══════════════════════════════════════════════════════════════ Game launch profiles

public sealed class GameProfile
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool UsePerformancePlan { get; set; } = true;
    public bool HighPriority { get; set; }
    public List<string> CloseApps { get; set; } = [];
    public DateTime? LastLaunched { get; set; }
}

public static class GameProfiles
{
    private static readonly List<GameProfile> _items = Json.Load(AppPaths.GameProfilesFile, () => new List<GameProfile>());
    public static IReadOnlyList<GameProfile> Items => _items;
    public static event Action? Changed;

    public static void Save(GameProfile p)
    {
        if (!_items.Contains(p)) _items.Add(p);
        Persist();
    }

    public static void Remove(GameProfile p) { _items.Remove(p); Persist(); }

    private static void Persist()
    {
        try { Json.Save(AppPaths.GameProfilesFile, _items); } catch (Exception ex) { Log.Error("games", "Failed to save game profiles", ex); }
        Changed?.Invoke();
    }

    /// <summary>
    /// Launches a game with its profile: switches power plan, closes the user-chosen
    /// background apps (never protected ones), raises priority if requested, and
    /// restores the previous power plan when the game exits.
    /// </summary>
    public static async Task<(bool, string)> LaunchAsync(GameProfile g, Action<string> status)
    {
        if (!File.Exists(g.ExePath)) return (false, $"Game executable not found: {g.ExePath}");
        var previousPlan = PowerService.Active;
        if (g.UsePerformancePlan && PowerService.FindPerformancePlan() is { } perf)
        {
            PowerService.SetActive(perf, $"Game profile: {g.Name}");
            status($"Power plan → {perf.Name}");
        }
        int closed = 0;
        foreach (var name in g.CloseApps)
        {
            foreach (var p in ProcessService.Snapshot().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !p.Protected))
                if ((await ProcessService.EndAsync(p, force: false)).Item1) closed++;
        }
        if (closed > 0) status($"Closed {closed} background app(s)");

        Process proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo(g.ExePath, g.Arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.ExePath)! })!;
        }
        catch (Exception ex) { return (false, $"Could not launch {g.Name}: {ex.Message}"); }
        g.LastLaunched = DateTime.Now;
        Persist();
        Log.Info("games", $"Launched {g.Name}");

        if (g.HighPriority)
        {
            await Task.Delay(3000);
            try { if (!proc.HasExited) proc.PriorityClass = ProcessPriorityClass.High; status("Priority → High"); } catch { }
        }

        _ = Task.Run(async () =>
        {
            // Some launchers hand off to a child process, so wait until no process with this exe name remains.
            var exeName = Path.GetFileNameWithoutExtension(g.ExePath);
            int quiet = 0;
            while (quiet < 3)
            {
                await Task.Delay(5000);
                quiet = Process.GetProcessesByName(exeName).Length == 0 ? quiet + 1 : 0;
            }
            if (g.UsePerformancePlan && previousPlan is not null)
            {
                PowerService.SetActive(previousPlan, $"{g.Name} exited");
                Log.Info("games", $"{g.Name} exited; restored power plan {previousPlan.Name}");
            }
        });
        return (true, $"{g.Name} launched.");
    }
}

/// <summary>Per-executable "Disable fullscreen optimizations" compatibility flag (AppCompatFlags\Layers).</summary>
public static class FullscreenOptimizations
{
    private const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string Flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public static bool IsDisabled(string exe) => (Reg.Read(Hive.HKCU, Layers, exe) as string ?? "").Contains(Flag, StringComparison.OrdinalIgnoreCase);

    public static (bool, string) Set(string exe, bool disable)
    {
        try
        {
            var snap = Reg.Capture(Hive.HKCU, Layers, exe);
            var flags = (Reg.Read(Hive.HKCU, Layers, exe) as string ?? "").Replace("~", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(f => !f.Equals(Flag, StringComparison.OrdinalIgnoreCase)).ToList();
            if (disable) flags.Add(Flag);
            if (flags.Count == 0) Reg.DeleteValue(Hive.HKCU, Layers, exe);
            else Reg.Write(Hive.HKCU, Layers, exe, "~ " + string.Join(' ', flags), RegistryValueKind.String);
            Journal.Add(new ChangeRecord
            {
                Kind = ChangeKind.FullscreenOptimization, Target = exe, Snapshots = [snap],
                Title = $"Fullscreen optimizations {(disable ? "disabled" : "enabled")}: {Path.GetFileName(exe)}",
                Detail = "Per-app compatibility flag, same as the file's Properties → Compatibility tab.",
            });
            return (true, $"Fullscreen optimizations {(disable ? "disabled" : "enabled")} for {Path.GetFileName(exe)}.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

// ═══════════════════════════════════════════════════════════════ Reports

public static class Reports
{
    public static async Task<string> ExportAsync()
    {
        var apps = await Task.Run(InstalledApps.List);
        var startup = await Task.Run(StartupService.List);
        var t = Telemetry.Instance;
        var m = t.Memory;
        var now = DateTime.Now;
        var data = new
        {
            generated = now,
            vytrix = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            system = new
            {
                computer = SystemInfo.ComputerName, model = SystemInfo.SystemModel, os = $"{SystemInfo.OsName} {SystemInfo.OsVersion}", build = SystemInfo.OsBuild,
                cpu = SystemInfo.Cpu.Brand, cores = SystemInfo.Cpu.PhysicalCores, threads = SystemInfo.Cpu.LogicalProcessors,
                gpus = SystemInfo.Gpus.Where(g => g.IsSoftware == 0).Select(g => new { g.Name, vramBytes = g.DedicatedVideoMemory, hagsSupported = g.HagsSupported, hagsEnabled = g.HagsEnabled }),
                memoryBytes = m.TotalPhys, motherboard = SystemInfo.Motherboard, bios = SystemInfo.Bios,
                disks = SystemInfo.Disks, volumes = SystemInfo.Volumes(),
                uptime = Fmt.Uptime(TimeSpan.FromMilliseconds(Native.VxNative.vx_get_uptime_ms())),
                powerPlan = PowerService.Active?.Name,
            },
            live = new
            {
                cpuPercent = t.Last.CpuPercent, gpuPercent = t.Last.GpuPercent, ramUsedBytes = m.TotalPhys - m.AvailPhys, commitBytes = m.CommitTotal,
                processes = m.ProcessCount, gpuTempC = t.Last.GpuTempC, acpiTempC = t.Last.AcpiTempC,
            },
            tweaks = TweakCatalog.All.Select(x => new { x.Id, x.Name, state = x.GetState().ToString() }),
            startup = startup.Select(s => new { s.Name, s.Publisher, s.SourceLabel, s.Enabled }),
            installedApps = apps.Select(a => new { a.Name, a.Publisher, a.Version }),
            recentChanges = Journal.All.Take(50).Select(r => new { r.Time, r.Title, r.Detail, r.RolledBack }),
        };
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(AppPaths.Reports, $"VYTRIX-Report-{stamp}.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(data, Json.Options));

        string Row(string k, object? v) => $"<tr><th>{Esc(k)}</th><td>{Esc(v?.ToString() ?? "—")}</td></tr>";
        string Temp(double c) => c < 0 ? "Unavailable" : $"{c:0} °C";
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html><head><meta charset="utf-8"><title>VYTRIX System Report</title>
            <style>body{font:14px Segoe UI,sans-serif;background:#0d0d10;color:#e6e6eb;margin:40px;max-width:1000px}
            h1{font-weight:600;letter-spacing:.2em}h1 span{color:#8b5cf6}h2{color:#a78bfa;font-weight:500;margin-top:32px;border-bottom:1px solid #26262d;padding-bottom:6px}
            table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #1d1d23;vertical-align:top}
            th{color:#8a8a96;font-weight:500;width:240px}.muted{color:#6b6b76}.ok{color:#22d3ee}</style></head><body>
            """);
        html.Append($"<h1>V<span>Y</span>TRIX</h1><p class='muted'>System report generated {now:f}</p>");
        html.Append("<h2>System</h2><table>");
        html.Append(Row("Computer", SystemInfo.ComputerName)).Append(Row("Model", SystemInfo.SystemModel)).Append(Row("Operating system", $"{SystemInfo.OsName} {SystemInfo.OsVersion} (build {SystemInfo.OsBuild})"));
        html.Append(Row("Processor", $"{SystemInfo.Cpu.Brand} — {SystemInfo.Cpu.PhysicalCores} cores / {SystemInfo.Cpu.LogicalProcessors} threads"));
        foreach (var g in SystemInfo.Gpus.Where(g => g.IsSoftware == 0)) html.Append(Row("Graphics", $"{g.Name} — {Fmt.Bytes(g.DedicatedVideoMemory)}"));
        html.Append(Row("Memory", Fmt.Bytes(m.TotalPhys))).Append(Row("Motherboard", SystemInfo.Motherboard)).Append(Row("BIOS", SystemInfo.Bios));
        foreach (var d in SystemInfo.Disks) html.Append(Row("Disk", $"{d.Model} — {Fmt.Bytes(d.Size)} {d.MediaType} ({d.BusType}), {d.Health}"));
        foreach (var v in SystemInfo.Volumes()) html.Append(Row($"Volume {v.Name}", $"{Fmt.Bytes(v.Free)} free of {Fmt.Bytes(v.Total)}"));
        html.Append(Row("Power plan", PowerService.Active?.Name)).Append(Row("Uptime", data.system.uptime)).Append("</table>");
        html.Append("<h2>Live snapshot</h2><table>");
        html.Append(Row("CPU utilisation", Fmt.Pct(t.Last.CpuPercent))).Append(Row("GPU utilisation", Fmt.Pct(t.Last.GpuPercent)));
        html.Append(Row("Memory in use", $"{Fmt.Bytes(m.TotalPhys - m.AvailPhys)} of {Fmt.Bytes(m.TotalPhys)}")).Append(Row("Committed", $"{Fmt.Bytes(m.CommitTotal)} of {Fmt.Bytes(m.CommitLimit)}"));
        html.Append(Row("GPU temperature", Temp(t.Last.GpuTempC))).Append(Row("ACPI thermal zone", Temp(t.Last.AcpiTempC))).Append(Row("Processes", m.ProcessCount)).Append("</table>");
        html.Append("<h2>Optimization state</h2><table>");
        foreach (var x in TweakCatalog.All) html.Append(Row(x.Name, x.GetState() switch { TweakState.Applied => "Applied", TweakState.Unsupported => "Not supported", TweakState.Partial => "Partially applied", _ => "Windows default" }));
        html.Append("</table><h2>Startup apps</h2><table>");
        foreach (var s in startup) html.Append(Row(s.Name, $"{(s.Enabled ? "Enabled" : "Disabled")} · {s.Publisher} · {s.SourceLabel}"));
        html.Append("</table><h2>Recent changes</h2><table>");
        foreach (var r in Journal.All.Take(50)) html.Append(Row(r.Time.ToString("g"), $"{r.Title}{(r.RolledBack ? " (rolled back)" : "")}"));
        html.Append($"</table><h2>Installed applications ({apps.Count})</h2><table>");
        foreach (var a in apps) html.Append(Row(a.Name, $"{a.Version} · {a.Publisher}"));
        html.Append("</table></body></html>");
        var htmlPath = Path.Combine(AppPaths.Reports, $"VYTRIX-Report-{stamp}.html");
        await File.WriteAllTextAsync(htmlPath, html.ToString());
        Log.Info("report", $"Exported report to {htmlPath}");
        return htmlPath;
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
}

// ═══════════════════════════════════════════════════════════════ Updates

public sealed record UpdateManifest(string Version, string Url, string Sha256, string? Notes);

/// <summary>
/// Auto-update. VYTRIX reads latest.json from the newest GitHub release, and if it is a
/// higher version, downloads the installer in the background (GitHub hosts only),
/// verifies its SHA-256 and offers a one-click install. Installing always needs the
/// user's confirmation and the Windows administrator prompt; nothing installs silently
/// behind their back.
/// </summary>
public static class Updater
{
    /// <summary>The GitHub owner/repo that publishes VYTRIX releases.</summary>
    public const string Repository = "adamunlie-lab/VYTRIX";
    /// <summary>Always points at the newest release's manifest.</summary>
    public const string DefaultFeed = $"https://github.com/{Repository}/releases/latest/download/latest.json";

    // Installer downloads are only accepted from GitHub release hosting.
    private static readonly string[] TrustedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public static string UpdatesFolder => Directory.CreateDirectory(Path.Combine(AppPaths.Root, "updates")).FullName;

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"VYTRIX/{Current.ToString(3)}");
        return http;
    }

    /// <summary>Adds a unique query parameter (keeping any existing query) to bypass CDN caches.</summary>
    internal static Uri WithCacheBuster(Uri uri)
    {
        var b = new UriBuilder(uri);
        var stamp = $"vx={DateTime.UtcNow.Ticks}";
        b.Query = string.IsNullOrEmpty(b.Query) || b.Query == "?" ? stamp : b.Query.TrimStart('?') + "&" + stamp;
        return b.Uri;
    }

    internal static bool IsTrustedDownload(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && TrustedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses a manifest and decides whether it is a newer, acceptable update.</summary>
    internal static (UpdateManifest? Manifest, string Message) Evaluate(string json, Version current)
    {
        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(json, Json.Options); }
        catch (JsonException) { return (null, "The update feed returned an invalid manifest."); }
        if (m is null || !Version.TryParse(m.Version, out var v)) return (null, "The update feed returned an invalid manifest.");
        if (string.IsNullOrWhiteSpace(m.Sha256) || m.Sha256.Length != 64) return (null, "The update manifest has no valid SHA-256 checksum; ignoring it.");
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var u) || !IsTrustedDownload(u)) return (null, "The update points outside GitHub release hosting; ignoring it.");
        return v > current ? (m, $"VYTRIX {m.Version} is available.") : (null, $"You're up to date (v{current.ToString(3)}).");
    }

    public static async Task<(UpdateManifest? Manifest, string Message)> CheckAsync()
    {
        var url = Settings.Current.UpdateFeedUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return (null, "No update channel is configured.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return (null, "The update feed must be an HTTPS URL.");
        try
        {
            using var http = Client(TimeSpan.FromSeconds(20));
            // GitHub briefly caches the "latest" redirect; ask for a fresh copy so new releases are seen at once.
            using var req = new HttpRequestMessage(HttpMethod.Get, WithCacheBuster(uri));
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var resp = await http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, "No VYTRIX release has been published yet.");
            resp.EnsureSuccessStatusCode();
            var r = Evaluate(await resp.Content.ReadAsStringAsync(), Current);
            Log.Info("update", r.Message);
            return r;
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"Update check failed: {ex.Message}");
            return (null, $"Update check failed: {ex.Message}");
        }
    }

    /// <summary>Path of an already downloaded and verified installer for this version, if any.</summary>
    public static string? ReadyInstaller(UpdateManifest m)
    {
        var file = Path.Combine(UpdatesFolder, $"VYTRIX-Setup-{m.Version}.exe");
        if (!File.Exists(file)) return null;
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase) ? file : null;
    }

    /// <summary>
    /// Starts the verified installer in update mode (progress window only, no wizard pages).
    /// Windows shows its administrator prompt; if the user declines, nothing changes.
    /// </summary>
    public static (bool Ok, string Message) LaunchInstaller(string file)
    {
        try
        {
            // /SILENT shows only a progress window; /VYTRIXUPDATE=1 makes setup reopen VYTRIX afterwards.
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /VYTRIXUPDATE=1") { UseShellExecute = true });
            Log.Change("update", $"Started update installer {Path.GetFileName(file)}.");
            return (true, "Installing the update. VYTRIX will reopen when it's done.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "The update was cancelled at the Windows administrator prompt. You can install it later from Settings → Updates.");
        }
        catch (Exception ex) { return (false, $"Could not start the update: {ex.Message}"); }
    }

    public static async Task<(bool, string)> DownloadAndVerifyAsync(UpdateManifest m, IProgress<double>? progress = null)
    {
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var uri) || !IsTrustedDownload(uri)) return (false, "Update downloads are only accepted from GitHub release hosting.");
        if (ReadyInstaller(m) is { } ready) return (true, ready);
        var file = Path.Combine(UpdatesFolder, $"VYTRIX-Setup-{m.Version}.exe");
        var partial = file + ".partial";
        using var http = Client(TimeSpan.FromMinutes(15));
        try
        {
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            // Redirects (GitHub → its asset CDN) must also stay on trusted hosts.
            if (resp.RequestMessage?.RequestUri is { } final && !IsTrustedDownload(final))
                return (false, $"The download was redirected to an untrusted host ({final.Host}).");
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(partial))
            {
                var buf = new byte[81920]; long read = 0; int n;
                while ((n = await src.ReadAsync(buf)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n)); read += n; if (total > 0) progress?.Report(read * 100.0 / total); }
            }
            string hash;
            await using (var fs = File.OpenRead(partial)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!hash.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                Log.Warn("update", "Downloaded update failed SHA-256 verification and was deleted.");
                return (false, "The download failed integrity verification and was deleted.");
            }
            File.Move(partial, file, true);
            // Old installers are no longer needed.
            foreach (var old in Directory.GetFiles(UpdatesFolder, "VYTRIX-Setup-*.exe").Where(f => !f.Equals(file, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(old); } catch { }
            Log.Info("update", $"Downloaded and verified VYTRIX {m.Version} (SHA-256 {hash[..12]}…).");
            return (true, file);
        }
        catch (Exception ex)
        {
            try { File.Delete(partial); } catch { }
            Log.Warn("update", $"Update download failed: {ex.Message}");
            return (false, $"Update download failed: {ex.Message}");
        }
    }
}
