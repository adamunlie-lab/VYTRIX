using System.Diagnostics;
using Vytrix.Native;
using Xunit;

namespace Vytrix.Tests;

/// <summary>Integration tests against the real C++ core on this machine.</summary>
public class NativeCoreTests
{
    static NativeCoreTests() => VxNative.vx_init();

    [Fact]
    public void Cpu_info_is_real()
    {
        Assert.Equal(1, VxNative.vx_get_cpu_info(out var cpu));
        Assert.False(string.IsNullOrWhiteSpace(cpu.Brand));
        Assert.True(cpu.PhysicalCores >= 1);
        Assert.True(cpu.LogicalProcessors >= cpu.PhysicalCores);
        Assert.Equal(Environment.ProcessorCount, (int)cpu.LogicalProcessors);
    }

    [Fact]
    public void Memory_matches_windows()
    {
        Assert.Equal(1, VxNative.vx_get_memory(out var m));
        Assert.True(m.TotalPhys > 1UL << 30);
        Assert.True(m.AvailPhys < m.TotalPhys);
        Assert.True(m.CommitLimit >= m.CommitTotal);
        Assert.True(m.ProcessCount > 10);
    }

    [Fact]
    public void Samples_are_in_range_or_explicitly_unavailable()
    {
        VxNative.vx_sample(out _);
        Thread.Sleep(1100);
        Assert.Equal(1, VxNative.vx_sample(out var s));
        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.True(s.GpuPercent == VxNative.Unavailable || s.GpuPercent is >= 0 and <= 100);
        Assert.True(s.GpuTempC == VxNative.Unavailable || s.GpuTempC is > 0 and < 120);
        Assert.True(s.AcpiTempC == VxNative.Unavailable || s.AcpiTempC is > 0 and < 125);
        Assert.True(s.NetRecvBps >= 0 || s.NetRecvBps == VxNative.Unavailable);
    }

    [Fact]
    public void Process_list_includes_this_process_with_memory()
    {
        var procs = VxNative.EnumProcesses();
        var me = procs.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        Assert.Equal((uint)Environment.ProcessId, me.Pid);
        Assert.True(me.WorkingSet > 0);
        Assert.True(procs.Length > 20);
    }

    [Fact]
    public void Power_schemes_have_exactly_one_active()
    {
        var plans = VxNative.EnumPowerSchemes();
        Assert.NotEmpty(plans);
        Assert.Single(plans, p => p.IsActive == 1);
        Assert.All(plans, p => Assert.Equal(36, p.Guid.Length));
    }

    [Fact]
    public void Minimum_processor_state_reads_a_valid_percentage()
    {
        // Read-only: never writes the user's power plan.
        Assert.Equal(1, VxNative.vx_power_read_ac("54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", out var v));
        Assert.InRange(v, 0u, 100u);
    }

    [Fact]
    public void Power_setting_with_bad_guid_fails_honestly() =>
        Assert.Equal(0, VxNative.vx_power_write_ac("bad", "guid", 50));

    [Fact]
    public void Setting_an_invalid_power_scheme_fails_honestly() => Assert.Equal(0, VxNative.vx_set_power_scheme("not-a-guid"));

    [Fact]
    public void Loopback_ping_succeeds()
    {
        Assert.Equal(0, VxNative.vx_ping("127.0.0.1", 1000, out var rtt));
        Assert.True(rtt < 100);
    }

    [Fact]
    public void Unresolvable_host_reports_resolve_failure() =>
        Assert.Equal(2, VxNative.vx_ping("no-such-host.invalid", 500, out _));

    [Fact]
    public void Gpus_enumerate_with_names()
    {
        var gpus = VxNative.EnumGpus();
        Assert.NotEmpty(gpus);   // at least the Microsoft Basic Render Driver
        Assert.All(gpus, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
    }

    [Fact]
    public void Sampling_is_cheap()
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) VxNative.vx_sample(out _);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"20 samples took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Repeated_process_enumeration_does_not_leak()
    {
        for (int i = 0; i < 5; i++) VxNative.EnumProcesses();
        GC.Collect(); GC.WaitForPendingFinalizers();
        var before = Process.GetCurrentProcess().HandleCount;
        for (int i = 0; i < 50; i++) VxNative.EnumProcesses();
        GC.Collect(); GC.WaitForPendingFinalizers();
        var after = Process.GetCurrentProcess().HandleCount;
        Assert.True(after - before < 20, $"Handle count grew by {after - before}");
    }
}
