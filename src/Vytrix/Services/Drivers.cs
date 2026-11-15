using System.Management;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>A graphics card's installed driver, read from Windows (Win32_VideoController).</summary>
public sealed record GpuDriver(string Name, string Vendor, string RawVersion, string DisplayVersion, DateTime? Date, string DownloadUrl)
{
    public int? AgeDays => Date is { } d ? (int)(DateTime.Now - d).TotalDays : null;

    /// <summary>Plain-language freshness: 0 = recent, 1 = getting old, 2 = old, -1 = unknown.</summary>
    public int Freshness => AgeDays switch { null => -1, < 120 => 0, < 365 => 1, _ => 2 };

    public string AgeText => AgeDays switch
    {
        null => "release date unknown",
        < 1 => "released today",
        1 => "released yesterday",
        < 60 => $"released {AgeDays} days ago",
        < 730 => $"released about {AgeDays / 30} months ago",
        _ => $"released about {AgeDays / 365} years ago",
    };

    public string Advice => Freshness switch
    {
        0 => "Up to date enough. New drivers mainly matter for brand-new games.",
        1 => "A few months old. Worth updating, especially before playing new releases.",
        2 => "Over a year old. Updating can noticeably improve game performance and stability.",
        _ => "Check your graphics vendor's site for the latest driver.",
    };
}

public static class GpuDrivers
{
    public static List<GpuDriver> Read()
    {
        var list = new List<GpuDriver>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                var pnp = $"{mo["PNPDeviceID"]}";
                var vendor = VendorOf(pnp);
                if (vendor is null) continue;   // Microsoft Basic Display Adapter, remote/virtual adapters
                var raw = $"{mo["DriverVersion"]}".Trim();
                DateTime? date = mo["DriverDate"] is string d && d.Length > 0 ? SafeDate(d) : null;
                list.Add(new GpuDriver($"{mo["Name"]}".Trim(), vendor, raw, DisplayVersion(vendor, raw), date, DownloadUrl(vendor)));
            }
        }
        catch (Exception ex) { Log.Warn("drivers", $"Couldn't read graphics drivers: {ex.Message}"); }
        return list;
    }

    private static DateTime? SafeDate(string cim) { try { return ManagementDateTimeConverter.ToDateTime(cim); } catch { return null; } }

    internal static string? VendorOf(string pnp) =>
        pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || pnp.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase) ? "AMD"
        : pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? "Intel"
        : null;

    /// <summary>
    /// NVIDIA's familiar number is the last five digits of Windows' version: 32.0.16.2124 → 621.24.
    /// Other vendors use their own scheme, so Windows' version is shown as is.
    /// </summary>
    internal static string DisplayVersion(string vendor, string raw)
    {
        if (vendor != "NVIDIA") return raw;
        var parts = raw.Split('.');
        if (parts.Length < 4) return raw;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return raw;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }

    internal static string DownloadUrl(string vendor) => vendor switch
    {
        "NVIDIA" => "https://www.nvidia.com/en-us/drivers/",
        "AMD" => "https://www.amd.com/en/support/download/drivers.html",
        _ => "https://www.intel.com/content/www/us/en/support/detect.html",
    };
}
