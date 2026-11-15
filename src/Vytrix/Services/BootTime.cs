using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>
/// One boot. With administrator rights it comes from Windows' own boot measurement
/// (Diagnostics-Performance event 100); otherwise from the System log as power-on to sign-in.
/// </summary>
public sealed record BootRecord(DateTime When, double Seconds, double? DesktopReadySeconds, double? BackgroundSeconds, bool Measured);

/// <summary>Something Windows flagged as slowing a boot (events 101-103: app, driver, service).</summary>
public sealed record SlowBootItem(DateTime When, string Kind, string Name, double DelaySeconds);

public static class BootTime
{
    private const string DiagLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>Recent boots, newest first. Measured when Windows' protected log is readable (administrator mode).</summary>
    public static List<BootRecord> Recent(int max = 10)
    {
        try { return Measured(max); }
        catch (UnauthorizedAccessException) { }
        catch (EventLogException ex) { Log.Warn("boot", $"Boot log unavailable: {ex.Message}"); }
        try { return FromSystemLog(max); }
        catch (Exception ex) { Log.Warn("boot", $"System log unavailable: {ex.Message}"); return []; }
    }

    /// <summary>Whether Windows' measured boot times are readable right now.</summary>
    public static bool CanReadMeasured()
    {
        try { using var r = new EventLogReader(new EventLogQuery(DiagLog, PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true }); r.ReadEvent()?.Dispose(); return true; }
        catch { return false; }
    }

    private static List<BootRecord> Measured(int max)
    {
        var list = new List<BootRecord>();
        using var reader = new EventLogReader(new EventLogQuery(DiagLog, PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true });
        for (EventRecord? e = reader.ReadEvent(); e is not null && list.Count < max; e = reader.ReadEvent())
            using (e)
            {
                var d = Data(e);
                if (!double.TryParse(d.GetValueOrDefault("BootTime"), out var total) || total <= 0) continue;
                double? main = double.TryParse(d.GetValueOrDefault("MainPathBootTime"), out var m) ? m / 1000 : null;
                double? post = double.TryParse(d.GetValueOrDefault("BootPostBootTime"), out var p) ? p / 1000 : null;
                list.Add(new BootRecord(e.TimeCreated ?? DateTime.MinValue, total / 1000, main, post, true));
            }
        return list;
    }

    /// <summary>Power-on (Kernel-General 12) to the next user sign-in (Winlogon 7001). Includes the time spent at the sign-in screen.</summary>
    internal static List<BootRecord> FromSystemLog(int max)
    {
        var starts = Times("*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]", max + 2);
        var logons = Times("*[System[Provider[@Name='Microsoft-Windows-Winlogon'] and EventID=7001]]", (max + 2) * 3);
        return PairBoots(starts, logons, max);
    }

    /// <summary>Pairs each power-on with the first sign-in after it and before the next power-on.</summary>
    internal static List<BootRecord> PairBoots(List<DateTime> starts, List<DateTime> logons, int max)
    {
        var s = starts.OrderBy(t => t).ToList();
        var result = new List<BootRecord>();
        for (int i = 0; i < s.Count; i++)
        {
            var next = i + 1 < s.Count ? s[i + 1] : DateTime.MaxValue;
            var logon = logons.Where(l => l > s[i] && l < next).DefaultIfEmpty(DateTime.MinValue).Min();
            if (logon == DateTime.MinValue) continue;
            var secs = (logon - s[i]).TotalSeconds;
            if (secs is > 0 and < 3600) result.Add(new BootRecord(s[i], secs, null, null, false));
        }
        return result.OrderByDescending(r => r.When).Take(max).ToList();
    }

    /// <summary>Apps, drivers and services Windows flagged as slowing recent boots (administrator mode only).</summary>
    public static List<SlowBootItem> SlowItems(int max = 8)
    {
        var list = new List<SlowBootItem>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(DiagLog, PathType.LogName, "*[System[(EventID=101 or EventID=102 or EventID=103)]]") { ReverseDirection = true });
            for (EventRecord? e = reader.ReadEvent(); e is not null && list.Count < 40; e = reader.ReadEvent())
                using (e)
                {
                    var d = Data(e);
                    var name = FirstNonEmpty(d.GetValueOrDefault("FriendlyName"), d.GetValueOrDefault("Name"));
                    if (name is null || !double.TryParse(d.GetValueOrDefault("DegradationTime"), out var ms) || ms <= 0) continue;
                    var kind = e.Id switch { 101 => "App", 102 => "Driver", _ => "Service" };
                    list.Add(new SlowBootItem(e.TimeCreated ?? DateTime.MinValue, kind, name, ms / 1000));
                }
        }
        catch { return []; }
        // Most recent report per item, biggest delays first.
        return list.GroupBy(i => (i.Kind, i.Name)).Select(g => g.OrderByDescending(x => x.When).First())
            .OrderByDescending(i => i.DelaySeconds).Take(max).ToList();
    }

    private static string? FirstNonEmpty(params string?[] s) => s.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static List<DateTime> Times(string xpath, int max)
    {
        var list = new List<DateTime>();
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
        for (EventRecord? e = reader.ReadEvent(); e is not null && list.Count < max; e = reader.ReadEvent())
            using (e) if (e.TimeCreated is { } t) list.Add(t);
        return list;
    }

    private static Dictionary<string, string> Data(EventRecord e)
    {
        var x = XDocument.Parse(e.ToXml());
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        return x.Descendants(ns + "Data").Where(d => d.Attribute("Name") is not null)
            .GroupBy(d => d.Attribute("Name")!.Value).ToDictionary(g => g.Key, g => g.First().Value);
    }
}
