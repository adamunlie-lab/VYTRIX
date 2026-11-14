using System.IO;
using Microsoft.Win32;
using Vytrix.Core;
using Vytrix.Native;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>
/// Tests that need administrator rights. They make real changes and verify the
/// rollback restores the exact previous state. In standard mode they return early.
/// Run with: tools\admin-qa.ps1 (elevated).
/// </summary>
[Trait("Category", "Admin")]
[Collection("Admin")]   // run sequentially: they share machine state
public class AdminTests
{
    private const string HklmScratch = @"SOFTWARE\VYTRIX-Tests";

    [Fact]
    public async Task Hklm_tweak_applies_backs_up_and_restores()
    {
        if (!Elevation.IsAdmin) return;
        var path = $@"{HklmScratch}\{Guid.NewGuid():N}";
        Reg.Write(Hive.HKLM, path, "Existing", 9, RegistryValueKind.DWord);
        try
        {
            var t = new Tweak
            {
                Id = "test.hklm." + Guid.NewGuid().ToString("N"), Name = "HKLM synthetic", Category = TweakCategory.Windows,
                Description = "d", ExpectedEffect = "e", Explanation = "x", RequiresAdmin = true,
                Ops = [RegOp.DWord(Hive.HKLM, path, "Existing", 1, null), RegOp.DWord(Hive.HKLM, path + @"\New", "V", 1, null)],
            };
            var (ok, msg) = await TweakEngine.ApplyAsync(t);
            Assert.True(ok, msg);
            var rec = Journal.ActiveFor(ChangeKind.Tweak, t.Id)!;
            Assert.Single(rec.BackupFiles);                          // existing key exported; new key not (nothing to back up)
            Assert.Contains("Existing", File.ReadAllText(rec.BackupFiles[0]));
            Assert.True(new FileInfo(rec.BackupFiles[0]).Length < 64 * 1024, "backup must contain only the target key");
            (ok, msg) = await TweakEngine.RevertAsync(t);
            Assert.True(ok, msg);
            Assert.Equal(9, Reg.Read(Hive.HKLM, path, "Existing"));
            Assert.False(Reg.KeyExists(Hive.HKLM, path + @"\New"));
        }
        finally
        {
            using var b = Reg.Base(Hive.HKLM);
            b.DeleteSubKeyTree(HklmScratch, false);
        }
    }

    [Fact]
    public async Task Real_policy_tweak_round_trip_restores_exact_values()
    {
        if (!Elevation.IsAdmin) return;
        var t = TweakCatalog.ById("privacy.activity")!;
        var before = t.Ops.Select(o => (o, v: Reg.Read(o.Hive, o.Path, o.Name), key: Reg.KeyExists(o.Hive, o.Path))).ToList();
        var stateBefore = t.GetState();

        var (ok, msg) = await TweakEngine.ApplyAsync(t);
        Assert.True(ok, msg);
        Assert.Equal(TweakState.Applied, t.GetState());

        (ok, msg) = await TweakEngine.RevertAsync(t);
        Assert.True(ok, msg);
        foreach (var (o, v, key) in before)
        {
            Assert.Equal(v, Reg.Read(o.Hive, o.Path, o.Name));
            Assert.Equal(key, Reg.KeyExists(o.Hive, o.Path));
        }
        Assert.Equal(stateBefore, t.GetState());
    }

    [Fact]
    public async Task Cpu_min_state_tweak_round_trip_restores_previous_value()
    {
        if (!Elevation.IsAdmin) return;
        const string sub = "54533251-82be-4824-96c1-47b60b740d00", set = "893dee8e-2bef-41e0-89c6-b55d0929964c";
        Assert.Equal(1, VxNative.vx_power_read_ac(sub, set, out var before));
        var t = TweakCatalog.ById("cpu.minstate")!;
        try
        {
            var (ok, msg) = await TweakEngine.ApplyAsync(t);
            Assert.True(ok, msg);
            VxNative.vx_power_read_ac(sub, set, out var mid);
            Assert.Equal(100u, mid);
            (ok, msg) = await TweakEngine.RevertAsync(t);
            Assert.True(ok, msg);
            VxNative.vx_power_read_ac(sub, set, out var after);
            Assert.Equal(before, after);
        }
        finally { VxNative.vx_power_write_ac(sub, set, before); }   // guarantee the user's plan is untouched
    }

    [Fact]
    public async Task Optional_service_start_type_change_rolls_back_exactly()
    {
        if (!Elevation.IsAdmin) return;
        var svc = WinServices.List().FirstOrDefault(s => s.Safety == SvcSafety.Optional && s.Name is "RetailDemo" or "Fax" or "MapsBroker" or "WMPNetworkSvc" or "wisvc");
        if (svc is null) return;
        var key = $@"SYSTEM\CurrentControlSet\Services\{svc.Name}";
        var startBefore = Reg.Read(Hive.HKLM, key, "Start");
        var delayedBefore = Reg.Read(Hive.HKLM, key, "DelayedAutostart");
        var target = svc.StartType == "Disabled" ? "Manual" : "Disabled";

        var (ok, msg) = await WinServices.SetStartTypeAsync(svc, target);
        Assert.True(ok, msg);
        Assert.Equal(target, WinServices.StartTypeOf(svc.Name));

        var rec = Journal.All.First(r => r.Kind == ChangeKind.Service && r.Target == svc.Name && !r.RolledBack);
        (ok, msg) = await TweakEngine.RollbackRecordAsync(rec);
        Assert.True(ok, msg);
        Assert.Equal(startBefore, Reg.Read(Hive.HKLM, key, "Start"));
        Assert.Equal(delayedBefore, Reg.Read(Hive.HKLM, key, "DelayedAutostart"));
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("wuauserv")]
    public async Task Protected_services_are_refused_even_as_admin(string name)
    {
        if (!Elevation.IsAdmin) return;
        var svc = WinServices.List().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (svc is null) return;
        var startBefore = WinServices.StartTypeOf(svc.Name);
        Assert.False((await WinServices.SetStartTypeAsync(svc, "Disabled")).Item1);
        Assert.False((await WinServices.StartStopAsync(svc, start: false)).Item1);
        Assert.Equal(startBefore, WinServices.StartTypeOf(svc.Name));
    }

    [Fact]
    public async Task Dns_change_rolls_back_to_previous_servers()
    {
        if (!Elevation.IsAdmin) return;
        var a = NetworkService.Adapters().FirstOrDefault(x => x.Gateways.Count > 0 && x.IPv4.Count > 0);
        if (a is null) return;
        var beforeStatic = a.DnsIsStatic;
        var beforeV4 = a.Dns.Where(d => !d.Contains(':')).ToList();

        var (ok, msg) = await NetworkService.SetDnsAsync(a, NetworkService.Presets.First(p => p.Name == "Cloudflare"));
        Assert.True(ok, msg);
        await Task.Delay(1500);
        var mid = NetworkService.Adapters().First(x => x.Id == a.Id);
        Assert.Contains("1.1.1.1", mid.Dns);
        Assert.True(mid.DnsIsStatic);

        var rec = Journal.All.First(r => r.Kind == ChangeKind.Dns && r.Target == a.Id && !r.RolledBack);
        (ok, msg) = await TweakEngine.RollbackRecordAsync(rec);
        Assert.True(ok, msg);
        await Task.Delay(1500);
        var after = NetworkService.Adapters().First(x => x.Id == a.Id);
        Assert.Equal(beforeStatic, after.DnsIsStatic);
        if (beforeStatic) Assert.Equal(beforeV4, after.Dns.Where(d => !d.Contains(':')).ToList());
        else Assert.DoesNotContain("1.1.1.1", after.Dns);
    }

    [Fact]
    public async Task Power_plan_switch_rolls_back()
    {
        if (!Elevation.IsAdmin) return;
        var before = PowerService.Active!;
        var other = PowerService.List().FirstOrDefault(p => !p.IsActive);
        if (other is null) return;
        var (ok, msg) = PowerService.SetActive(other, "admin QA");
        Assert.True(ok, msg);
        Assert.Equal(other.Guid, PowerService.Active!.Guid);
        var rec = Journal.All.First(r => r.Kind == ChangeKind.PowerPlan && !r.RolledBack);
        (ok, msg) = await TweakEngine.RollbackRecordAsync(rec);
        Assert.True(ok, msg);
        Assert.Equal(before.Guid, PowerService.Active!.Guid);
    }

    [Fact]
    public async Task Restore_point_is_created_or_skip_is_reported_honestly()
    {
        if (!Elevation.IsAdmin) return;
        var before = RestorePoints.List();
        var (ok, msg) = await RestorePoints.CreateAsync("VYTRIX admin QA");
        var after = RestorePoints.List();
        if (ok) Assert.True(after.Count > before.Count || after.First().Sequence != before.FirstOrDefault()?.Sequence, "reported success but no new point exists");
        else Assert.True(msg.Contains("24 hours") || msg.Contains("System Protection"), $"unexpected failure: {msg}");
    }

    [Fact]
    public async Task Admin_cleanup_locations_scan_without_requiring_elevation_note()
    {
        if (!Elevation.IsAdmin) return;
        var cats = StorageService.Categories().Where(c => c.RequiresAdmin).ToList();
        await StorageService.ScanAsync(cats);   // scan only, never clean
        Assert.All(cats, c => Assert.Null(c.Note));
    }

    [Fact]
    public void Profiles_have_no_admin_skips_when_elevated()
    {
        if (!Elevation.IsAdmin) return;
        foreach (var p in Profiles.All)
            Assert.DoesNotContain(Profiles.Preview(p), i => i.Action.Contains("administrator"));
    }

    [Fact]
    public async Task Winsat_memory_benchmark_reports_real_throughput()
    {
        if (!Elevation.IsAdmin) return;
        var (ok, lines, msg) = await Benchmark.RunAsync("mem");
        Assert.True(ok, msg);
        Assert.Contains(lines, l => l.Value.Contains("MB/s"));
    }
}
