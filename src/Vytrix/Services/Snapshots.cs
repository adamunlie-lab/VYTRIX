using System.IO;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>A few honest, comparable measurements of this PC at one moment.</summary>
public sealed record PcSnapshot
{
    public DateTime At { get; init; }
    public string Profile { get; init; } = "";
    public int BackgroundProcesses { get; init; } = -1;
    public int TotalProcesses { get; init; } = -1;
    public int StartupApps { get; init; } = -1;
    /// <summary>Average CPU use over the last minute of live readings (-1 when there aren't enough).</summary>
    public double CpuPercent { get; init; } = -1;
    public double RamUsedGB { get; init; } = -1;
    public DateTime? LastBootAt { get; init; }
    public double? LastBootSeconds { get; init; }
    public bool BootMeasured { get; init; }
}

/// <summary>One line of the before/after comparison.</summary>
public sealed record SnapshotRow(string Label, string Before, string After, int Better, string? Note = null);

/// <summary>Takes a baseline before a profile is applied, so the Optimization page can show what changed.</summary>
public static class PcSnapshots
{
    private static string BaselineFile => Path.Combine(AppPaths.Root, "baseline.json");

    public static PcSnapshot Capture(string profile = "")
    {
        var t = Telemetry.Instance;
        double cpu = -1;
        lock (t.CpuHistory)
        {
            var recent = t.CpuHistory.Skip(Math.Max(0, t.CpuHistory.Length - 60)).Where(v => v >= 0).ToList();
            if (recent.Count >= 10) cpu = recent.Average();
        }
        var m = t.Memory;
        var boot = SafeBoot();
        return new PcSnapshot
        {
            At = DateTime.Now, Profile = profile,
            BackgroundProcesses = t.TotalProcesses > 0 ? t.BackgroundProcesses : -1,
            TotalProcesses = t.TotalProcesses > 0 ? t.TotalProcesses : -1,
            StartupApps = SafeStartup(),
            CpuPercent = cpu,
            RamUsedGB = m.TotalPhys > 0 ? (m.TotalPhys - m.AvailPhys) / 1073741824.0 : -1,
            LastBootAt = boot?.When, LastBootSeconds = boot?.Seconds, BootMeasured = boot?.Measured ?? false,
        };
    }

    private static int SafeStartup() { try { return StartupService.EnabledCount(); } catch { return -1; } }
    private static BootRecord? SafeBoot() { try { return BootTime.Recent(1).FirstOrDefault(); } catch { return null; } }

    public static void SaveBaseline(string profile)
    {
        try { Json.Save(BaselineFile, Capture(profile)); Log.Info("snapshot", $"Saved a before-snapshot for the {profile} profile."); }
        catch (Exception ex) { Log.Warn("snapshot", $"Could not save the before-snapshot: {ex.Message}"); }
    }

    public static PcSnapshot? Baseline() => File.Exists(BaselineFile) ? Json.Load<PcSnapshot?>(BaselineFile, () => null) : null;

    public static void Clear() { try { File.Delete(BaselineFile); } catch { } }

    /// <summary>Before/after rows. Better: 1 = improved, -1 = worse, 0 = about the same or not comparable.</summary>
    public static List<SnapshotRow> Compare(PcSnapshot before, PcSnapshot now)
    {
        var rows = new List<SnapshotRow>();
        static int Dir(double b, double a, double tolerance) => Math.Abs(a - b) <= tolerance ? 0 : a < b ? 1 : -1;

        if (before.BackgroundProcesses >= 0 && now.BackgroundProcesses >= 0)
            rows.Add(new("Background processes", before.BackgroundProcesses.ToString(), now.BackgroundProcesses.ToString(), Dir(before.BackgroundProcesses, now.BackgroundProcesses, 3)));
        if (before.StartupApps >= 0 && now.StartupApps >= 0)
            rows.Add(new("Startup apps", before.StartupApps.ToString(), now.StartupApps.ToString(), Dir(before.StartupApps, now.StartupApps, 0)));
        if (before.CpuPercent >= 0 && now.CpuPercent >= 0)
            rows.Add(new("CPU use (1-minute average)", $"{before.CpuPercent:0.0}%", $"{now.CpuPercent:0.0}%", Dir(before.CpuPercent, now.CpuPercent, 1.5), "Depends on what's open at the time."));
        if (before.RamUsedGB >= 0 && now.RamUsedGB >= 0)
            rows.Add(new("Memory in use", $"{before.RamUsedGB:0.0} GB", $"{now.RamUsedGB:0.0} GB", Dir(before.RamUsedGB, now.RamUsedGB, 0.3), "Depends on what's open at the time."));
        if (before.LastBootSeconds is { } bb)
        {
            bool newBoot = now.LastBootAt is { } a && before.LastBootAt is { } b && a > b.AddMinutes(1) && now.LastBootSeconds is not null;
            rows.Add(newBoot
                ? new("Startup time", $"{bb:0.0} s", $"{now.LastBootSeconds:0.0} s", before.BootMeasured == now.BootMeasured ? Dir(bb, now.LastBootSeconds!.Value, 1) : 0,
                    now.BootMeasured ? null : "Power-on to sign-in, including the sign-in screen.")
                : new("Startup time", $"{bb:0.0} s", "after your next restart", 0));
        }
        return rows;
    }
}
