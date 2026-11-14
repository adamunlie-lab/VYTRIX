using System.IO;
using Vytrix.Core;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>Static checks on the tweak library and safety policies.</summary>
public class CatalogTests
{
    [Fact]
    public void Tweak_ids_are_unique() =>
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());

    [Fact]
    public void Every_tweak_is_fully_documented()
    {
        foreach (var t in TweakCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name), t.Id);
            Assert.True(t.Description.Length > 15, $"{t.Id} description");
            Assert.True(t.ExpectedEffect.Length > 15, $"{t.Id} expected effect");
            Assert.True(t.Explanation.Length > 40, $"{t.Id} explanation");
            Assert.False(string.IsNullOrWhiteSpace(t.Compatibility), $"{t.Id} compatibility");
            Assert.True(t.Ops.Count > 0 || (t.CustomApply is not null && t.CustomRevert is not null && t.CustomState is not null), $"{t.Id} must be reversible");
        }
    }

    [Fact]
    public void Every_tweak_is_assigned_to_a_component()
    {
        foreach (var t in TweakCatalog.All) Assert.True(TweakCatalog.HasComponent(t.Id), $"{t.Id} has no component");
    }

    [Fact]
    public void Components_have_tweaks_except_memory_by_design()
    {
        foreach (var c in Components.All)
        {
            var n = TweakCatalog.All.Count(t => t.Component == c.Id);
            if (c.Id == TweakComponent.Memory) Assert.Equal(0, n);   // no placebo "RAM optimizers"
            else Assert.True(n > 0, $"{c.Label} has no tweaks");
        }
        Assert.Contains(TweakCatalog.All, t => t.Component == TweakComponent.Cpu);
        Assert.Contains(TweakCatalog.All, t => t.Component == TweakComponent.Gpu);
    }

    [Fact]
    public void Custom_tweaks_describe_their_exact_change()
    {
        foreach (var t in TweakCatalog.All.Where(t => t.Ops.Count == 0))
            Assert.False(string.IsNullOrWhiteSpace(t.ChangesText), $"{t.Id} must state exactly what it changes");
    }

    [Fact]
    public void Terms_of_agreement_are_embedded_and_complete()
    {
        Assert.Contains("VYTRIX TERMS OF AGREEMENT", Terms.Text);
        foreach (var section in new[] { "LICENSE", "PRIVACY", "NO WARRANTY", "LIMITATION OF LIABILITY", "BACKUPS, ROLLBACK" })
            Assert.Contains(section, Terms.Text);
        Assert.Contains($"Version {Terms.Version}", Terms.Text);
    }

    [Fact]
    public void Every_category_is_covered()
    {
        foreach (var c in Enum.GetValues<TweakCategory>())
            Assert.Contains(TweakCatalog.All, t => t.Category == c);
    }

    [Fact]
    public void Machine_wide_tweaks_require_admin()
    {
        foreach (var t in TweakCatalog.All.Where(t => t.Ops.Any(o => o.Hive == Hive.HKLM)))
            Assert.True(t.RequiresAdmin, $"{t.Id} writes HKLM but is not marked RequiresAdmin");
    }

    [Fact]
    public void No_tweak_touches_security_or_update_components()
    {
        string[] forbidden = ["Windows Defender", "WindowsUpdate", "SharedAccess", "MpsSvc", "WinDefend", "wuauserv", "SecurityHealth", "Firewall", "UAC", "EnableLUA", "SmartScreen"];
        foreach (var t in TweakCatalog.All)
            foreach (var op in t.Ops)
                Assert.DoesNotContain(forbidden, f => op.Path.Contains(f, StringComparison.OrdinalIgnoreCase) || (op.Name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_placebo_network_latency_tweaks()
    {
        string[] placebo = ["TcpAckFrequency", "TCPNoDelay", "NetworkThrottlingIndex", "SystemResponsiveness", "LargeSystemCache", "IRPStackSize"];
        Assert.DoesNotContain(TweakCatalog.All.SelectMany(t => t.Ops), o => placebo.Contains(o.Name ?? "", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void States_can_be_read_without_throwing()
    {
        foreach (var t in TweakCatalog.All) _ = t.GetState();
    }

    [Fact]
    public void Profiles_reference_existing_tweaks_and_explain_themselves()
    {
        Assert.Equal(["balanced", "gaming", "competitive", "creator"], Profiles.All.Select(p => p.Id));
        foreach (var p in Profiles.All)
        {
            Assert.All(p.TweakIds, id => Assert.NotNull(TweakCatalog.ById(id)));
            Assert.True(p.Description.Length > 40);
        }
        Assert.NotNull(Profiles.ById("custom"));
    }

    [Fact]
    public void Profile_preview_lists_an_action_for_every_tweak()
    {
        foreach (var p in Profiles.All)
            Assert.Equal(p.TweakIds.Count, Profiles.Preview(p).Count);
    }
}

public class PolicyTests
{
    [Theory]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("wuauserv")]
    [InlineData("BFE")]
    [InlineData("RpcSs")]
    public async Task Protected_services_cannot_be_changed(string name)
    {
        var svc = WinServices.List().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (svc is null) return;   // not installed on this machine
        Assert.Equal(SvcSafety.Protected, svc.Safety);
        var (ok, _) = await WinServices.SetStartTypeAsync(svc, "Disabled");
        Assert.False(ok);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("MsMpEng.exe")]
    [InlineData("csrss.exe")]
    [InlineData("svchost.exe")]
    public void Core_processes_are_protected(string name) => Assert.True(ProcessService.IsProtected(name));

    [Fact]
    public void Cleanup_never_targets_personal_folders()
    {
        string[] personal =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ];
        foreach (var c in StorageService.Categories())
            foreach (var root in c.Roots)
                Assert.DoesNotContain(personal, p => root.TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || p.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cleanup_skips_recent_files_and_deletes_only_inside_roots()
    {
        var dir = Path.Combine(TestEnvironment.DataDir, "cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "old.tmp"); File.WriteAllText(old, "x"); File.SetLastWriteTime(old, DateTime.Now.AddDays(-3));
        var fresh = Path.Combine(dir, "fresh.tmp"); File.WriteAllText(fresh, "y");
        var cat = new CleanupCategory { Id = "t", Name = "t", Description = "t", Roots = [dir], Selected = true };
        await StorageService.ScanAsync([cat]);
        Assert.Equal(1, cat.FileCount);
        var (_, deleted, _) = await StorageService.CleanAsync([cat]);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }
}

public class ParsingTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe /background", "C:\\Tools\\tool.exe")]
    [InlineData("C:\\Program Files\\No Quotes\\run.exe -x", "C:\\Program Files\\No Quotes\\run.exe")]
    public void Extracts_executable_from_startup_command(string cmd, string exe) => Assert.Equal(exe, StartupService.ExtractExe(cmd));

    [Fact]
    public void Ping_statistics_are_computed_correctly()
    {
        var r = new PingResult { Target = "x", Sent = 4, Received = 3 };
        r.Rtts.AddRange([10u, 20u, 15u]);
        Assert.Equal(25, r.LossPct);
        Assert.Equal(15, r.Avg);
        Assert.Equal(10, r.Min);
        Assert.Equal(20, r.Max);
        Assert.Equal(7.5, r.Jitter);
    }

    [Fact]
    public void Ping_with_no_replies_reports_unavailable_not_zero()
    {
        var r = new PingResult { Target = "x", Sent = 5, Received = 0 };
        Assert.Equal(100, r.LossPct);
        Assert.True(r.Avg < 0);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(-1, "—")]
    public void Formats_bytes(double b, string expected) => Assert.Equal(expected, Fmt.Bytes(b));

    [Fact]
    public void Unavailable_percentages_are_not_shown_as_numbers() => Assert.Equal("N/A", Fmt.Pct(-1));

    [Fact]
    public void Telemetry_before_first_sample_reports_unavailable_not_zero()
    {
        // Regression: the default struct used to read as 0 °C / 0% before the first sample.
        var t = Telemetry.Instance;   // never started in the test process
        Assert.False(t.HasSample);
        var s = t.Last;
        foreach (var v in new[] { s.CpuPercent, s.GpuPercent, s.GpuTempC, s.AcpiTempC, s.GpuDedicatedUsed, s.NetRecvBps, s.NetSendBps, s.DiskReadBps, s.DiskWriteBps })
            Assert.Equal(Native.VxNative.Unavailable, v);
        Assert.All(t.CpuHistory, v => Assert.Equal(-1.0, v));
    }
}
