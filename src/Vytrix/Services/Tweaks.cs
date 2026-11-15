using Microsoft.Win32;
using Vytrix.Core;
using Vytrix.Native;

namespace Vytrix.Services;

public enum Risk { Low, Moderate, Advanced }

public enum TweakCategory { Gaming, Performance, Windows, Privacy, Network, Startup, Power, VisualEffects }

public enum TweakState { Applied, NotApplied, Partial, Unsupported }

/// <summary>The hardware/system area a tweak acts on. The Tweaks page is organised by this.</summary>
public enum TweakComponent { Cpu, Gpu, Memory, Storage, Network, Input, Display, System, Privacy }

public static class Components
{
    public static readonly (TweakComponent Id, string Label, string Icon, string Blurb)[] All =
    [
        (TweakComponent.Cpu, "CPU", "IcoHardware", "Processor scheduling, power management and throttling."),
        (TweakComponent.Gpu, "GPU", "IcoGaming", "Graphics scheduling, frame presentation and the video encoder."),
        (TweakComponent.Memory, "Memory", "IcoMemory", "System RAM."),
        (TweakComponent.Storage, "Storage", "IcoStorage", "Disk space and automatic cleanup."),
        (TweakComponent.Network, "Network", "IcoNetwork", "Bandwidth use by Windows services."),
        (TweakComponent.Input, "Input", "IcoMouse", "Mouse and pointer behaviour."),
        (TweakComponent.Display, "Display & visuals", "IcoDisplay", "Desktop effects, animations and UI responsiveness."),
        (TweakComponent.System, "Windows & startup", "IcoSettings", "Search, Explorer, notifications and boot behaviour."),
        (TweakComponent.Privacy, "Privacy", "IcoShield", "Data Windows collects and shares."),
    ];

    public static string Label(TweakComponent c) => All.First(x => x.Id == c).Label;
}

/// <summary>One registry write performed by a tweak, with the value Windows ships with.</summary>
public sealed record RegOp(Hive Hive, string Path, string? Name, object? Value, RegistryValueKind Kind, object? WindowsDefault = null, bool KeyOnly = false)
{
    /// <summary>
    /// The value that means "off". Needed when Windows' default already equals the tweak's
    /// value (e.g. Game Mode is on by default): turning the tweak off must write this
    /// explicitly, because restoring "default" would leave it on.
    /// </summary>
    public object? OffValue { get; init; }

    /// <summary>When the value is absent, Windows behaves as if it were set to WindowsDefault.</summary>
    public bool MissingMeansDefault => WindowsDefault is not null;

    public static RegOp DWord(Hive h, string path, string name, int value, int? windowsDefault) => new(h, path, name, value, RegistryValueKind.DWord, windowsDefault);
    public static RegOp Str(Hive h, string path, string name, string value, string? windowsDefault) => new(h, path, name, value, RegistryValueKind.String, windowsDefault);
    public static RegOp Key(Hive h, string path) => new(h, path, null, null, RegistryValueKind.Unknown, null, true);
    public static RegOp DefaultStr(Hive h, string path, string value) => new(h, path, null, value, RegistryValueKind.String, null);

    public bool Matches()
    {
        if (KeyOnly) return Reg.KeyExists(Hive, Path);
        var cur = Reg.Read(Hive, Path, Name);
        // An absent value behaves like Windows' default; if that default IS the tweak's value, it's on.
        if (cur is null) return MissingMeansDefault && ValueEquals(WindowsDefault, Value);
        return ValueEquals(cur, Value);
    }

    private bool ValueEquals(object? cur, object? want)
    {
        if (cur is null || want is null) return false;
        return Kind switch
        {
            RegistryValueKind.DWord => cur is int i && want is int w && i == w,
            RegistryValueKind.String => cur is string s && want is string ws && string.Equals(s, ws, StringComparison.OrdinalIgnoreCase),
            _ => Equals(cur, want),
        };
    }

    public string Describe() => KeyOnly ? $@"Create key {Hive}\{Path}" : $@"{Hive}\{Path}\{Name ?? "(Default)"} = {Value}";
}

public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required TweakCategory Category { get; init; }
    public required string Description { get; init; }
    public required string ExpectedEffect { get; init; }
    public required string Explanation { get; init; }
    public Risk Risk { get; init; } = Risk.Low;
    public string Compatibility { get; init; } = "Windows 10 and Windows 11.";
    public bool RequiresAdmin { get; init; }
    public string? AppliesAfter { get; init; }               // e.g. "Restart", "Sign out", null = immediately
    public int MinBuild { get; init; }
    public int MaxBuild { get; init; } = int.MaxValue;
    public IReadOnlyList<RegOp> Ops { get; init; } = [];
    public Func<string?>? SupportCheck { get; init; }        // returns reason when unsupported
    public Action<bool>? AfterChange { get; init; }          // apply live (SPI / broadcast)
    public Func<bool?>? CustomState { get; init; }
    public Func<Task<(bool, string)>>? CustomApply { get; init; }
    public Func<Task<(bool, string)>>? CustomRevert { get; init; }

    /// <summary>Exact description of what a custom (non-Ops) tweak changes.</summary>
    public string? ChangesText { get; init; }

    /// <summary>
    /// Detects the same setting turned on through another documented route (Windows Settings,
    /// Group Policy, the driver's default), so a setting the user already enabled shows as on.
    /// </summary>
    public Func<bool>? AlsoOnWhen { get; init; }
    /// <summary>Where that other route lives, shown to the user (e.g. "Windows Settings → …").</summary>
    public string? AlsoOnSource { get; init; }
    /// <summary>Turns the setting off at that other route (journaled), if it is safe to do so.</summary>
    public Func<Task<(bool, string)>>? TurnOffElsewhere { get; init; }

    /// <summary>True when VYTRIX itself applied this tweak (an open journal record exists).</summary>
    public bool AppliedByVytrix => Journal.ActiveFor(ChangeKind.Tweak, Id) is not null;

    /// <summary>On, but not because of VYTRIX: the user (or Windows) already had it enabled.</summary>
    public bool AlreadyOnInWindows => GetState() == TweakState.Applied && !AppliedByVytrix;

    public TweakComponent Component => TweakCatalog.ComponentOf(Id);
    public string ComponentLabel => Components.Label(Component);
    public string CategoryLabel => Category == TweakCategory.VisualEffects ? "Visual effects" : Category.ToString();
    public string RiskLabel => Risk switch { Risk.Low => "Low risk", Risk.Moderate => "Moderate", _ => "Advanced" };
    public string ChangesSummary => ChangesText ?? (Ops.Count > 0 ? string.Join("\n", Ops.Select(o => o.Describe())) : "See explanation.");

    public string? UnsupportedReason
    {
        get
        {
            var build = Environment.OSVersion.Version.Build;
            if (build < MinBuild) return $"Requires Windows build {MinBuild} or newer (you have {build}).";
            if (build > MaxBuild) return "Not applicable to this version of Windows.";
            return SupportCheck?.Invoke();
        }
    }

    public TweakState GetState()
    {
        if (UnsupportedReason is not null) return TweakState.Unsupported;
        TweakState state;
        if (CustomState is not null)
        {
            var s = CustomState();
            if (s is null) return TweakState.Unsupported;
            state = s.Value ? TweakState.Applied : TweakState.NotApplied;
        }
        else
        {
            int matched = Ops.Count(o => { try { return o.Matches(); } catch { return false; } });
            state = matched == Ops.Count ? TweakState.Applied : matched == 0 ? TweakState.NotApplied : TweakState.Partial;
        }
        if (state != TweakState.Applied && IsOnElsewhere()) state = TweakState.Applied;
        return state;
    }

    public bool IsOnElsewhere()
    {
        try { return AlsoOnWhen?.Invoke() == true; } catch { return false; }
    }
}

/// <summary>Applies and reverts tweaks with full journaling and registry backups.</summary>
public static class TweakEngine
{
    public static event Action<Tweak>? TweakChanged;

    public static async Task<(bool Ok, string Message)> ApplyAsync(Tweak t, string? reason = null)
    {
        if (t.UnsupportedReason is { } why) return (false, why);
        if (t.RequiresAdmin && !Elevation.IsAdmin) return (false, $"\"{t.Name}\" changes a machine-wide setting and needs administrator rights.");
        try
        {
            if (t.CustomApply is not null)
            {
                var r = await t.CustomApply();
                if (r.Item1)
                {
                    Journal.Add(new ChangeRecord { Kind = ChangeKind.Tweak, Target = t.Id, Title = $"Enabled: {t.Name}", Detail = reason ?? r.Item2 });
                    TweakChanged?.Invoke(t);
                }
                return r;
            }

            await Task.Run(() =>
            {
                var record = new ChangeRecord { Kind = ChangeKind.Tweak, Target = t.Id, Title = $"Enabled: {t.Name}", Detail = reason ?? t.ExpectedEffect };
                foreach (var op in t.Ops) record.Snapshots.Add(Reg.Capture(op.Hive, op.Path, op.Name, op.KeyOnly));
                if (Settings.Current.ExportRegistryBeforeChanges)
                    foreach (var key in t.Ops.Select(o => (o.Hive, o.Path)).Distinct())
                        if (Reg.ExportBackup(key.Hive, key.Path, t.Id) is { } file) record.BackupFiles.Add(file);

                foreach (var op in t.Ops)
                {
                    if (op.KeyOnly) Reg.EnsureKey(op.Hive, op.Path);
                    else Reg.Write(op.Hive, op.Path, op.Name, op.Value!, op.Kind);
                }
                Journal.Add(record);
            });
            t.AfterChange?.Invoke(true);
            TweakChanged?.Invoke(t);
            return (true, t.AppliesAfter is null ? $"{t.Name} enabled." : $"{t.Name} enabled. Takes effect after: {t.AppliesAfter.ToLowerInvariant()}.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"Windows denied access while applying \"{t.Name}\". Relaunch VYTRIX as administrator.");
        }
        catch (Exception ex)
        {
            Log.Error("tweak", $"Apply failed: {t.Id}", ex);
            return (false, $"Could not apply \"{t.Name}\": {ex.Message}");
        }
    }

    /// <summary>
    /// Reverts a tweak. If VYTRIX recorded the prior state it restores exactly that;
    /// otherwise it falls back to the documented Windows default for each value.
    /// </summary>
    public static async Task<(bool Ok, string Message)> RevertAsync(Tweak t)
    {
        if (t.RequiresAdmin && !Elevation.IsAdmin) return (false, $"Reverting \"{t.Name}\" needs administrator rights.");
        try
        {
            if (t.CustomRevert is not null)
            {
                var r = await t.CustomRevert();
                if (r.Item1)
                {
                    Journal.CloseAll(ChangeKind.Tweak, t.Id);
                    Journal.Add(new ChangeRecord { Kind = ChangeKind.Tweak, Target = t.Id + ":revert", Title = $"Turned off: {t.Name}", Detail = r.Item2, Reversible = false });
                    var stillOn = await EnsureOffElsewhereAsync(t);
                    TweakChanged?.Invoke(t);
                    if (stillOn is not null) return (false, stillOn);
                }
                return r;
            }

            bool usedJournal = false;
            await Task.Run(() =>
            {
                var record = Journal.ActiveFor(ChangeKind.Tweak, t.Id);
                if (record is not null)
                {
                    foreach (var s in record.Snapshots.AsEnumerable().Reverse()) Reg.Restore(s);
                    usedJournal = true;
                }
                else
                {
                    foreach (var op in t.Ops)
                    {
                        if (op.KeyOnly) { using var b = Reg.Base(op.Hive); b.DeleteSubKeyTree(op.Path, false); }
                        else if (op.WindowsDefault is null) Reg.DeleteValue(op.Hive, op.Path, op.Name);
                        else Reg.Write(op.Hive, op.Path, op.Name, op.WindowsDefault, op.Kind);
                    }
                }
                // If the restored state is still "on" (Windows' default IS on, or it was on before
                // VYTRIX touched it), write the explicit off value so the switch really turns it off.
                var forced = new List<RegSnapshot>();
                foreach (var op in t.Ops.Where(o => o.OffValue is not null && o.Matches()))
                {
                    forced.Add(Reg.Capture(op.Hive, op.Path, op.Name));
                    Reg.Write(op.Hive, op.Path, op.Name, op.OffValue!, op.Kind);
                }
                Journal.CloseAll(ChangeKind.Tweak, t.Id);
                Journal.Add(new ChangeRecord
                {
                    Kind = ChangeKind.Tweak, Target = t.Id + ":revert", Title = $"Turned off: {t.Name}", Reversible = forced.Count > 0, Snapshots = forced,
                    Detail = usedJournal ? "Restored the exact values recorded before VYTRIX changed them." : "No prior record existed; restored Windows defaults.",
                });
            });
            t.AfterChange?.Invoke(false);
            var still = await EnsureOffElsewhereAsync(t);
            TweakChanged?.Invoke(t);
            if (still is not null) return (false, still);
            return (true, usedJournal ? $"{t.Name} restored to your previous setting." : $"{t.Name} turned off (Windows default restored).");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"Windows denied access while reverting \"{t.Name}\". Relaunch VYTRIX as administrator.");
        }
        catch (Exception ex)
        {
            Log.Error("tweak", $"Revert failed: {t.Id}", ex);
            return (false, $"Could not revert \"{t.Name}\": {ex.Message}");
        }
    }

    /// <summary>
    /// After VYTRIX's own values are reverted, the setting may still be on through another
    /// route (Windows Settings, a policy, a driver default). Turn it off there if that route
    /// is safe to change; otherwise return an honest message saying where it's still on.
    /// Returns null when the setting is now off.
    /// </summary>
    private static async Task<string?> EnsureOffElsewhereAsync(Tweak t)
    {
        if (!t.IsOnElsewhere()) return null;
        if (t.TurnOffElsewhere is not null)
        {
            var (ok, msg) = await t.TurnOffElsewhere();
            if (ok && !t.IsOnElsewhere()) return null;
            return $"\"{t.Name}\" is still on through {t.AlsoOnSource ?? "another Windows setting"}: {msg}";
        }
        return $"VYTRIX undid its own change, but \"{t.Name}\" is still on through {t.AlsoOnSource ?? "another Windows setting"}. Change it there to turn it off.";
    }

    /// <summary>Writes one value at an alternate location with a journaled snapshot (used by TurnOffElsewhere).</summary>
    internal static Task<(bool, string)> SetElsewhere(Hive hive, string path, string name, int value, string id, string what) => Task.Run<(bool, string)>(() =>
    {
        if (hive == Hive.HKLM && !Elevation.IsAdmin) return (false, "needs administrator mode");
        try
        {
            var snap = Reg.Capture(hive, path, name);
            Reg.Write(hive, path, name, value, RegistryValueKind.DWord);
            Journal.Add(new ChangeRecord { Kind = ChangeKind.Tweak, Target = id + ":elsewhere", Title = what, Detail = $@"{hive}\{path}\{name} = {value}", Snapshots = [snap] });
            return (true, what);
        }
        catch (Exception ex) { return (false, ex.Message); }
    });

    /// <summary>Rolls back one journal record (any kind that is registry-based).</summary>
    public static async Task<(bool Ok, string Message)> RollbackRecordAsync(ChangeRecord r)
    {
        if (r.RolledBack) return (true, "Already rolled back.");
        if (!r.Reversible) return (false, "This action cannot be reversed (for example, deleted temporary files).");
        try
        {
            switch (r.Kind)
            {
                case ChangeKind.Dns:
                    return await NetworkService.RollbackDnsAsync(r);
                case ChangeKind.PowerPlan:
                    if (r.Extra.TryGetValue("previous", out var prev) && VxNative.vx_set_power_scheme(prev) == 1)
                    {
                        Journal.MarkRolledBack(r);
                        return (true, "Previous power plan restored.");
                    }
                    return (false, "The previous power plan no longer exists.");
                case ChangeKind.Tweak when TweakCatalog.ById(r.Target) is { } t:
                    return await RevertAsync(t);
                default:
                    if (r.Snapshots.Any(s => s.Hive == Hive.HKLM) && !Elevation.IsAdmin)
                        return (false, "This rollback touches machine-wide settings and needs administrator rights.");
                    await Task.Run(() => { foreach (var s in r.Snapshots.AsEnumerable().Reverse()) Reg.Restore(s); });
                    Journal.MarkRolledBack(r);
                    Log.Change("rollback", $"Rolled back: {r.Title}");
                    return (true, $"Rolled back: {r.Title}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("rollback", $"Rollback failed: {r.Title}", ex);
            return (false, $"Rollback failed: {ex.Message}");
        }
    }
}

/// <summary>
/// The curated tweak library. Every entry is a documented Windows setting
/// (Settings app toggle, Group Policy, or documented registry value) with a
/// real, observable effect. No placebo "latency" or "RAM" tweaks.
/// </summary>
public static class TweakCatalog
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string ExplorerAdv = Explorer + @"\Advanced";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    public static IReadOnlyList<Tweak> All { get; } = Build();
    public static Tweak? ById(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Which component each tweak acts on (one table, so the grouping is easy to review).</summary>
    private static readonly Dictionary<string, TweakComponent> ComponentMap = new()
    {
        ["gaming.gamemode"] = TweakComponent.Cpu, ["perf.powerthrottling"] = TweakComponent.Cpu, ["cpu.minstate"] = TweakComponent.Cpu,
        ["gaming.hags"] = TweakComponent.Gpu, ["gaming.windowedopt"] = TweakComponent.Gpu, ["gpu.vrr"] = TweakComponent.Gpu, ["gaming.gamedvr"] = TweakComponent.Gpu,
        ["power.hibernate"] = TweakComponent.Storage, ["storage.storagesense"] = TweakComponent.Storage,
        ["net.deliveryopt"] = TweakComponent.Network,
        ["gaming.mouseaccel"] = TweakComponent.Input,
        ["vfx.transparency"] = TweakComponent.Display, ["vfx.minanimate"] = TweakComponent.Display, ["vfx.taskbaranim"] = TweakComponent.Display, ["perf.menudelay"] = TweakComponent.Display,
        ["perf.backgroundapps"] = TweakComponent.System, ["win.websearch"] = TweakComponent.System, ["win.tips"] = TweakComponent.System, ["win.fileext"] = TweakComponent.System,
        ["win.classicmenu"] = TweakComponent.System, ["startup.delay"] = TweakComponent.System, ["startup.faststartup"] = TweakComponent.System,
        ["privacy.adid"] = TweakComponent.Privacy, ["privacy.tailored"] = TweakComponent.Privacy, ["privacy.activity"] = TweakComponent.Privacy,
        ["privacy.diagdata"] = TweakComponent.Privacy, ["privacy.speech"] = TweakComponent.Privacy,
        // Added in 1.4
        ["gpu.pcieaspm"] = TweakComponent.Gpu,
        ["storage.diskidle"] = TweakComponent.Storage, ["storage.longpaths"] = TweakComponent.Storage, ["vfx.iconsonly"] = TweakComponent.Storage,
        ["input.stickykeys"] = TweakComponent.Input, ["input.filterkeys"] = TweakComponent.Input, ["input.togglekeys"] = TweakComponent.Input,
        ["input.keydelay"] = TweakComponent.Input, ["input.usbsuspend"] = TweakComponent.Input, ["input.gamebarbutton"] = TweakComponent.Input,
        ["vfx.dragfull"] = TweakComponent.Display, ["vfx.iconshadow"] = TweakComponent.Display, ["vfx.selectionalpha"] = TweakComponent.Display, ["vfx.peek"] = TweakComponent.Display,
        ["win.hiddenfiles"] = TweakComponent.System, ["win.thispc"] = TweakComponent.System, ["win.lockscreentips"] = TweakComponent.System, ["win.startrecs"] = TweakComponent.System,
        ["win.endtask"] = TweakComponent.System, ["win.taskview"] = TweakComponent.System, ["win.autoplay"] = TweakComponent.System, ["startup.restartapps"] = TweakComponent.System,
        ["privacy.settingsads"] = TweakComponent.Privacy, ["privacy.langlist"] = TweakComponent.Privacy, ["privacy.apptracking"] = TweakComponent.Privacy,
        ["privacy.inking"] = TweakComponent.Privacy, ["privacy.feedback"] = TweakComponent.Privacy, ["privacy.cloudsearch"] = TweakComponent.Privacy, ["privacy.searchhistory"] = TweakComponent.Privacy,
    };

    public static TweakComponent ComponentOf(string id) => ComponentMap.TryGetValue(id, out var c) ? c : TweakComponent.System;
    public static bool HasComponent(string id) => ComponentMap.ContainsKey(id);

    // Processor power management GUIDs (documented: powercfg /aliases).
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string ProcThrottleMin = "893dee8e-2bef-41e0-89c6-b55d0929964c";

    private static uint? ReadMinProcessorState() => VxNative.vx_power_read_ac(SubProcessor, ProcThrottleMin, out var v) == 1 ? v : null;

    /// <summary>Sets the active plan's plugged-in minimum processor state, journaling the previous value.</summary>
    private static Task<(bool, string)> SetMinProcessorState(bool high) => Task.Run<(bool, string)>(() =>
    {
        const string id = "cpu.minstate";
        var plan = PowerService.Active;
        if (ReadMinProcessorState() is not { } before) return (false, "Windows did not return the current processor power setting.");
        uint target;
        if (high) target = 100;
        else
        {
            // Restore the value recorded when this was turned on, for the same plan; otherwise Windows' Balanced default (5%).
            var rec = Journal.All.FirstOrDefault(r => r.Target == id + ":raw" && !r.RolledBack && r.Extra.GetValueOrDefault("plan") == plan?.Guid);
            target = rec is not null && uint.TryParse(rec.Extra.GetValueOrDefault("previous"), out var p) ? p : 5;
            if (rec is not null) Journal.MarkRolledBack(rec);
        }
        if (VxNative.vx_power_write_ac(SubProcessor, ProcThrottleMin, target) != 1)
            return (false, "Windows refused to change the power plan setting. Relaunch VYTRIX as administrator and try again.");
        if (high)
            Journal.Add(new ChangeRecord
            {
                Kind = ChangeKind.Tweak, Target = id + ":raw", Reversible = false,
                Title = $"Minimum processor state {before}% → 100% ({plan?.Name})",
                Detail = "Previous value recorded; turning the tweak off restores it.",
                Extra = { ["previous"] = before.ToString(), ["plan"] = plan?.Guid ?? "" },
            });
        return (true, $"Minimum processor state (plugged in) set to {target}% on {plan?.Name ?? "the active plan"}.");
    });

    // More documented power setting GUIDs (powercfg /query).
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3", UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20", PcieAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string SubDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442", DiskIdle = "6738e2c4-e8a5-4a42-b16a-e040e769756e";

    private static uint? ReadAc(string sub, string setting) => VxNative.vx_power_read_ac(sub, setting, out var v) == 1 ? v : null;

    /// <summary>
    /// Sets a plugged-in value on the active power plan, journaling the previous value so turning the tweak
    /// off restores exactly what was there (for the same plan); otherwise the given fallback.
    /// </summary>
    private static Task<(bool, string)> SetAcPower(string id, string sub, string setting, uint value, bool on, uint fallback, string label) => Task.Run<(bool, string)>(() =>
    {
        var plan = PowerService.Active;
        if (ReadAc(sub, setting) is not { } before) return (false, "Windows did not return the current power setting.");
        uint target = value;
        if (!on)
        {
            var rec = Journal.All.FirstOrDefault(r => r.Target == id + ":raw" && !r.RolledBack && r.Extra.GetValueOrDefault("plan") == plan?.Guid);
            target = rec is not null && uint.TryParse(rec.Extra.GetValueOrDefault("previous"), out var p) ? p : fallback;
            if (rec is not null) Journal.MarkRolledBack(rec);
        }
        if (VxNative.vx_power_write_ac(sub, setting, target) != 1)
            return (false, "Windows refused to change the power plan setting. Relaunch VYTRIX as administrator and try again.");
        if (on)
            Journal.Add(new ChangeRecord
            {
                Kind = ChangeKind.Tweak, Target = id + ":raw", Reversible = false,
                Title = $"{label}: {before} → {target} ({plan?.Name})",
                Detail = "Previous value recorded; turning the tweak off restores it.",
                Extra = { ["previous"] = before.ToString(), ["plan"] = plan?.Guid ?? "" },
            });
        return (true, $"{label} set on {plan?.Name ?? "the active plan"}.");
    });

    private static void SpiMouse(bool precisionOff)
    {
        int[] v = precisionOff ? [0, 0, 0] : [6, 10, 1];
        Win32.SystemParametersInfo(Win32.SPI_SETMOUSE, 0, v, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
    }

    private static string? HagsSupport()
    {
        var gpus = SystemInfo.Gpus.Where(g => g.IsSoftware == 0).ToList();
        if (gpus.Count == 0) return "No hardware GPU detected.";
        if (gpus.All(g => g.HagsSupported == 0)) return "Your GPU driver does not support hardware-accelerated GPU scheduling (requires WDDM 2.7+ driver).";
        return null;
    }

    /// <summary>Writes an HKCU DWORD for on/off style settings, journaling the prior value.</summary>
    private static Task<(bool, string)> SetDword(string path, string name, int value, string id, string message) => Task.Run<(bool, string)>(() =>
    {
        var snap = Reg.Capture(Hive.HKCU, path, name);
        Reg.Write(Hive.HKCU, path, name, value, RegistryValueKind.DWord);
        Journal.Add(new ChangeRecord { Kind = ChangeKind.Tweak, Target = id + ":raw", Title = $"{name} = {value}", Detail = message, Snapshots = [snap] });
        return (true, message);
    });

    private static List<Tweak> Build() =>
    [
        // ───────────────────────────── Gaming
        new()
        {
            Id = "gaming.gamemode", Name = "Game Mode", Category = TweakCategory.Gaming,
            Description = "Lets Windows prioritise the game you are playing.",
            ExpectedEffect = "Fewer background interruptions (Windows Update installs and driver installs pause; notifications are reduced) while a game is focused.",
            Explanation = "Same switch as Settings → Gaming → Game Mode. When a game runs, Windows withholds Windows Update driver installations and restart notifications and gives the game priority for CPU and GPU scheduling. On by default in Windows 11; this makes sure it hasn't been turned off.",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 1)],
            ChangesText = @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 1 (on) / 0 (off)",
            // Windows leaves the value absent when Game Mode is at its default (on), so "missing" means on.
            CustomState = () => Reg.Read(Hive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") is not int v || v != 0,
            CustomApply = () => SetDword(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, "gaming.gamemode", "Game Mode turned on."),
            CustomRevert = () => SetDword(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 0, "gaming.gamemode", "Game Mode turned off."),
        },
        new()
        {
            Id = "gaming.hags", Name = "Hardware-accelerated GPU scheduling", Category = TweakCategory.Gaming,
            Description = "Moves GPU work scheduling from the CPU to a dedicated GPU processor.",
            ExpectedEffect = "Can slightly reduce CPU overhead and input latency in GPU-bound games. Required for DLSS Frame Generation on NVIDIA RTX 40/50 GPUs.",
            Explanation = "Same switch as Settings → System → Display → Graphics → Advanced graphics settings. Only available when the GPU driver supports it (WDDM 2.7+). Results vary per game; if you see stutter or capture issues, turn it off again.",
            Risk = Risk.Moderate, RequiresAdmin = true, AppliesAfter = "Restart", MinBuild = 19041,
            Compatibility = "Windows 10 2004+ / Windows 11 with a supporting driver (NVIDIA GTX 10-series+, AMD RX 5000+, recent Intel).",
            SupportCheck = HagsSupport,
            Ops = [RegOp.DWord(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, null)],
            // Newer drivers turn HAGS on by default without writing HwSchMode; ask the driver.
            AlsoOnWhen = () => Reg.Read(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is null
                            && SystemInfo.Gpus.Any(g => g.IsSoftware == 0 && g.HagsEnabled == 1),
            AlsoOnSource = "your graphics driver's default",
            TurnOffElsewhere = () => TweakEngine.SetElsewhere(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1, "gaming.hags", "Set HAGS off (overrides the driver default)"),
        },
        new()
        {
            Id = "gaming.gamedvr", Name = "Turn off background game recording", Category = TweakCategory.Gaming,
            Description = "Stops Xbox Game Bar from continuously recording the last moments of gameplay.",
            ExpectedEffect = "Frees the GPU video encoder and some disk writes during games. Manual captures (Win+Alt+R, screenshots) keep working.",
            Explanation = "Same switch as Settings → Gaming → Captures → \"Record what happened\". Background recording keeps a rolling buffer encoded by the GPU, which costs a few percent of performance on some systems. It is off by default on Windows 11, so this often shows as already on. If you've turned off Game Bar captures entirely, background recording is off too, and VYTRIX recognises that.",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0, 0) with { OffValue = 1 }],
            // Captures disabled entirely also means no background recording.
            AlsoOnWhen = () => Reg.Read(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") is 0
                            || Reg.Read(Hive.HKCU, @"System\GameConfigStore", "GameDVR_Enabled") is 0,
            AlsoOnSource = "Xbox Game Bar captures being turned off (Settings → Gaming → Captures)",
            // Turning background recording back on means captures must be allowed again.
            TurnOffElsewhere = async () =>
            {
                var a = await TweakEngine.SetElsewhere(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1, "gaming.gamedvr", "Re-enabled Game Bar captures");
                var b = await TweakEngine.SetElsewhere(Hive.HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 1, "gaming.gamedvr", "Re-enabled Game DVR");
                return (a.Item1 && b.Item1, a.Item1 ? b.Item2 : a.Item2);
            },
        },
        new()
        {
            Id = "gaming.windowedopt", Name = "Optimizations for windowed games", Category = TweakCategory.Gaming,
            Description = "Uses the modern flip presentation model for DirectX 10/11 games running windowed or borderless.",
            ExpectedEffect = "Lower latency in borderless/windowed mode and support for Auto HDR and VRR in older DirectX games.",
            Explanation = "Same switch as Settings → System → Display → Graphics → Default graphics settings → Optimizations for windowed games. Windows upgrades legacy \"blt\" swap chains to flip-model, which bypasses extra composition copies.",
            MinBuild = 22000, Compatibility = "Windows 11 only.",
            CustomState = () => ((Reg.Read(Hive.HKCU, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings") as string) ?? "")
                .Contains("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase),
            ChangesText = @"SwapEffectUpgradeEnable=1 in HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings (other entries kept)",
            CustomApply = () => DirectXGlobal.SetAsync("SwapEffectUpgradeEnable", "1", "gaming.windowedopt"),
            CustomRevert = () => DirectXGlobal.SetAsync("SwapEffectUpgradeEnable", "0", "gaming.windowedopt"),
        },
        new()
        {
            Id = "gpu.vrr", Name = "Variable refresh rate for windowed games", Category = TweakCategory.Gaming,
            Description = "Lets DirectX 11 games that don't support VRR natively use G-SYNC / FreeSync when running windowed or borderless.",
            ExpectedEffect = "Smoother motion without tearing in those games on a VRR-capable display. No effect if your monitor doesn't support VRR.",
            Explanation = "Same switch as Settings → System → Display → Graphics → Default graphics settings → Variable refresh rate. Games that already support VRR are unaffected. Requires a G-SYNC/FreeSync/VESA Adaptive-Sync display with VRR enabled in the GPU control panel.",
            MinBuild = 18362, Compatibility = "Windows 10 1903+ / Windows 11, WDDM 2.6+ driver, VRR-capable display.",
            CustomState = () => ((Reg.Read(Hive.HKCU, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings") as string) ?? "")
                .Contains("VRROptimizeEnable=1", StringComparison.OrdinalIgnoreCase),
            ChangesText = @"VRROptimizeEnable=1 in HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings (other entries kept)",
            CustomApply = () => DirectXGlobal.SetAsync("VRROptimizeEnable", "1", "gpu.vrr"),
            CustomRevert = () => DirectXGlobal.SetAsync("VRROptimizeEnable", "0", "gpu.vrr"),
        },
        new()
        {
            Id = "cpu.minstate", Name = "Keep CPU clocks up when plugged in", Category = TweakCategory.Power,
            Description = "Sets the active power plan's minimum processor state to 100% while on AC power.",
            ExpectedEffect = "The CPU doesn't drop to its lowest clock between bursts of work, removing clock ramp-up delay. Raises idle power draw and temperature. Little difference on CPUs with fast hardware P-states (Ryzen CPPC, Intel Speed Shift).",
            Explanation = "Changes \"Processor power management → Minimum processor state (Plugged in)\" in Control Panel → Power Options → Advanced settings, for the currently active plan only. High performance and Ultimate Performance plans already use 100%. Battery behaviour is not changed. Turning this off restores the exact value it had before.",
            Risk = Risk.Moderate, Compatibility = "All Windows 10/11 PCs. Applies to the active power plan; switching plans uses that plan's own value.",
            ChangesText = "Active power plan → Processor power management → Minimum processor state (plugged in) = 100%\n(powrprof: PowerWriteACValueIndex SUB_PROCESSOR/PROCTHROTTLEMIN, then re-applies the plan)",
            CustomState = () => ReadMinProcessorState() is { } v ? v >= 100 : null,
            CustomApply = () => SetMinProcessorState(true),
            CustomRevert = () => SetMinProcessorState(false),
        },
        new()
        {
            Id = "storage.storagesense", Name = "Turn on Storage Sense", Category = TweakCategory.Windows,
            Description = "Lets Windows automatically remove temporary files when disk space runs low.",
            ExpectedEffect = "Keeps free space available without manual cleanup. What gets cleaned (temp files, Recycle Bin and Downloads age limits) follows the rules shown in Settings → System → Storage → Storage Sense.",
            Explanation = "Same switch as Settings → System → Storage → Storage Sense. Review its cleanup rules after enabling it: if you rely on the Downloads folder as storage, make sure Downloads cleanup is set to \"Never\".",
            MinBuild = 17763, Compatibility = "Windows 10 1809+ / Windows 11.",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, 0)],
        },
        new()
        {
            Id = "gaming.mouseaccel", Name = "Disable \"Enhance pointer precision\"", Category = TweakCategory.Gaming,
            Description = "Turns off Windows mouse acceleration so cursor travel depends only on physical distance.",
            ExpectedEffect = "Consistent, predictable aim. This is about consistency, not FPS.",
            Explanation = "Same as Control Panel → Mouse → Pointer Options → Enhance pointer precision. Most games use raw input and are unaffected, but games and menus that use the Windows cursor become 1:1. Applied immediately.",
            Ops =
            [
                RegOp.Str(Hive.HKCU, @"Control Panel\Mouse", "MouseSpeed", "0", "1"),
                RegOp.Str(Hive.HKCU, @"Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                RegOp.Str(Hive.HKCU, @"Control Panel\Mouse", "MouseThreshold2", "0", "10"),
            ],
            AfterChange = on => SpiMouse(on),
        },

        // ───────────────────────────── Performance
        new()
        {
            Id = "perf.powerthrottling", Name = "Disable power throttling", Category = TweakCategory.Performance,
            Description = "Prevents Windows from forcing background processes onto efficiency mode (EcoQoS).",
            ExpectedEffect = "Background renders, compiles and encodes keep full CPU speed when their window isn't focused. Increases power draw — not recommended on battery.",
            Explanation = "Uses the documented PowerThrottlingOff value. Power throttling lowers clock speed for work Windows considers background. Useful for creators running long jobs in the background; desktop gamers see little difference.",
            Risk = Risk.Moderate, RequiresAdmin = true, AppliesAfter = "Restart", MinBuild = 16299,
            Compatibility = "Windows 10 1709+ / Windows 11. Most noticeable on laptops and hybrid CPUs.",
            Ops = [RegOp.DWord(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null)],
        },
        new()
        {
            Id = "perf.backgroundapps", Name = "Block background Store apps", Category = TweakCategory.Performance,
            Description = "Stops Microsoft Store (UWP) apps from running in the background for the current user.",
            ExpectedEffect = "Fewer background processes and wake-ups. Store apps (Mail, Weather, etc.) won't refresh live tiles or notifications until opened.",
            Explanation = "The Windows 10 \"Let apps run in the background\" master switch. Windows 11 manages this per app (Settings → Apps → app → Advanced options); this value is still honoured on many builds. Desktop programs are not affected.",
            Compatibility = "Fully effective on Windows 10; partially on Windows 11 (per-app settings take priority).",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, null)],
            AlsoOnWhen = () => Reg.Read(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground") is 2,
            AlsoOnSource = "a Group Policy (\"Let Windows apps run in the background\" = Force Deny)",
        },
        new()
        {
            Id = "perf.menudelay", Name = "Faster menu display", Category = TweakCategory.Performance,
            Description = "Shortens the hover delay before cascading menus open (400 ms → 100 ms).",
            ExpectedEffect = "Menus feel snappier. Purely a UI responsiveness change.",
            Explanation = "Changes HKCU\\Control Panel\\Desktop\\MenuShowDelay, the delay Windows waits before showing submenus, and applies it live via SystemParametersInfo.",
            Ops = [RegOp.Str(Hive.HKCU, @"Control Panel\Desktop", "MenuShowDelay", "100", "400")],
            AfterChange = on => Win32.SystemParametersInfo(Win32.SPI_SETMENUSHOWDELAY, on ? 100u : 400u, IntPtr.Zero, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE),
        },

        // ───────────────────────────── Windows
        new()
        {
            Id = "win.websearch", Name = "Remove web results from Start search", Category = TweakCategory.Windows,
            Description = "Start menu search shows only local apps, files and settings — no Bing results.",
            ExpectedEffect = "Faster, less cluttered Start search with no queries sent to Bing.",
            Explanation = "Applies the documented user policy DisableSearchBoxSuggestions. Takes effect after Explorer restarts or you sign out.",
            AppliesAfter = "Sign out or Explorer restart",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)],
            // Search settings' own switch for Bing results.
            AlsoOnWhen = () => Reg.Read(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled") is 0,
            AlsoOnSource = "Windows Search settings (BingSearchEnabled = 0)",
            TurnOffElsewhere = () => TweakEngine.SetElsewhere(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1, "win.websearch", "Re-enabled Bing results in Start search"),
        },
        new()
        {
            Id = "win.tips", Name = "Turn off tips and suggestions", Category = TweakCategory.Windows,
            Description = "Stops \"Get tips and suggestions\" notifications and suggested apps in Start.",
            ExpectedEffect = "Fewer promotional notifications and no automatically suggested app installs.",
            Explanation = "Same as Settings → System → Notifications → \"Get tips and suggestions when using Windows\", plus the Start suggestions switch.",
            Ops =
            [
                RegOp.DWord(Hive.HKCU, Cdm, "SubscribedContent-338389Enabled", 0, 1),
                RegOp.DWord(Hive.HKCU, Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                RegOp.DWord(Hive.HKCU, Cdm, "SilentInstalledAppsEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "win.fileext", Name = "Show file extensions", Category = TweakCategory.Windows,
            Description = "Shows .exe, .pdf, .zip and other extensions in File Explorer.",
            ExpectedEffect = "Easier to recognise file types and spot disguised files like invoice.pdf.exe. A safety improvement.",
            Explanation = "Same as File Explorer → View → Show → File name extensions.",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "HideFileExt", 0, 1)],
            AfterChange = _ => Win32.BroadcastSettingChange("ShellState"),
        },
        new()
        {
            Id = "win.classicmenu", Name = "Classic right-click menu", Category = TweakCategory.Windows,
            Description = "Restores the full Windows 10-style context menu without \"Show more options\".",
            ExpectedEffect = "One click less for shell extensions such as 7-Zip, Git or antivirus scans.",
            Explanation = "Registers an empty override for the Windows 11 context menu COM class in your user profile, which makes Explorer fall back to the classic menu. Reverting deletes that override.",
            MinBuild = 22000, Compatibility = "Windows 11 only.", AppliesAfter = "Explorer restart",
            Ops = [RegOp.DefaultStr(Hive.HKCU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "")],
        },

        // ───────────────────────────── Privacy
        new()
        {
            Id = "privacy.adid", Name = "Turn off advertising ID", Category = TweakCategory.Privacy,
            Description = "Stops apps from using your advertising ID for personalised ads.",
            ExpectedEffect = "Apps can no longer link your activity through a shared ad identifier.",
            Explanation = "Same as Settings → Privacy & security → General → \"Let apps show me personalized ads\".",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)],
        },
        new()
        {
            Id = "privacy.tailored", Name = "Turn off tailored experiences", Category = TweakCategory.Privacy,
            Description = "Microsoft won't use diagnostic data to personalise tips and ads.",
            ExpectedEffect = "Fewer personalised recommendations.",
            Explanation = "Same as Settings → Privacy & security → Diagnostics & feedback → Tailored experiences.",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)],
        },
        new()
        {
            Id = "privacy.activity", Name = "Disable activity history", Category = TweakCategory.Privacy,
            Description = "Windows stops collecting and uploading the apps and files you use.",
            ExpectedEffect = "Less local tracking and no activity upload to your Microsoft account.",
            Explanation = "Applies the documented Group Policy values PublishUserActivities and UploadUserActivities (Computer Configuration → System → OS Policies).",
            RequiresAdmin = true,
            Ops =
            [
                RegOp.DWord(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                RegOp.DWord(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null),
            ],
        },
        new()
        {
            Id = "privacy.diagdata", Name = "Send only required diagnostic data", Category = TweakCategory.Privacy,
            Description = "Limits Windows telemetry to the minimum level allowed on your edition.",
            ExpectedEffect = "Less diagnostic data sent to Microsoft. Windows Update and security features keep working.",
            Explanation = "Sets the documented AllowTelemetry policy to 1 (Required). This does NOT disable Windows Update, Defender or any security service. Note: Windows will show \"Some settings are managed by your organization\" while a policy is set.",
            RequiresAdmin = true, Risk = Risk.Moderate,
            Ops = [RegOp.DWord(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1, null)],
            // Settings → Diagnostics & feedback → "Send optional diagnostic data" off stores 0/1 here.
            AlsoOnWhen = () => Reg.Read(Hive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry") is int v && v <= 1,
            AlsoOnSource = "Windows Settings → Privacy & security → Diagnostics & feedback (\"Send optional diagnostic data\" is off)",
        },
        new()
        {
            Id = "privacy.speech", Name = "Turn off online speech recognition", Category = TweakCategory.Privacy,
            Description = "Voice input uses only on-device recognition.",
            ExpectedEffect = "Voice clips are no longer sent to Microsoft's cloud. Dictation accuracy may drop.",
            Explanation = "Same as Settings → Privacy & security → Speech → Online speech recognition.",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0, null)],
        },

        // ───────────────────────────── Network
        new()
        {
            Id = "net.deliveryopt", Name = "Delivery Optimization: local network only", Category = TweakCategory.Network,
            Description = "Windows Update shares downloaded updates only with PCs on your home network, not the internet.",
            ExpectedEffect = "Stops your upload bandwidth being used to send updates to strangers. It does not change your ping.",
            Explanation = "Applies the documented DODownloadMode policy = 1 (LAN). Updates still download normally from Microsoft.",
            RequiresAdmin = true,
            Ops = [RegOp.DWord(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1, null)],
            // Settings → Windows Update → Delivery Optimization stores the choice in the Network Service profile
            // (0 = off, 1 = local network). Only readable with administrator rights.
            AlsoOnWhen = () =>
            {
                using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                using var k = users.OpenSubKey(@"S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings");
                return k?.GetValue("DownloadMode") is int m && m is 0 or 1;
            },
            AlsoOnSource = "Windows Settings → Windows Update → Advanced options → Delivery Optimization",
        },

        // ───────────────────────────── Startup
        new()
        {
            Id = "startup.delay", Name = "Remove startup app delay", Category = TweakCategory.Startup,
            Description = "Lets startup apps launch right after sign-in instead of waiting a few seconds.",
            ExpectedEffect = "Startup programs appear sooner. Trade-off: sign-in can feel busier if you have many startup apps — trim them in Startup first.",
            Explanation = "Sets Explorer's StartupDelayInMSec to 0. By default Explorer delays Run-key programs so the desktop loads first.",
            Ops = [RegOp.DWord(Hive.HKCU, Explorer + @"\Serialize", "StartupDelayInMSec", 0, null)],
        },
        new()
        {
            Id = "startup.faststartup", Name = "Disable Fast Startup", Category = TweakCategory.Startup,
            Description = "Shut down performs a full shutdown instead of a hybrid hibernate.",
            ExpectedEffect = "Fixes problems with drivers, dual-boot and \"uptime never resets\". Cold boot may be a few seconds slower.",
            Explanation = "Same as Control Panel → Power Options → \"Turn on fast startup\". Fast Startup saves the kernel session to disk at shutdown; turning it off makes every shutdown a clean start.",
            RequiresAdmin = true,
            Ops = [RegOp.DWord(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)],
            // Fast Startup needs hibernation; with hibernation disabled it's effectively off.
            AlsoOnWhen = () => Reg.Read(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is 0,
            AlsoOnSource = "hibernation being disabled (Fast Startup can't run without it)",
        },

        // ───────────────────────────── Power
        new()
        {
            Id = "power.hibernate", Name = "Disable hibernation", Category = TweakCategory.Power,
            Description = "Turns off hibernation and deletes hiberfil.sys.",
            ExpectedEffect = "Frees disk space equal to roughly 40% of your RAM. Also disables Fast Startup. Not recommended on laptops that rely on hibernate.",
            Explanation = "Runs the built-in command `powercfg /hibernate off`. Revert runs `powercfg /hibernate on`.",
            RequiresAdmin = true, Risk = Risk.Moderate,
            ChangesText = "powercfg /hibernate off   (revert: powercfg /hibernate on)",
            CustomState = () => Reg.Read(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is int v ? v == 0 : null,
            CustomApply = async () => { var r = await SysCmd.RunAsync(SysCmd.SystemExe("powercfg.exe"), ["/hibernate", "off"]); return (r.Code == 0, r.Code == 0 ? "Hibernation disabled." : r.Output); },
            CustomRevert = async () => { var r = await SysCmd.RunAsync(SysCmd.SystemExe("powercfg.exe"), ["/hibernate", "on"]); return (r.Code == 0, r.Code == 0 ? "Hibernation enabled." : r.Output); },
        },

        // ───────────────────────────── Visual effects
        new()
        {
            Id = "vfx.transparency", Name = "Turn off transparency effects", Category = TweakCategory.VisualEffects,
            Description = "Disables acrylic/mica blur on the taskbar, Start and windows.",
            ExpectedEffect = "Slightly less GPU composition work. Most noticeable on integrated graphics.",
            Explanation = "Same as Settings → Personalization → Colors → Transparency effects. Applied immediately.",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)],
            AfterChange = _ => Win32.BroadcastSettingChange("ImmersiveColorSet"),
        },
        new()
        {
            Id = "vfx.minanimate", Name = "Turn off window minimise/maximise animation", Category = TweakCategory.VisualEffects,
            Description = "Windows snap open and closed instantly.",
            ExpectedEffect = "Window actions feel instant. Purely cosmetic responsiveness.",
            Explanation = "Same as System Properties → Performance Options → \"Animate windows when minimizing and maximizing\". Applied live through SystemParametersInfo.",
            Ops = [RegOp.Str(Hive.HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1")],
            AfterChange = on =>
            {
                var ai = new Win32.ANIMATIONINFO { cbSize = 8, iMinAnimate = on ? 0 : 1 };
                Win32.SystemParametersInfo(Win32.SPI_SETANIMATION, 8, ref ai, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
            },
        },
        new()
        {
            Id = "vfx.taskbaranim", Name = "Turn off taskbar animations", Category = TweakCategory.VisualEffects,
            Description = "Removes taskbar button animations.",
            ExpectedEffect = "Slightly snappier taskbar. Cosmetic.",
            Explanation = "Same as Performance Options → \"Animations in the taskbar\".",
            AppliesAfter = "Explorer restart",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "TaskbarAnimations", 0, 1)],
        },
        new()
        {
            Id = "vfx.dragfull", Name = "Don't show window contents while dragging", Category = TweakCategory.VisualEffects,
            Description = "Moving a window drags an outline instead of redrawing the whole window.",
            ExpectedEffect = "Less redrawing while dragging windows. Mostly helps older or integrated GPUs; cosmetic on gaming PCs.",
            Explanation = "Same as Performance Options → \"Show window contents while dragging\". Applied live through SystemParametersInfo.",
            Ops = [RegOp.Str(Hive.HKCU, @"Control Panel\Desktop", "DragFullWindows", "0", "1")],
            AfterChange = on => Win32.SystemParametersInfo(Win32.SPI_SETDRAGFULLWINDOWS, on ? 0u : 1u, IntPtr.Zero, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE),
        },
        new()
        {
            Id = "vfx.iconshadow", Name = "Turn off desktop icon label shadows", Category = TweakCategory.VisualEffects,
            Description = "Removes the drop shadow behind desktop icon names.",
            ExpectedEffect = "Crisper icon labels and a tiny bit less drawing. Cosmetic.",
            Explanation = "Same as Performance Options → \"Use drop shadows for icon labels on the desktop\".",
            AppliesAfter = "Explorer restart",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "ListviewShadow", 0, 1)],
        },
        new()
        {
            Id = "vfx.selectionalpha", Name = "Turn off translucent selection rectangle", Category = TweakCategory.VisualEffects,
            Description = "Drag-selecting files shows a plain outline instead of a see-through box.",
            ExpectedEffect = "Slightly lighter drawing when selecting many files. Cosmetic.",
            Explanation = "Same as Performance Options → \"Show translucent selection rectangle\".",
            AppliesAfter = "Explorer restart",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "ListviewAlphaSelect", 0, 1)],
        },
        new()
        {
            Id = "vfx.peek", Name = "Turn off Peek", Category = TweakCategory.VisualEffects,
            Description = "Hovering the far corner of the taskbar no longer fades all windows to show the desktop.",
            ExpectedEffect = "No accidental desktop previews when your mouse touches the corner mid-game. Clicking the corner still shows the desktop.",
            Explanation = "Same as Performance Options → \"Enable Peek\" (Desktop Window Manager's EnableAeroPeek).",
            AppliesAfter = "Sign out",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0, 1)],
        },
        new()
        {
            Id = "vfx.iconsonly", Name = "Show icons instead of thumbnails", Category = TweakCategory.VisualEffects,
            Description = "File Explorer shows file-type icons instead of generating picture and video previews.",
            ExpectedEffect = "Folders full of photos or videos open faster, especially on hard drives and network shares. You lose the previews.",
            Explanation = "Same as File Explorer → Options → View → \"Always show icons, never thumbnails\".",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "IconsOnly", 1, 0)],
            AfterChange = _ => Win32.BroadcastSettingChange("ShellState"),
        },

        // ───────────────────────────── Input
        new()
        {
            Id = "input.stickykeys", Name = "Turn off the Sticky Keys shortcut", Category = TweakCategory.Gaming,
            Description = "Pressing Shift five times no longer pops up the Sticky Keys prompt.",
            ExpectedEffect = "No more pop-up stealing focus from your game when you tap Shift repeatedly. Sticky Keys itself still works if you turn it on in Settings.",
            Explanation = "Same as Settings → Accessibility → Keyboard → Sticky keys → \"Keyboard shortcut for Sticky keys\". Clears the shortcut flag only (Flags 510 → 506).",
            AppliesAfter = "Sign out",
            Ops = [RegOp.Str(Hive.HKCU, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510")],
        },
        new()
        {
            Id = "input.filterkeys", Name = "Turn off the Filter Keys shortcut", Category = TweakCategory.Gaming,
            Description = "Holding right Shift for eight seconds no longer pops up the Filter Keys prompt.",
            ExpectedEffect = "No pop-up when you hold Shift to sprint or crouch. Filter Keys itself still works if you turn it on in Settings.",
            Explanation = "Same as Settings → Accessibility → Keyboard → Filter keys → \"Keyboard shortcut for Filter keys\". Clears the shortcut flag only (Flags 126 → 122).",
            AppliesAfter = "Sign out",
            Ops = [RegOp.Str(Hive.HKCU, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126")],
        },
        new()
        {
            Id = "input.togglekeys", Name = "Turn off the Toggle Keys shortcut", Category = TweakCategory.Gaming,
            Description = "Holding Num Lock for five seconds no longer turns on Toggle Keys.",
            ExpectedEffect = "No surprise prompt or beeps from holding Num Lock. Toggle Keys itself still works if you turn it on in Settings.",
            Explanation = "Same as Settings → Accessibility → Keyboard → Toggle keys → \"Keyboard shortcut for Toggle keys\". Clears the shortcut flag only (Flags 62 → 58).",
            AppliesAfter = "Sign out",
            Ops = [RegOp.Str(Hive.HKCU, @"Control Panel\Accessibility\ToggleKeys", "Flags", "58", "62")],
        },
        new()
        {
            Id = "input.keydelay", Name = "Shorter key repeat delay", Category = TweakCategory.Windows,
            Description = "A held key starts repeating sooner (about 250 ms instead of 500 ms).",
            ExpectedEffect = "Faster held-key repeat in text and menus. Games that read keys directly are unaffected.",
            Explanation = "Same as Control Panel → Keyboard → Repeat delay, moved to the shortest setting. Applied live through SystemParametersInfo.",
            Ops = [RegOp.Str(Hive.HKCU, @"Control Panel\Keyboard", "KeyboardDelay", "0", "1")],
            AfterChange = on => Win32.SystemParametersInfo(Win32.SPI_SETKEYBOARDDELAY, on ? 0u : 1u, IntPtr.Zero, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE),
        },
        new()
        {
            Id = "input.usbsuspend", Name = "Keep USB devices awake when plugged in", Category = TweakCategory.Power,
            Description = "Turns off USB selective suspend in the active power plan while on AC power.",
            ExpectedEffect = "Can fix mice, headsets, controllers and USB audio cutting out or lagging after a moment idle. Slightly higher idle power use.",
            Explanation = "Changes \"USB settings → USB selective suspend setting (Plugged in)\" in Control Panel → Power Options → Advanced settings, for the active plan only. Battery behaviour is unchanged. Turning this off restores the exact previous value.",
            Compatibility = "All Windows 10/11 PCs with USB devices. Applies to the active power plan.",
            ChangesText = "Active power plan → USB settings → USB selective suspend (plugged in) = Disabled",
            CustomState = () => ReadAc(SubUsb, UsbSelectiveSuspend) is { } v ? v == 0 : null,
            CustomApply = () => SetAcPower("input.usbsuspend", SubUsb, UsbSelectiveSuspend, 0, true, 1, "USB selective suspend (plugged in) disabled"),
            CustomRevert = () => SetAcPower("input.usbsuspend", SubUsb, UsbSelectiveSuspend, 0, false, 1, "USB selective suspend (plugged in) restored"),
        },
        new()
        {
            Id = "input.gamebarbutton", Name = "Controller Xbox button doesn't open Game Bar", Category = TweakCategory.Gaming,
            Description = "Pressing the Xbox button on a controller no longer opens Xbox Game Bar.",
            ExpectedEffect = "No overlay popping up when you press the guide button in games or Steam Big Picture. Win+G still opens Game Bar.",
            Explanation = "Same as Settings → Gaming → Xbox Game Bar → \"Open Xbox Game Bar using this button on a controller\".",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, 1)],
        },

        // ───────────────────────────── GPU, storage & power links
        new()
        {
            Id = "gpu.pcieaspm", Name = "Turn off PCI Express link power saving when plugged in", Category = TweakCategory.Power,
            Description = "Keeps PCIe links (graphics card, NVMe drives) at full power on AC instead of dropping into low-power states.",
            ExpectedEffect = "Can remove small wake-up delays and rare stutters on some systems. Raises idle power use a little; most modern PCs won't notice a difference.",
            Explanation = "Changes \"PCI Express → Link State Power Management (Plugged in)\" to Off in Control Panel → Power Options → Advanced settings, for the active plan only. Battery behaviour is unchanged. Turning this off restores the exact previous value.",
            Risk = Risk.Moderate, Compatibility = "Windows 10/11 PCs with PCIe devices. Applies to the active power plan.",
            ChangesText = "Active power plan → PCI Express → Link State Power Management (plugged in) = Off",
            CustomState = () => ReadAc(SubPciExpress, PcieAspm) is { } v ? v == 0 : null,
            CustomApply = () => SetAcPower("gpu.pcieaspm", SubPciExpress, PcieAspm, 0, true, 1, "PCIe link power management (plugged in) off"),
            CustomRevert = () => SetAcPower("gpu.pcieaspm", SubPciExpress, PcieAspm, 0, false, 1, "PCIe link power management (plugged in) restored"),
        },
        new()
        {
            Id = "storage.diskidle", Name = "Never turn off hard disks when plugged in", Category = TweakCategory.Power,
            Description = "Stops Windows from spinning down hard drives after 20 minutes idle while on AC power.",
            ExpectedEffect = "No several-second pause while a sleeping hard drive spins back up. No effect on SSDs.",
            Explanation = "Changes \"Hard disk → Turn off hard disk after (Plugged in)\" to Never in Control Panel → Power Options → Advanced settings, for the active plan only. Battery behaviour is unchanged. Turning this off restores the exact previous value.",
            Compatibility = "Useful on PCs with spinning hard drives. Applies to the active power plan.",
            ChangesText = "Active power plan → Hard disk → Turn off hard disk after (plugged in) = Never",
            CustomState = () => ReadAc(SubDisk, DiskIdle) is { } v ? v == 0 : null,
            CustomApply = () => SetAcPower("storage.diskidle", SubDisk, DiskIdle, 0, true, 1200, "Hard disk turn-off (plugged in) set to never"),
            CustomRevert = () => SetAcPower("storage.diskidle", SubDisk, DiskIdle, 0, false, 1200, "Hard disk turn-off (plugged in) restored"),
        },
        new()
        {
            Id = "storage.longpaths", Name = "Allow long file paths", Category = TweakCategory.Windows,
            Description = "Lets programs that support it use file paths longer than 260 characters.",
            ExpectedEffect = "Fixes \"path too long\" errors with deep game mod folders, Node.js, Git and similar tools.",
            Explanation = "Sets the documented LongPathsEnabled value (the same as the Group Policy \"Enable Win32 long paths\"). Programs still have to opt in; nothing changes for those that don't.",
            RequiresAdmin = true, MinBuild = 14393, Compatibility = "Windows 10 1607+ / Windows 11.",
            Ops = [RegOp.DWord(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)],
        },

        // ───────────────────────────── Windows & Explorer
        new()
        {
            Id = "win.hiddenfiles", Name = "Show hidden files and folders", Category = TweakCategory.Windows,
            Description = "File Explorer shows files and folders marked as hidden (such as AppData).",
            ExpectedEffect = "Easier to find game saves, configs and mod folders. Protected operating system files stay hidden.",
            Explanation = "Same as File Explorer → View → Show → Hidden items.",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "Hidden", 1, 2)],
            AfterChange = _ => Win32.BroadcastSettingChange("ShellState"),
        },
        new()
        {
            Id = "win.thispc", Name = "Open File Explorer to This PC", Category = TweakCategory.Windows,
            Description = "File Explorer opens on your drives instead of Home / Quick access.",
            ExpectedEffect = "Drives are one click away, and Explorer doesn't have to load recent files and cloud items on open.",
            Explanation = "Same as File Explorer → Options → General → \"Open File Explorer to: This PC\".",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "LaunchTo", 1, 2)],
        },
        new()
        {
            Id = "win.lockscreentips", Name = "Turn off lock screen tips and fun facts", Category = TweakCategory.Windows,
            Description = "No tips, ads or \"fun facts\" over your lock screen picture.",
            ExpectedEffect = "A clean lock screen. Your chosen picture or slideshow stays.",
            Explanation = "Same as Settings → Personalization → Lock screen → \"Get fun facts, tips, tricks, and more on your lock screen\".",
            Ops =
            [
                RegOp.DWord(Hive.HKCU, Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
                RegOp.DWord(Hive.HKCU, Cdm, "SubscribedContent-338387Enabled", 0, 1),
            ],
        },
        new()
        {
            Id = "win.startrecs", Name = "Turn off Start menu recommendations", Category = TweakCategory.Windows,
            Description = "Start stops recommending tips, shortcuts and new apps.",
            ExpectedEffect = "A less cluttered Start menu with fewer promotions. Your recent files setting is separate.",
            Explanation = "Same as Settings → Personalization → Start → \"Show recommendations for tips, shortcuts, new apps, and more\".",
            MinBuild = 22621, Compatibility = "Windows 11 22H2 and newer.",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "Start_IrisRecommendations", 0, 1)],
        },
        new()
        {
            Id = "win.endtask", Name = "Add \"End task\" to taskbar right-click", Category = TweakCategory.Windows,
            Description = "Right-clicking an app on the taskbar offers End task.",
            ExpectedEffect = "Close a frozen game or app in two clicks, without opening Task Manager.",
            Explanation = "Same as Settings → System → For developers → End Task.",
            MinBuild = 22631, Compatibility = "Windows 11 23H2 and newer.",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0)],
        },
        new()
        {
            Id = "win.taskview", Name = "Hide the Task View button", Category = TweakCategory.Windows,
            Description = "Removes the Task View button from the taskbar.",
            ExpectedEffect = "A tidier taskbar. Win+Tab still opens Task View.",
            Explanation = "Same as Settings → Personalization → Taskbar → Task view.",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "ShowTaskViewButton", 0, 1)],
        },
        new()
        {
            Id = "win.autoplay", Name = "Turn off AutoPlay", Category = TweakCategory.Windows,
            Description = "Plugging in a USB drive, phone or disc no longer pops up an AutoPlay prompt.",
            ExpectedEffect = "No pop-ups interrupting you, and media can't start an action automatically. Open drives yourself from File Explorer.",
            Explanation = "Same as Settings → Bluetooth & devices → AutoPlay → \"Use AutoPlay for all media and devices\".",
            Ops = [RegOp.DWord(Hive.HKCU, Explorer + @"\AutoplayHandlers", "DisableAutoplay", 1, 0)],
        },
        new()
        {
            Id = "startup.restartapps", Name = "Don't reopen apps after restarting", Category = TweakCategory.Startup,
            Description = "Windows won't automatically restart the apps you had open when you sign back in.",
            ExpectedEffect = "A cleaner, quicker start after updates and restarts; open only what you need.",
            Explanation = "Same as Settings → Accounts → Sign-in options → \"Automatically save my restartable apps and restart them when I sign back in\".",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "RestartApps", 0, 0) with { OffValue = 1 }],
        },

        // ───────────────────────────── More privacy
        new()
        {
            Id = "privacy.settingsads", Name = "Turn off suggested content in Settings", Category = TweakCategory.Privacy,
            Description = "The Settings app stops showing suggested content and offers.",
            ExpectedEffect = "Fewer promotions inside Settings.",
            Explanation = "Same as Settings → Privacy & security → General → \"Show me suggested content in the Settings app\".",
            Ops =
            [
                RegOp.DWord(Hive.HKCU, Cdm, "SubscribedContent-338393Enabled", 0, 1),
                RegOp.DWord(Hive.HKCU, Cdm, "SubscribedContent-353694Enabled", 0, 1),
                RegOp.DWord(Hive.HKCU, Cdm, "SubscribedContent-353696Enabled", 0, 1),
            ],
        },
        new()
        {
            Id = "privacy.langlist", Name = "Don't share your language list with websites", Category = TweakCategory.Privacy,
            Description = "Websites can no longer read your Windows language list to show locally relevant content.",
            ExpectedEffect = "One less detail websites can use to identify you. Sites use your browser's language setting instead.",
            Explanation = "Same as Settings → Privacy & security → General → \"Let websites show me locally relevant content by accessing my language list\".",
            Ops = [RegOp.DWord(Hive.HKCU, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, 0)],
        },
        new()
        {
            Id = "privacy.apptracking", Name = "Don't track app launches", Category = TweakCategory.Privacy,
            Description = "Windows stops recording which apps you open to personalise Start and search.",
            ExpectedEffect = "Less local tracking. Start no longer shows a \"most used\" list.",
            Explanation = "Same as Settings → Privacy & security → General → \"Let Windows improve Start and search results by tracking app launches\".",
            Ops = [RegOp.DWord(Hive.HKCU, ExplorerAdv, "Start_TrackProgs", 0, 1)],
        },
        new()
        {
            Id = "privacy.inking", Name = "Turn off inking and typing personalization", Category = TweakCategory.Privacy,
            Description = "Windows stops building a personal dictionary from your typing and handwriting.",
            ExpectedEffect = "Your typed words and handwriting aren't collected for suggestions. Typing suggestions may be less personal.",
            Explanation = "Same as Settings → Privacy & security → Inking & typing personalization → \"Custom inking and typing dictionary\".",
            Ops =
            [
                RegOp.DWord(Hive.HKCU, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0),
                RegOp.DWord(Hive.HKCU, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),
                RegOp.DWord(Hive.HKCU, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, 1),
                RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, 1),
            ],
        },
        new()
        {
            Id = "privacy.feedback", Name = "Never ask for feedback", Category = TweakCategory.Privacy,
            Description = "Windows stops popping up requests to rate or give feedback.",
            ExpectedEffect = "No feedback prompts. You can still send feedback yourself with the Feedback Hub.",
            Explanation = "Same as Settings → Privacy & security → Diagnostics & feedback → Feedback frequency → Never.",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null)],
        },
        new()
        {
            Id = "privacy.cloudsearch", Name = "Don't include cloud content in search", Category = TweakCategory.Privacy,
            Description = "Windows search stops looking in your Microsoft and work/school account content.",
            ExpectedEffect = "Search shows only what's on this PC, and doesn't query your OneDrive, Outlook or Bing account history.",
            Explanation = "Same as Settings → Privacy & security → Search permissions → Cloud content search (both Microsoft account and work or school account).",
            Ops =
            [
                RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsMSACloudSearchEnabled", 0, 1),
                RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsAADCloudSearchEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "privacy.searchhistory", Name = "Turn off search history on this device", Category = TweakCategory.Privacy,
            Description = "Windows search stops remembering what you searched for on this PC.",
            ExpectedEffect = "No saved search history. Search results lose past-search shortcuts.",
            Explanation = "Same as Settings → Privacy & security → Search permissions → History → \"Search history on this device\".",
            Ops = [RegOp.DWord(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDeviceSearchHistoryEnabled", 0, 1)],
        },
    ];
}

/// <summary>Edits the semicolon-delimited DirectXUserGlobalSettings string without clobbering other keys.</summary>
internal static class DirectXGlobal
{
    private const string Path = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string Name = "DirectXUserGlobalSettings";

    public static Task<(bool, string)> SetAsync(string key, string value, string tweakId) => Task.Run<(bool, string)>(() =>
    {
        var snap = Reg.Capture(Hive.HKCU, Path, Name);
        var current = (Reg.Read(Hive.HKCU, Path, Name) as string) ?? "";
        var pairs = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .Where(p => !p[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p[0]}={p[1]}").ToList();
        pairs.Add($"{key}={value}");
        Reg.Write(Hive.HKCU, Path, Name, string.Join(";", pairs) + ";", RegistryValueKind.String);
        if (value == "1")
            Journal.Add(new ChangeRecord { Kind = ChangeKind.Tweak, Target = tweakId + ":raw", Title = $"DirectX setting {key}={value}", Detail = "Previous DirectX global settings recorded.", Snapshots = [snap] });
        return (true, "Applied.");
    });
}
