using System.IO;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>Boot times, before/after snapshots, driver check, seasonal themes, setup export/import, model choice.</summary>
public class FeatureTests
{
    // ───────────── Boot times

    [Fact]
    public void Boots_pair_power_on_with_the_next_sign_in_only()
    {
        var d = new DateTime(2026, 11, 10, 8, 0, 0);
        var starts = new List<DateTime> { d, d.AddDays(1), d.AddDays(2) };
        var logons = new List<DateTime>
        {
            d.AddSeconds(42),                     // boot 1 → 42 s
            d.AddSeconds(90),                     // a second sign-in the same boot is ignored
            // boot 2 has no sign-in (e.g. shut down at the lock screen) → skipped
            d.AddDays(2).AddSeconds(31),          // boot 3 → 31 s
        };
        var boots = BootTime.PairBoots(starts, logons, 10);
        Assert.Equal(2, boots.Count);
        Assert.Equal(31, boots[0].Seconds, 1);    // newest first
        Assert.Equal(42, boots[1].Seconds, 1);
        Assert.All(boots, b => Assert.False(b.Measured));
    }

    [Fact]
    public void A_sign_in_hours_later_is_not_counted_as_boot_time()
    {
        var d = new DateTime(2026, 11, 10, 8, 0, 0);
        Assert.Empty(BootTime.PairBoots([d], [d.AddHours(3)], 10));
    }

    [Fact]
    public void Reading_real_boot_history_never_throws()
    {
        var boots = BootTime.Recent(3);
        Assert.All(boots, b => Assert.InRange(b.Seconds, 0.1, 3600));
        _ = BootTime.SlowItems();
    }

    // ───────────── Graphics drivers

    [Theory]
    [InlineData("32.0.16.2124", "621.24")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.15.8180", "581.80")]
    public void Nvidia_versions_use_the_familiar_number(string raw, string expected) =>
        Assert.Equal(expected, GpuDrivers.DisplayVersion("NVIDIA", raw));

    [Fact]
    public void Other_vendors_keep_the_windows_version() =>
        Assert.Equal("31.0.24033.1003", GpuDrivers.DisplayVersion("AMD", "31.0.24033.1003"));

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2D05", "NVIDIA")]
    [InlineData(@"PCI\VEN_1002&DEV_744C", "AMD")]
    [InlineData(@"PCI\VEN_8086&DEV_A7A0", "Intel")]
    [InlineData(@"ROOT\BasicDisplay", null)]
    public void Vendors_are_recognised_from_the_device_id(string pnp, string? vendor) => Assert.Equal(vendor, GpuDrivers.VendorOf(pnp));

    [Fact]
    public void Driver_age_is_rated_honestly()
    {
        GpuDriver At(int days) => new("GPU", "NVIDIA", "", "", DateTime.Now.AddDays(-days), "");
        Assert.Equal(0, At(10).Freshness);
        Assert.Equal(1, At(200).Freshness);
        Assert.Equal(2, At(500).Freshness);
        Assert.Equal(-1, new GpuDriver("GPU", "NVIDIA", "", "", null, "").Freshness);
    }

    // ───────────── Before / after

    [Fact]
    public void Before_and_after_marks_improvements_and_waits_for_a_restart()
    {
        var boot = new DateTime(2026, 11, 15, 9, 0, 0);
        var before = new PcSnapshot { Profile = "Gaming", BackgroundProcesses = 140, StartupApps = 9, CpuPercent = 6, RamUsedGB = 9.5, LastBootAt = boot, LastBootSeconds = 38, BootMeasured = true };
        var same = before with { BackgroundProcesses = 120, StartupApps = 6, CpuPercent = 5.8, RamUsedGB = 10.4 };
        var rows = PcSnapshots.Compare(before, same).ToDictionary(r => r.Label);
        Assert.Equal(1, rows["Background processes"].Better);
        Assert.Equal(1, rows["Startup apps"].Better);
        Assert.Equal(0, rows["CPU use (1-minute average)"].Better);   // within noise
        Assert.Equal(-1, rows["Memory in use"].Better);
        Assert.Equal("after your next restart", rows["Startup time"].After);

        var afterRestart = same with { LastBootAt = boot.AddDays(1), LastBootSeconds = 30 };
        var bootRow = PcSnapshots.Compare(before, afterRestart).Single(r => r.Label == "Startup time");
        Assert.Equal("30.0 s", bootRow.After);
        Assert.Equal(1, bootRow.Better);
    }

    [Fact]
    public void Missing_readings_are_left_out_rather_than_shown_as_zero()
    {
        var rows = PcSnapshots.Compare(new PcSnapshot(), new PcSnapshot());
        Assert.Empty(rows);
    }

    [Fact]
    public void Baseline_is_saved_and_read_back()
    {
        PcSnapshots.SaveBaseline("Competitive");
        var b = PcSnapshots.Baseline();
        Assert.NotNull(b);
        Assert.Equal("Competitive", b!.Profile);
        PcSnapshots.Clear();
        Assert.Null(PcSnapshots.Baseline());
    }

    // ───────────── Seasonal themes

    [Theory]
    [InlineData(12, 27, "Christmas", false, "New Year")]
    [InlineData(1, 6, "Christmas", false, "New Year")]
    [InlineData(12, 20, "Christmas", false, null)]
    [InlineData(1, 7, "New Year", true, "Graphite")]
    [InlineData(1, 7, "New Year", false, null)]          // picked by hand: left alone
    [InlineData(12, 28, "Cherry Blossom", false, null)]   // only Christmas becomes New Year
    public void Seasonal_switch_follows_the_calendar(int month, int day, string current, bool automatic, string? expected) =>
        Assert.Equal(expected, ThemeManager.SeasonalSwitch(current, new DateTime(month == 1 ? 2027 : 2026, month, day), automatic));

    [Fact]
    public void New_year_scene_celebrates_the_coming_year()
    {
        Assert.Equal(2027, FireworksSky.CelebratedYear(new DateTime(2026, 12, 30)));
        Assert.Equal(2027, FireworksSky.CelebratedYear(new DateTime(2026, 11, 15)));
        Assert.Equal(2027, FireworksSky.CelebratedYear(new DateTime(2027, 1, 3)));
        Assert.Contains(ThemeManager.NewYear, ThemeManager.Themes);
        Assert.Equal("Gold", ThemeManager.DefaultAccent(ThemeManager.NewYear));
    }

    // ───────────── Export / import

    [Fact]
    public void Tweak_setup_round_trips_and_skips_what_this_version_does_not_know()
    {
        var path = Path.Combine(AppPaths.Root, "test-setup.vytrix.json");
        File.WriteAllText(path, """{ "format": "vytrix-tweak-setup", "version": 1, "tweaks": ["gaming.gamemode", "input.stickykeys", "made.up.tweak"], "powerPlan": "performance" }""");
        var imp = TweakSetup.Read(path);
        Assert.Contains(imp.Usable.Concat(imp.Unsupported), t => t.Id == "gaming.gamemode");
        Assert.Contains(imp.Usable.Concat(imp.Unsupported), t => t.Id == "input.stickykeys");
        Assert.Equal(["made.up.tweak"], imp.Unknown);
        Assert.Equal("performance", imp.PowerPlan);

        TweakSetup.LoadIntoCustom(imp);
        Assert.Equal(imp.Usable.Select(t => t.Id), Settings.Current.CustomProfileTweaks);

        TweakSetup.Export(path);
        var again = TweakSetup.Read(path);
        Assert.Empty(again.Unknown);
        File.Delete(path);
    }

    [Theory]
    [InlineData("""{ "hello": "world" }""")]
    [InlineData("not json at all")]
    [InlineData("""{ "format": "vytrix-tweak-setup", "version": 9, "tweaks": [] }""")]
    public void Files_that_are_not_a_setup_are_rejected_clearly(string content)
    {
        var path = Path.Combine(AppPaths.Root, "bad-setup.json");
        File.WriteAllText(path, content);
        var ex = Assert.Throws<InvalidDataException>(() => TweakSetup.Read(path));
        Assert.Contains("VYTRIX", ex.Message);
        File.Delete(path);
    }

    // ───────────── Local AI model choice

    [Theory]
    [InlineData(8L, "qwen3:8b")]
    [InlineData(12L, "qwen3:8b")]
    [InlineData(6L, "qwen3:4b")]
    [InlineData(0L, "qwen3:4b")]
    public void Model_recommendation_follows_video_memory(long gb, string expected) =>
        Assert.Equal(expected, Ollama.Recommended((ulong)gb * 1024 * 1024 * 1024 - (gb > 0 ? 200UL * 1024 * 1024 : 0)).Id);
}
